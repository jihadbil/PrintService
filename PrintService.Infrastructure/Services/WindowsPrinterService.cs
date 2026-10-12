using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintService.Core.Interfaces;
using PrintService.Core.Models;
using SkiaSharp;

namespace PrintService.Infrastructure.Services;

/// <summary>Validates, journals and serializes print jobs independently of the HTTP connection.</summary>
public sealed class WindowsPrinterService : IPrinterService
{
    private readonly ILogger<WindowsPrinterService> _logger;
    private readonly IPrinterCatalog _catalog;
    private readonly IPrintBackend _backend;
    private readonly PrintJobStore _store;
    private readonly PrintingOptions _options;
    private readonly CancellationToken _shutdown;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _workers;
    private readonly Dictionary<string, (string Fingerprint, Task<PrintJobResult> Task)> _active = [];
    private readonly Dictionary<string, SemaphoreSlim> _printerLocks = new(StringComparer.OrdinalIgnoreCase);

    public WindowsPrinterService(ILogger<WindowsPrinterService> logger, IPrinterCatalog catalog,
        IPrintBackend backend, PrintJobStore store, PrintingOptions options, CancellationToken shutdown = default)
    {
        options.Validate();
        (_logger, _catalog, _backend, _store, _options, _shutdown) = (logger, catalog, backend, store, options, shutdown);
        _workers = new(options.MaxConcurrentPrinters);
    }

    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        _catalog.GetPrintersAsync(cancellationToken);

    public async Task DrainAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Task[] tasks;
        try { tasks = _active.Values.Select(value => (Task)value.Task).ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(tasks).WaitAsync(ct);
    }

    public async Task<PrinterInfo?> GetDefaultPrinterAsync(CancellationToken cancellationToken = default) =>
        (await GetPrintersAsync(cancellationToken)).FirstOrDefault(p => p.IsDefault);

    public Task<PrintJobResult?> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        GetJobAsync(jobId, null, cancellationToken);

    public async Task<PrintJobResult?> GetJobAsync(string jobId, string? systemId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entry = _store.Read(jobId, systemId);
            if (entry is null) return null;
            return Recover(entry.Result, _active.ContainsKey(PrintJobIdentity.StorageKey(jobId, systemId)));
        }
        finally { _gate.Release(); }
    }

    public async Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) return PrintJobResult.Fail("", null, "طلب الطباعة غير صالح.");
        _shutdown.ThrowIfCancellationRequested();
        // Detach mutable caller data before queueing.
        var job = new PrintJobRequest
        {
            JobId = request.JobId, PrinterName = request.PrinterName?.Trim(), DocumentType = request.DocumentType,
            ContentBase64 = request.ContentBase64, Copies = request.Copies, JobTitle = request.JobTitle,
            SystemId = PrintJobIdentity.NormalizeSystemId(request.SystemId), Page = request.Page?.Normalize()
        };
        var validation = Validate(job, out byte[] content);
        if (validation is not null)
        {
            var invalid = PrintJobResult.Fail(job.JobId ?? "", job.PrinterName, validation);
            invalid.SystemId = job.SystemId;
            return invalid;
        }
        var fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Printer = job.PrinterName?.ToUpperInvariant(), job.DocumentType, job.Copies, job.JobTitle,
            Content = Convert.ToHexString(SHA256.HashData(content))
        });
        // Preserve fingerprints of existing requests without layout overrides.
        if (job.Page is not null)
            fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(new { Request = Convert.ToHexString(SHA256.HashData(fingerprintBytes)), job.Page });
        string fingerprint = Convert.ToHexString(SHA256.HashData(fingerprintBytes));
        string key = PrintJobIdentity.StorageKey(job.JobId, job.SystemId);
        Task<PrintJobResult> task;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active.TryGetValue(key, out var active))
            {
                if (active.Fingerprint != fingerprint) return Conflict(job);
                task = active.Task;
            }
            else
            {
                var existing = _store.Read(job.JobId, job.SystemId);
                if (existing is not null)
                    return existing.Fingerprint == fingerprint ? Recover(existing.Result, false) : Conflict(job);
                if (_active.Count >= _options.MaxQueuedJobs)
                {
                    var busy = PrintJobResult.Fail(job.JobId, job.PrinterName, "قائمة الطباعة ممتلئة؛ حاول لاحقاً بالمعرف نفسه.");
                    busy.Status = "Busy";
                    busy.SystemId = job.SystemId;
                    return busy;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = PrintJobResult.Fail(job.JobId, job.PrinterName, "المهمة في قائمة الانتظار.");
                result.Status = "Queued";
                result.SystemId = job.SystemId;
                _store.Write(fingerprint, result);
                task = Task.Run(() => ExecuteAsync(job, content, fingerprint, result));
                _active.Add(key, (fingerprint, task));
            }
        }
        finally { _gate.Release(); }
        // A disconnected caller stops waiting; an accepted job is never silently resubmitted.
        return await task.WaitAsync(cancellationToken);
    }

    private async Task<PrintJobResult> ExecuteAsync(PrintJobRequest job, byte[] content,
        string fingerprint, PrintJobResult result)
    {
        SemaphoreSlim? printerLock = null;
        bool printerAcquired = false, workerAcquired = false, submissionStarted = false;
        try
        {
            var printers = await _catalog.GetPrintersAsync(_shutdown);
            var printer = string.IsNullOrWhiteSpace(job.PrinterName)
                ? printers.FirstOrDefault(p => p.IsDefault)
                : printers.FirstOrDefault(p => string.Equals(p.Name, job.PrinterName, StringComparison.OrdinalIgnoreCase));
            if (printer is null) throw new InvalidOperationException("الطابعة المطلوبة أو الافتراضية غير موجودة.");
            result.PrinterName = printer.Name;
            if (printer.StatusKnown && !printer.IsReady)
                throw new InvalidOperationException($"الطابعة غير جاهزة: {printer.Status}");
            await _gate.WaitAsync(_shutdown);
            try
            {
                if (!_printerLocks.TryGetValue(printer.Name, out printerLock))
                    _printerLocks.Add(printer.Name, printerLock = new SemaphoreSlim(1));
            }
            finally { _gate.Release(); }
            // Wait for this printer before occupying a global worker slot.
            await printerLock.WaitAsync(_shutdown);
            printerAcquired = true;
            await _workers.WaitAsync(_shutdown);
            workerAcquired = true;
            result.Status = "Processing";
            result.Message = "جاري إرسال المستند إلى مخزن الطباعة.";
            await SaveAsync(fingerprint, result);
            for (int i = 0; i < job.Copies; i++)
            {
                _shutdown.ThrowIfCancellationRequested();
                submissionStarted = true;
                var submitted = await _backend.SubmitAsync(job, content, printer.Name, _shutdown);
                result.CompletedCopies++;
                if (submitted.SpoolerJobId is uint id) result.SpoolerJobIds.Add(id);
                if (submitted.OutputFilePath is string file) result.OutputFiles.Add(file);
                await SaveAsync(fingerprint, result);
            }
            result.Success = true;
            result.Status = "Submitted";
            result.Message = $"تم إرسال {result.CompletedCopies} نسخة إلى مخزن الطباعة؛ لا يعني ذلك اكتمال الطباعة على الورق.";
        }
        catch (Exception ex)
        {
            if (ex is PrintSubmissionException { SpoolerJobId: uint id }) result.SpoolerJobIds.Add(id);
            result.Success = false;
            // Once the driver is called, failure/timeout may still have produced output.
            result.Status = submissionStarted && (ex is not PrintPreparationException || result.CompletedCopies > 0)
                ? "Unknown" : "Failed";
            result.Message = $"تعذر إكمال المهمة: {ex.Message}. النسخ المؤكد إرسالها: {result.CompletedCopies}. لن تعاد تلقائياً.";
            _logger.LogError(ex, "Print job {JobId} ended with {Status}", job.JobId, result.Status);
        }
        finally
        {
            result.ProcessedAt = DateTime.UtcNow;
            try { await SaveAsync(fingerprint, result); }
            catch (Exception ex)
            {
                result.Success = false;
                result.Status = "Unknown";
                result.Message = "تعذر حفظ نتيجة المهمة؛ راجع مخزن الطباعة قبل إعادة المحاولة بمعرف جديد.";
                _logger.LogError(ex, "Could not persist print result {JobId}", job.JobId);
            }
            if (workerAcquired) _workers.Release();
            if (printerAcquired) printerLock!.Release();
            await _gate.WaitAsync();
            try { _active.Remove(PrintJobIdentity.StorageKey(job.JobId, job.SystemId)); }
            finally { _gate.Release(); }
        }
        return result;
    }

    private async Task SaveAsync(string fingerprint, PrintJobResult result)
    {
        await _gate.WaitAsync();
        try { _store.Write(fingerprint, result); }
        finally { _gate.Release(); }
    }

    private string? Validate(PrintJobRequest job, out byte[] content)
    {
        content = [];
        if (!PrintJobIdentity.IsValidSystemId(job.SystemId))
            return "SystemId يجب ألا يتجاوز 64 حرفاً من الأحرف الإنجليزية والأرقام و . _ -.";
        if (job.Page?.Validate(job.DocumentType) is string pageError) return pageError;
        if (string.IsNullOrWhiteSpace(job.JobId) || job.JobId.Length > 128 || job.JobId.Any(char.IsControl))
            return "JobId مطلوب بطول لا يتجاوز 128 حرفاً.";
        if (!Enum.IsDefined(job.DocumentType)) return "نوع المستند غير مدعوم.";
        if (job.Copies < 1 || job.Copies > _options.MaxCopies) return $"عدد النسخ يجب أن يكون بين 1 و{_options.MaxCopies}.";
        if (string.IsNullOrWhiteSpace(job.JobTitle) || job.JobTitle.Length > 200 || job.JobTitle.Any(char.IsControl))
            return "عنوان المهمة مطلوب بطول لا يتجاوز 200 حرفاً ودون محارف تحكم.";
        if (job.PrinterName?.Length > 256 || job.PrinterName?.Any(char.IsControl) == true)
            return "اسم الطابعة غير صالح.";
        if (string.IsNullOrWhiteSpace(job.ContentBase64)) return "محتوى الطباعة فارغ.";
        if (job.ContentBase64.Length > ((_options.MaxDocumentBytes + 2L) / 3 * 4)) return "المستند يتجاوز الحجم المسموح.";
        try { content = Convert.FromBase64String(job.ContentBase64); }
        catch (FormatException) { return "محتوى Base64 غير صالح."; }
        if (content.Length == 0 || content.Length > _options.MaxDocumentBytes) return "حجم المستند غير صالح.";
        if (job.DocumentType == PrintDocumentType.Pdf && !content.AsSpan().StartsWith("%PDF-"u8))
            return "يجب إرسال ملف PDF حقيقي؛ لا يتم تحويل النص تلقائياً إلى PDF.";
        if (job.DocumentType == PrintDocumentType.Text)
        {
            try
            {
                string text = new UTF8Encoding(false, true).GetString(content);
                if (text.Length > 200_000 || text.Contains('\0')) return "النص طويل جداً أو يحتوي على محارف غير صالحة.";
            }
            catch (DecoderFallbackException) { return "النص يجب أن يكون UTF-8 صالحاً."; }
        }
        if (job.DocumentType == PrintDocumentType.Image)
        {
            using var data = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > _options.MaxImagePixels)
                return "الصورة غير صالحة أو تتجاوز الأبعاد المسموحة.";
        }
        return null;
    }

    private static PrintJobResult Conflict(PrintJobRequest job)
    {
        var result = PrintJobResult.Fail(job.JobId, job.PrinterName, "استُخدم JobId نفسه لمحتوى أو إعدادات مختلفة.");
        result.Status = "Conflict";
        result.SystemId = job.SystemId;
        return result;
    }

    private static PrintJobResult Recover(PrintJobResult result, bool active)
    {
        if (!active && result.Status is "Queued" or "Processing")
        {
            result.Success = false;
            result.Status = "Unknown";
            result.Message = "توقفت الخدمة قبل حفظ النتيجة النهائية؛ راجع مخزن الطباعة. لن تعاد المهمة تلقائياً.";
        }
        return result;
    }
}
