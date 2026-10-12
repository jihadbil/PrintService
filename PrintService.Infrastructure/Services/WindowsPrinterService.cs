using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Text;
using Microsoft.Extensions.Logging;
using PrintService.Core.Interfaces;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// تنفيذ واجهة IPrinterService للتفاعل المباشر مع طابعات نظام ويندوز.
/// يدعم استكشاف الطابعات، وتحديد الطابعة الافتراضية، وتنفيذ الطباعة الصامتة
/// سواء للأوامر الحرارية (RAW / ESC-POS) أو النصوص المنسقة أو ملفات PDF أو الصور.
/// </summary>
public class WindowsPrinterService : IPrinterService
{
    private readonly ILogger<WindowsPrinterService> _logger;

    /// <summary>
    /// منشئ الخدمة، يستقبل نظام التسجيل (Logger) لتسجيل الأحداث والأخطاء.
    /// </summary>
    /// <param name="logger">كائن تسجيل الأحداث.</param>
    public WindowsPrinterService(ILogger<WindowsPrinterService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// قراءة واستكشاف جميع الطابعات المثبتة في نظام ويندوز مع التحقق من حالتها وأحجام الورق التي تدعمها.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء العملية.</param>
    /// <returns>قائمة الطابعات المثبتة في النظام.</returns>
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default)
    {
        var printers = new List<PrinterInfo>();
        string defaultPrinterName = string.Empty;

        try
        {
            var defaultSettings = new PrinterSettings();
            defaultPrinterName = defaultSettings.PrinterName ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذر قراءة الطابعة الافتراضية من إعدادات النظام.");
        }

        foreach (string printerName in PrinterSettings.InstalledPrinters)
        {
            var info = new PrinterInfo
            {
                Name = printerName,
                IsDefault = string.Equals(printerName, defaultPrinterName, StringComparison.OrdinalIgnoreCase),
                IsOnline = true,
                Status = "Ready"
            };

            try
            {
                var settings = new PrinterSettings { PrinterName = printerName };
                if (settings.IsValid)
                {
                    foreach (PaperSize paperSize in settings.PaperSizes)
                    {
                        info.SupportedPaperSizes.Add(paperSize.PaperName);
                    }
                }
                else
                {
                    info.IsOnline = false;
                    info.Status = "InvalidOrOffline";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "تعذر قراءة أحجام الورق للطابعة {PrinterName}", printerName);
            }

            printers.Add(info);
        }

        return Task.FromResult<IReadOnlyList<PrinterInfo>>(printers);
    }

    /// <summary>
    /// جلب بيانات الطابعة الافتراضية في النظام، أو أول طابعة متاحة.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء العملية.</param>
    /// <returns>بيانات الطابعة الافتراضية أو null.</returns>
    public async Task<PrinterInfo?> GetDefaultPrinterAsync(CancellationToken cancellationToken = default)
    {
        var printers = await GetPrintersAsync(cancellationToken);
        return printers.FirstOrDefault(p => p.IsDefault) ?? printers.FirstOrDefault();
    }

    /// <summary>
    /// تنفيذ أمر الطباعة الصامت بالكامل بدون إظهار أي نافذة تأكيد للمستخدم.
    /// </summary>
    /// <param name="request">بيانات الطلب بما فيها اسم الطابعة، نوع المستند، والمحتوى المشفر Base64.</param>
    /// <param name="cancellationToken">رمز إلغاء العملية.</param>
    /// <returns>نتيجة تنفيذ العملية.</returns>
    public async Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return PrintJobResult.Fail(string.Empty, null, "طلب الطباعة غير صالح (null).");
        }

        string targetPrinter = request.PrinterName?.Trim() ?? string.Empty;

        // إذا لم يحدد الطلب اسماً للطابعة، يتم التوجيه للطابعة الافتراضية للنظام
        if (string.IsNullOrEmpty(targetPrinter))
        {
            var defaultPrinter = await GetDefaultPrinterAsync(cancellationToken);
            if (defaultPrinter == null)
            {
                return PrintJobResult.Fail(request.JobId, null, "لم يتم العثور على أي طابعة مثبتة في النظام.");
            }
            targetPrinter = defaultPrinter.Name;
        }

        // التحقق من صحة وجود الطابعة في ويندوز
        var printerSettings = new PrinterSettings { PrinterName = targetPrinter };
        if (!printerSettings.IsValid)
        {
            return PrintJobResult.Fail(request.JobId, targetPrinter, $"الطابعة '{targetPrinter}' غير موجودة أو غير صالحة في النظام.");
        }

        if (string.IsNullOrWhiteSpace(request.ContentBase64))
        {
            return PrintJobResult.Fail(request.JobId, targetPrinter, "محتوى الطباعة (ContentBase64) فارغ.");
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(request.ContentBase64);
        }
        catch (FormatException)
        {
            return PrintJobResult.Fail(request.JobId, targetPrinter, "صيغة المحتوى غير صحيحة، يجب أن يكون Base64 صالحاً.");
        }

        _logger.LogInformation("بدء معالجة طلب الطباعة [{JobId}] للطابعة [{Printer}] بنوع [{DocType}] وحجم [{Size}] بايت",
            request.JobId, targetPrinter, request.DocumentType, payloadBytes.Length);

        try
        {
            // هل الطابعة طابعة افتراضية للملفات (مثل Microsoft Print to PDF أو doPDF)؟
            bool isVirtualPdfPrinter = targetPrinter.Contains("Print to PDF", StringComparison.OrdinalIgnoreCase)
                                    || targetPrinter.Contains("doPDF", StringComparison.OrdinalIgnoreCase);

            // التحقق هل البيانات تبدأ بترويسة ملف PDF حقيقي (%PDF)
            bool isRealPdf = payloadBytes.Length >= 4
                          && payloadBytes[0] == 0x25 // %
                          && payloadBytes[1] == 0x50 // P
                          && payloadBytes[2] == 0x44 // D
                          && payloadBytes[3] == 0x46; // F

            int copies = Math.Max(1, request.Copies);

            for (int i = 0; i < copies; i++)
            {
                switch (request.DocumentType)
                {
                    case PrintDocumentType.Text:
                        // طباعة النصوص المنسقة دائماً عبر محرك PrintDocument لضمان إنتاج مستند سليم وقابل للقراءة
                        string textData = Encoding.UTF8.GetString(payloadBytes);
                        PrintTextSilently(targetPrinter, textData, request.JobTitle);
                        break;

                    case PrintDocumentType.RawEscPos:
                        if (isVirtualPdfPrinter)
                        {
                            // إذا كان المستخدم يختبر طابعة Microsoft Print to PDF، يتم تحويل النص تلقائياً
                            // إلى PrintDocument لإنشاء ملف PDF سليم بدلاً من رمي بايتات خام تسبب "Failed to load PDF document"
                            string rawText = Encoding.UTF8.GetString(payloadBytes);
                            PrintTextSilently(targetPrinter, rawText, request.JobTitle);
                        }
                        else
                        {
                            // الطابعات الحرارية الحقيقية تستقبل بايتات ESC/POS مباشرة عبر winspool
                            RawPrinterHelper.SendBytesToPrinter(targetPrinter, payloadBytes, request.JobTitle);
                        }
                        break;

                    case PrintDocumentType.Pdf:
                        if (isRealPdf)
                        {
                            PrintPdfSilently(targetPrinter, payloadBytes, request.JobTitle);
                        }
                        else
                        {
                            // في حال تم إرسال نص عادي تحت تصنيف PDF، نقوم بطباعته كنص لإنشاء PDF صالح
                            string fallbackText = Encoding.UTF8.GetString(payloadBytes);
                            PrintTextSilently(targetPrinter, fallbackText, request.JobTitle);
                        }
                        break;

                    case PrintDocumentType.Image:
                        PrintImageSilently(targetPrinter, payloadBytes, request.JobTitle);
                        break;

                    default:
                        RawPrinterHelper.SendBytesToPrinter(targetPrinter, payloadBytes, request.JobTitle);
                        break;
                }
            }

            _logger.LogInformation("تم إرسال أمر الطباعة [{JobId}] بنجاح إلى [{Printer}]", request.JobId, targetPrinter);
            return PrintJobResult.Ok(request.JobId, targetPrinter, $"تم إرسال المستند بنجاح إلى الطابعة '{targetPrinter}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "خطأ أثناء تنفيذ الطباعة [{JobId}] على الطابعة [{Printer}]", request.JobId, targetPrinter);
            return PrintJobResult.Fail(request.JobId, targetPrinter, $"فشل في تنفيذ الطباعة: {ex.Message}");
        }
    }

    /// <summary>
    /// طباعة النصوص بصمت تام عبر محرك GDI+ لضمان توافقها الكامل مع كافة طابعات ويندوز (بما فيها طابعات الـ PDF الافتراضية).
    /// </summary>
    private static void PrintTextSilently(string printerName, string text, string jobTitle)
    {
        using var printDoc = new PrintDocument();
        printDoc.PrinterSettings.PrinterName = printerName;
        printDoc.DocumentName = jobTitle;
        // إلغاء أي شاشات حوارية للطباعة
        printDoc.PrintController = new StandardPrintController();

        // استخدام خط أحادي المسافة مناسب للفواتير والجداول
        using var font = new Font("Courier New", 9.5f, FontStyle.Regular);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        int lineIndex = 0;

        printDoc.PrintPage += (sender, e) =>
        {
            if (e.Graphics == null) return;

            float lineHeight = font.GetHeight(e.Graphics) + 2f;
            float y = e.MarginBounds.Top;
            float x = e.MarginBounds.Left;

            while (lineIndex < lines.Length)
            {
                if (y + lineHeight > e.MarginBounds.Bottom)
                {
                    e.HasMorePages = true;
                    return;
                }

                e.Graphics.DrawString(lines[lineIndex], font, Brushes.Black, x, y);
                y += lineHeight;
                lineIndex++;
            }

            e.HasMorePages = false;
        };

        printDoc.Print();
    }

    /// <summary>
    /// طباعة ملفات الـ PDF بصمت عبر أداة Edge المدمجة في ويندوز 10/11 أو مشغل النظام الافتراضي.
    /// </summary>
    private void PrintPdfSilently(string printerName, byte[] pdfBytes, string jobTitle)
    {
        string tempPdfPath = Path.Combine(Path.GetTempPath(), $"print_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(tempPdfPath, pdfBytes);

        try
        {
            string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(edgePath))
            {
                var pInfo = new ProcessStartInfo
                {
                    FileName = edgePath,
                    Arguments = $"--headless --print-to-printer --printer-name=\"{printerName}\" \"{tempPdfPath}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };
                using var proc = Process.Start(pInfo);
                proc?.WaitForExit(10000);
                return;
            }

            // خيار بديل عبر مشغل الويندوز الافتراضي
            var shellInfo = new ProcessStartInfo
            {
                FileName = tempPdfPath,
                Verb = "printto",
                Arguments = $"\"{printerName}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            };
            using var shellProc = Process.Start(shellInfo);
            shellProc?.WaitForExit(8000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذر تشغيل معالج PDF الخارجي، جاري التراجع للإرسال المباشر للمخزن");
            RawPrinterHelper.SendBytesToPrinter(printerName, pdfBytes, jobTitle);
        }
        finally
        {
            // حذف الملف المؤقت بعد الانتهاء
            Task.Delay(5000).ContinueWith(_ =>
            {
                try { if (File.Exists(tempPdfPath)) File.Delete(tempPdfPath); } catch { }
            });
        }
    }

    /// <summary>
    /// طباعة الصور النقطية بصمت تام باستخدام PrintDocument مع إلغاء مربع حوار حالة الطباعة.
    /// </summary>
    private static void PrintImageSilently(string printerName, byte[] imageBytes, string jobTitle)
    {
        using var memoryStream = new MemoryStream(imageBytes);
        using var image = Image.FromStream(memoryStream);

        using var printDocument = new PrintDocument();
        printDocument.PrinterSettings.PrinterName = printerName;
        printDocument.DocumentName = jobTitle;
        printDocument.PrintController = new StandardPrintController();

        printDocument.PrintPage += (sender, e) =>
        {
            if (e.Graphics != null)
            {
                Rectangle m = e.MarginBounds;
                e.Graphics.DrawImage(image, m);
            }
            e.HasMorePages = false;
        };

        printDocument.Print();
    }
}
