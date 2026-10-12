using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrintService.Core.Models;
using PrintService.Infrastructure;
using PrintService.Infrastructure.Services;
using SkiaSharp;
using Xunit;

namespace PrintService.Tests;

public sealed class PrintingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrintService.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<PrintJobStore> _stores = [];
    private static PrintJobRequest Job() => new() { DocumentType = PrintDocumentType.Text,
        ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("فاتورة اختبار\nTotal: 15")) };
    private WindowsPrinterService Service(FakeBackend backend, PrintingOptions? options = null, FakeCatalog? catalog = null)
    {
        options ??= new() { DataDirectory = _root };
        var store = new PrintJobStore(options); _stores.Add(store);
        return new(NullLogger<WindowsPrinterService>.Instance, catalog ?? new(), backend, store, options);
    }

    [Fact]
    public async Task DuplicateAfterCompletionAndRestartDoesNotPrintAgain()
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job();
        var first = await service.PrintAsync(job); var duplicate = await service.PrintAsync(job);
        Assert.True(first.Success); Assert.Equal(first.SpoolerJobIds, duplicate.SpoolerJobIds); Assert.Equal(1, backend.Calls);
        _stores[0].Dispose();
        Assert.True((await Service(backend).PrintAsync(job)).Success); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task ConcurrentDuplicatesShareOneSubmission()
    {
        var backend = new FakeBackend { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Service(backend); var job = Job(); var first = service.PrintAsync(job);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.PrintAsync(job); backend.Release.SetResult();
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.Success)); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task DisconnectDoesNotCancelAcceptedJob()
    {
        var backend = new FakeBackend { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Service(backend); using var disconnect = new CancellationTokenSource(); var job = Job();
        var request = service.PrintAsync(job, disconnect.Token);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); disconnect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        backend.Release.SetResult(); Assert.True((await service.PrintAsync(job)).Success); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task ChangedContentWithSameIdIsConflict()
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job(); await service.PrintAsync(job);
        job.ContentBase64 = Convert.ToBase64String("different"u8);
        Assert.Equal("Conflict", (await service.PrintAsync(job)).Status); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task SystemNamespacesAreIndependentAndPersistAcrossRestart()
    {
        var backend = new FakeBackend(); var service = Service(backend);
        var restaurant = Job(); restaurant.JobId = "invoice:123"; restaurant.SystemId = "Restaurant";
        var accounting = Job(); accounting.JobId = restaurant.JobId; accounting.SystemId = "accounting";
        var legacy = Job(); legacy.JobId = "restaurant:invoice:123";
        var results = await Task.WhenAll(service.PrintAsync(restaurant), service.PrintAsync(accounting), service.PrintAsync(legacy));
        Assert.All(results, r => Assert.True(r.Success)); Assert.Equal(3, backend.Calls);
        Assert.Equal("restaurant", results[0].SystemId);
        Assert.Equal("accounting", (await service.GetJobAsync(accounting.JobId, "ACCOUNTING"))!.SystemId);
        Assert.Null(await service.GetJobAsync(restaurant.JobId));
        Assert.Null(await service.GetJobAsync(restaurant.JobId, "other"));
        _stores[0].Dispose(); service = Service(backend);
        Assert.True((await service.PrintAsync(restaurant)).Success);
        Assert.True((await service.PrintAsync(accounting)).Success); Assert.Equal(3, backend.Calls);
    }

    [Theory]
    [InlineData("bad:scope")] [InlineData("bad/scope")] [InlineData("نظام")]
    public async Task InvalidSystemIdIsRejectedWithoutPrinting(string scope)
    {
        var backend = new FakeBackend(); var job = Job(); job.SystemId = scope;
        Assert.Equal("ValidationFailed", (await Service(backend).PrintAsync(job)).Status); Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task PageChangesConflictButEmptyPageIsCompatibleWithLegacyJob()
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job();
        await service.PrintAsync(job); job.Page = new();
        Assert.True((await service.PrintAsync(job)).Success);
        job.Page = new() { PaperName = "A4" };
        Assert.Equal("Conflict", (await service.PrintAsync(job)).Status); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task PageAndSystemAreDetachedBeforeQueueing()
    {
        var backend = new FakeBackend { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Service(backend); var job = Job(); job.SystemId = "restaurant";
        job.Page = new() { PaperWidthMm = 80, PaperHeightMm = 200, Margins = new() { LeftMm = 2 } };
        var first = service.PrintAsync(job); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        job.Page = new() { PaperName = "A3" }; job.SystemId = "other";
        backend.Release.SetResult(); var result = await first;
        Assert.True(result.Success); Assert.Equal("restaurant", result.SystemId);
        Assert.Equal(80, backend.LastRequest!.Page!.PaperWidthMm);
        Assert.Equal(2, backend.LastRequest.Page.Margins!.LeftMm);
    }

    [Fact]
    public async Task InvalidPageOptionsNeverReachBackend()
    {
        PrintPageOptions[] invalid = [new() { PaperWidthMm = 80 }, new() { PaperWidthMm = 0, PaperHeightMm = 200 },
            new() { PaperWidthMm = double.NaN, PaperHeightMm = 200 }, new() { PaperName = "A4", PaperWidthMm = 80, PaperHeightMm = 200 },
            new() { Margins = new() { TopMm = -1 } }, new() { Margins = new() { LeftMm = double.PositiveInfinity } },
            new() { PaperWidthMm = 80, PaperHeightMm = 200, Margins = new() { LeftMm = 50, RightMm = 50 } }];
        var backend = new FakeBackend(); var service = Service(backend);
        foreach (var page in invalid)
        {
            var job = Job(); job.Page = page;
            Assert.Equal("ValidationFailed", (await service.PrintAsync(job)).Status);
        }
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public void PdfAndRawDoNotSilentlyIgnoreUnsupportedLayout()
    {
        Assert.NotNull(new PrintPageOptions { Margins = new() { LeftMm = 2 } }.Validate(PrintDocumentType.RawEscPos));
        Assert.NotNull(new PrintPageOptions { Margins = new() { LeftMm = 2 } }.Validate(PrintDocumentType.Pdf));
        Assert.NotNull(new PrintPageOptions { Landscape = true }.Validate(PrintDocumentType.Pdf));
        Assert.Null(new PrintPageOptions { PaperName = "A4" }.Validate(PrintDocumentType.Pdf));
        Assert.Equal("1x,ignore-pdf-print-settings,paper=a4", WindowsPrintBackend.PdfPrintSettings(new() { PaperName = "A4" }));
        Assert.Equal("1x,ignore-pdf-print-settings", WindowsPrintBackend.PdfPrintSettings(null));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(11)] [InlineData(int.MaxValue)]
    public async Task CopiesAreValidatedBeforeSubmission(int copies)
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job(); job.Copies = copies;
        Assert.Equal("ValidationFailed", (await service.PrintAsync(job)).Status); Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(99)]
    public async Task UnknownTypesCannotFallThroughToRaw(int type)
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job(); job.DocumentType = (PrintDocumentType)type;
        Assert.False((await service.PrintAsync(job)).Success); Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [InlineData("")] [InlineData("not-base64!")] [InlineData("    ")] [InlineData("/w==")]
    public async Task InvalidContentIsRejected(string content)
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job(); job.ContentBase64 = content;
        Assert.Equal("ValidationFailed", (await service.PrintAsync(job)).Status); Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task PdfCannotSilentlyBecomeText()
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job(); job.DocumentType = PrintDocumentType.Pdf;
        Assert.False((await service.PrintAsync(job)).Success); Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task DocumentSizeAndImageDimensionsAreBounded()
    {
        var backend = new FakeBackend(); var service = Service(backend, new() { DataDirectory = _root, MaxDocumentBytes = 4 });
        Assert.False((await service.PrintAsync(Job())).Success); _stores[0].Dispose();
        service = Service(backend, new() { DataDirectory = _root, MaxImagePixels = 1 });
        using var image = new SKBitmap(2, 2); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var job = Job(); job.DocumentType = PrintDocumentType.Image; job.ContentBase64 = Convert.ToBase64String(encoded.ToArray());
        Assert.False((await service.PrintAsync(job)).Success); Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task MissingDefaultDoesNotSelectArbitraryPrinter()
    {
        var backend = new FakeBackend(); var service = Service(backend, catalog: new() { HasDefault = false });
        Assert.Null(await service.GetDefaultPrinterAsync());
        Assert.Equal("Failed", (await service.PrintAsync(Job())).Status); Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task PartialFailureIsTrackedAndNeverReplayed()
    {
        var backend = new FakeBackend { FailOnCall = 2 }; var service = Service(backend); var job = Job(); job.Copies = 3;
        var result = await service.PrintAsync(job); Assert.Equal("Unknown", result.Status);
        Assert.Equal(1, result.CompletedCopies); Assert.Single(result.SpoolerJobIds);
        await service.PrintAsync(job); Assert.Equal(2, backend.Calls);
    }

    [Fact]
    public async Task UncertainOutputRetainsKnownWindowsJobId()
    {
        var backend = new FakeBackend { FailOnCall = 1, UncertainOutput = true };
        var result = await Service(backend).PrintAsync(Job());
        Assert.Equal("Unknown", result.Status); Assert.Contains(42u, result.SpoolerJobIds);
        Assert.Equal(0, result.CompletedCopies);
    }

    [Fact]
    public async Task UnfinishedJobAfterRestartBecomesUnknownWithoutSubmission()
    {
        var backend = new FakeBackend(); var service = Service(backend); var job = Job(); var result = await service.PrintAsync(job);
        var entry = _stores[0].Read(job.JobId)!; result.Status = "Processing"; _stores[0].Write(entry.Fingerprint, result);
        _stores[0].Dispose(); var restarted = Service(backend);
        Assert.Equal("Unknown", (await restarted.GetJobAsync(job.JobId))!.Status);
        Assert.Equal("Unknown", (await restarted.PrintAsync(job)).Status); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task QueueLimitRejectsNewJobsButAllowsDuplicates()
    {
        var backend = new FakeBackend { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Service(backend, new() { DataDirectory = _root, MaxQueuedJobs = 1 }); var job = Job();
        var first = service.PrintAsync(job); await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Busy", (await service.PrintAsync(Job())).Status);
        var replay = service.PrintAsync(job); backend.Release.SetResult(); await Task.WhenAll(first, replay); Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task DifferentJobsOnSamePrinterNeverOverlap()
    {
        var backend = new FakeBackend { Delay = 40 }; var service = Service(backend);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.PrintAsync(Job())));
        Assert.Equal(8, backend.Calls); Assert.Equal(1, backend.MaxActive);
    }

    [Fact]
    public void StatusDistinguishesOfflinePaperOutAndBusy()
    {
        Assert.False(WindowsPrinterCatalog.DescribeStatus(128).Online);
        Assert.False(WindowsPrinterCatalog.DescribeStatus(16).Ready); Assert.True(WindowsPrinterCatalog.DescribeStatus(16).Online);
        Assert.True(WindowsPrinterCatalog.DescribeStatus(512).Ready); Assert.False(WindowsPrinterCatalog.DescribeStatus(0, true).Ready);
    }

    [Fact]
    public void PaperNamesStopAtFirstNullInsteadOfExposingBufferPadding()
    {
        Assert.Equal("Custom Size", WindowsPrinterCatalog.DecodePaperName("Custom Size\0uninitialized data\0"));
    }

    [Fact]
    public void LayoutPreservesImageRatioAndUnicodeText()
    {
        Assert.Equal((100, 50), DocumentLayout.FitImage(400, 200, 100, 100));
        Assert.Equal((50, 100), DocumentLayout.FitImage(200, 400, 100, 100)); string text = "عَربي 😀 longword";
        Assert.Equal((100, 100), DocumentLayout.FitImageAtDpi(400, 200, 100, 100, 300, 600));
        var lines = DocumentLayout.WrapText(text, 4, value => System.Globalization.StringInfo.ParseCombiningCharacters(value).Length);
        Assert.Equal(text, string.Concat(lines)); Assert.All(lines, line => Assert.False(char.IsHighSurrogate(line[^1])));
        Assert.True(DocumentLayout.IsRightToLeft("123 فاتورة")); Assert.False(DocumentLayout.IsRightToLeft("Invoice فاتورة"));
        Assert.Throws<InvalidOperationException>(() => DocumentLayout.WrapText("A", 0.5f, _ => 1));
    }

    [Fact]
    public void JournalHasExclusiveProcessLease()
    {
        var options = new PrintingOptions { DataDirectory = _root }; using var first = new PrintJobStore(options);
        Assert.Throws<IOException>(() => new PrintJobStore(options));
    }

    [Fact]
    public async Task MissingPdfEngineIsExplicitFailure()
    {
        var backend = new WindowsPrintBackend(new() { DataDirectory = _root, PdfExecutablePath = Path.Combine(_root, "missing.exe") });
        Assert.Null(backend.FindPdfExecutable()); var job = Job(); job.DocumentType = PrintDocumentType.Pdf;
        await Assert.ThrowsAsync<PrintPreparationException>(() => backend.SubmitAsync(job, "%PDF-1.7"u8.ToArray(), "Test", default));
    }

    [Theory]
    [InlineData("Success")] [InlineData("Fail")] [InlineData("Timeout")]
    public async Task PdfProcessExitAndTimeoutAreCheckedBeforeDeletingInput(string behavior)
    {
        var backend = new WindowsPrintBackend(new()
        {
            DataDirectory = _root, PdfTimeoutSeconds = 1,
            PdfExecutablePath = Path.Combine(AppContext.BaseDirectory, "PrintService.Tests.exe")
        });
        var job = Job(); job.DocumentType = PrintDocumentType.Pdf;
        var task = backend.SubmitAsync(job, "%PDF-1.7"u8.ToArray(), behavior, default);
        if (behavior == "Fail") await Assert.ThrowsAsync<IOException>(() => task);
        else if (behavior == "Timeout") await Assert.ThrowsAsync<TimeoutException>(() => task);
        else await task;
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "temporary")));
    }

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PrintService.Tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeCatalog : IPrinterCatalog
    {
        public bool HasDefault = true;
        public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PrinterInfo>>(
            [new() { Name = "Test", IsDefault = HasDefault, IsOnline = true, IsReady = true, StatusKnown = true }]);
    }

    private sealed class FakeBackend : IPrintBackend
    {
        public int Calls, MaxActive, FailOnCall, Delay; private int _active;
        public bool UncertainOutput;
        public PrintJobRequest? LastRequest;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public async Task<BackendPrintResult> SubmitAsync(PrintJobRequest request, byte[] content, string printerName, CancellationToken ct)
        {
            LastRequest = request;
            int call = Interlocked.Increment(ref Calls); int active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active); Started.TrySetResult();
            try
            {
                if (Release is not null) await Release.Task.WaitAsync(ct); if (Delay > 0) await Task.Delay(Delay, ct);
                if (call == FailOnCall)
                {
                    if (UncertainOutput) throw new PrintSubmissionException("Output uncertain", 42, new IOException());
                    throw new IOException("Simulated driver failure");
                }
                return new((uint)call);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
