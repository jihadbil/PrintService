using System.Diagnostics;
using System.Text;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// تنفيذ محرك الطباعة لنظام ويندوز؛ يدعم أوامر ESC/POS المباشرة، ومستندات PDF عبر عملية منفصلة، والنصوص والصور عبر GDI.
/// </summary>
/// <param name="options">خيارات وتكوينات الطباعة المعتمدة للخدمة.</param>
public sealed class WindowsPrintBackend(PrintingOptions options) : IPrintBackend
{
    /// <summary>
    /// إرسال ومعالجة أمر الطباعة وتمريره إلى الطابعة المطلوبة عبر المحرك المخصص لنوع المستند.
    /// </summary>
    /// <param name="request">طلب أمر الطباعة المتضمن الخيارات والنوع والعنوان.</param>
    /// <param name="content">المحتوى الثنائي للمستند بعد فك ترميز Base64.</param>
    /// <param name="printerName">اسم الطابعة المستهدفة في نظام ويندوز.</param>
    /// <param name="ct">رمز إلغاء العملية.</param>
    /// <returns>نتيجة الإرسال متضمنة رقم العملية في مخزن الطباعة ومسار ملف الإخراج إن وجد.</returns>
    /// <exception cref="PrintPreparationException">إذا كانت الطابعة تتطلب تفاعلاً مرئياً أو خيارات غير متوافقة.</exception>
    /// <exception cref="PrintSubmissionException">إذا تعذر تأكيد وصول الأمر بنجاح إلى مخزن ويندوز.</exception>
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

    /// <summary>
    /// البحث عن المسار التنفيذي لبرنامج تشغيل ومحرك طباعة PDF الخارجي (SumatraPDF) في المسارات القياسية أو المكونة.
    /// </summary>
    /// <returns>المسار الكامل للملف التنفيذي إن وجد، أو null إذا لم يكن مثبتاً.</returns>
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

    /// <summary>
    /// توليد وسائط وإعدادات سطر الأوامر الخاصة بطباعة PDF وفقاً لخيارات الصفحة المحددة.
    /// </summary>
    /// <param name="page">خيارات الصفحة المطلوبة.</param>
    /// <returns>سلسلة نصية تحتوي على معاملات الطباعة، أو null إذا لم تتطلب إعدادات خاصة.</returns>
    public static string? PdfPrintSettings(PrintPageOptions? page)
    {
        // Copies belong to the API request; embedded PDF ViewerPreferences must not multiply them.
        var values = new List<string> { "1x", "ignore-pdf-print-settings" };
        if (page?.PaperName is string paper) values.Add("paper=" + paper.ToLowerInvariant());
        return values.Count == 0 ? null : string.Join(',', values);
    }

    /// <summary>
    /// طباعة مستند PDF عبر استدعاء محرك PDF الخارجي في عملية منفصلة صامتة ومراقبة اكتمالها ومهلتها.
    /// </summary>
    /// <param name="printer">اسم الطابعة في ويندوز.</param>
    /// <param name="content">مصفوفة بايتات ملف PDF.</param>
    /// <param name="page">خيارات مقاس الورق المحددة للطلب.</param>
    /// <param name="ct">رمز إلغاء العملية.</param>
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
