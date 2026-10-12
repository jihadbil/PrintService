using System.Diagnostics;
using System.Text;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

public sealed class WindowsPrintBackend(PrintingOptions options) : IPrintBackend
{
    public async Task<BackendPrintResult> SubmitAsync(PrintJobRequest request, byte[] content,
        string printerName, CancellationToken ct)
    {
        if (printerName.Contains("OneNote", StringComparison.OrdinalIgnoreCase) ||
            printerName.Contains("doPDF", StringComparison.OrdinalIgnoreCase))
            throw new PrintPreparationException("هذه الطابعة قد تطلب نافذة تفاعلية؛ استخدم طابعة فعلية أو Microsoft Print to PDF للنصوص والصور.");
        bool pdfPrinter = printerName.Contains("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase);
        if (request.DocumentType == PrintDocumentType.RawEscPos)
        {
            if (pdfPrinter) throw new PrintPreparationException("لا يمكن تحويل أوامر ESC/POS إلى PDF؛ اختر نوع Text أو Image.");
            uint id = await Task.Run(() => RawPrinterHelper.Submit(printerName, content, request.JobTitle, ct), ct);
            return new(id);
        }
        if (request.DocumentType == PrintDocumentType.Pdf)
        {
            if (pdfPrinter) throw new PrintPreparationException("طباعة PDF إلى طابعة ملفات تتطلب مسار حفظ لا يدعمه محرك PDF؛ اختر طابعة فعلية.");
            await PrintPdfAsync(printerName, content, request.Page, ct);
            return new();
        }
        string? output = null;
        if (pdfPrinter)
        {
            string directory = Path.Combine(options.DataDirectory, "output");
            Directory.CreateDirectory(directory);
            output = Path.Combine(directory, $"print_{Guid.NewGuid():N}.pdf");
        }
        uint? jobId = await Task.Run<uint?>(() =>
        {
            if (options.DocumentEngine == "Win32Gdi")
                return request.DocumentType == PrintDocumentType.Text
                    ? Win32GdiPrintEngine.PrintText(printerName, Encoding.UTF8.GetString(content), request.JobTitle, output, ct, request.Page)
                    : Win32GdiPrintEngine.PrintImage(printerName, content, request.JobTitle, output, ct, request.Page);
            if (request.DocumentType == PrintDocumentType.Text)
                LegacyPrintDocumentEngine.PrintText(printerName, Encoding.UTF8.GetString(content), request.JobTitle, output, ct, request.Page);
            else LegacyPrintDocumentEngine.PrintImage(printerName, content, request.JobTitle, output, ct, request.Page);
            return null;
        }, ct);
        if (output is not null)
        {
            // Some drivers finish writing asynchronously after EndDoc.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            bool valid = false;
            try
            {
                while (!valid)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    try
                    {
                        using var file = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None);
                        var header = new byte[5];
                        valid = file.Length > 5 && file.Read(header) == 5 && header.AsSpan().SequenceEqual("%PDF-"u8);
                    }
                    catch (IOException) { }
                    if (!valid) await Task.Delay(100, timeout.Token);
                }
            }
            catch (Exception ex)
            {
                throw new PrintSubmissionException(
                    "أُرسل الأمر للطابعة لكن تعذر تأكيد ملف PDF؛ تحقق من صلاحيات الكتابة في مجلد الإخراج ومخزن Windows.", jobId, ex);
            }
        }
        return new(jobId, output);
    }

    public string? FindPdfExecutable()
    {
        if (options.PdfExecutablePath is not null)
            return File.Exists(options.PdfExecutablePath) ? options.PdfExecutablePath : null;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", "SumatraPDF.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SumatraPDF", "SumatraPDF.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SumatraPDF", "SumatraPDF.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? PdfPrintSettings(PrintPageOptions? page)
    {
        // Copies belong to the API request; embedded PDF ViewerPreferences must not multiply them.
        var values = new List<string> { "1x", "ignore-pdf-print-settings" };
        if (page?.PaperName is string paper) values.Add("paper=" + paper.ToLowerInvariant());
        return values.Count == 0 ? null : string.Join(',', values);
    }

    private async Task PrintPdfAsync(string printer, byte[] content, PrintPageOptions? page, CancellationToken ct)
    {
        string executable = FindPdfExecutable() ?? throw new PrintPreparationException(
            "SumatraPDF غير موجود. ثبته أو اضبط Printing:PdfExecutablePath على مساره الكامل.");
        string directory = Path.Combine(options.DataDirectory, "temporary");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"print_{Guid.NewGuid():N}.pdf");
        bool safeToDelete = true;
        try
        {
            await File.WriteAllBytesAsync(file, content, ct);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (string arg in new[] { "-print-to", printer, "-silent", "-exit-on-print" })
                start.ArgumentList.Add(arg);
            if (PdfPrintSettings(page) is string settings)
            {
                start.ArgumentList.Add("-print-settings");
                start.ArgumentList.Add(settings);
            }
            start.ArgumentList.Add(file);
            using var process = Process.Start(start) ?? throw new IOException("تعذر تشغيل محرك PDF.");
            safeToDelete = false;
            // Drain both pipes immediately so the child cannot block on a full buffer.
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.PdfTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                safeToDelete = true;
                await Task.WhenAll(error, output).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    safeToDelete = true;
                }
                catch (Exception) { /* Retain the file if the child may still be using it. */ }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException("انتهت مهلة محرك PDF؛ تحقق من مخزن الطباعة قبل إعادة الطلب بمعرف جديد.");
            }
            if (process.ExitCode != 0)
                throw new IOException($"فشل محرك PDF برمز خروج {process.ExitCode}.");
        }
        finally
        {
            if (safeToDelete)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
