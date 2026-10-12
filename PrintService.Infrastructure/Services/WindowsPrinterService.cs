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
/// سواء للأوامر الحرارية (RAW / ESC-POS) أو الصور العادية.
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
            // قراءة اسم الطابعة الافتراضية المحددة في إعدادات ويندوز
            var defaultSettings = new PrinterSettings();
            defaultPrinterName = defaultSettings.PrinterName ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذر قراءة الطابعة الافتراضية من إعدادات النظام.");
        }

        // استعراض كافة الطابعات المثبتة عبر PrinterSettings.InstalledPrinters
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
                    // جلب أحجام الورق المتاحة والمعرفة لهذه الطابعة في ويندوز
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
            // فك تشفير المحتوى من Base64 إلى مصفوفة بايتات
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
            int copies = Math.Max(1, request.Copies);

            for (int i = 0; i < copies; i++)
            {
                switch (request.DocumentType)
                {
                    case PrintDocumentType.RawEscPos:
                    case PrintDocumentType.Text:
                    case PrintDocumentType.Pdf:
                        // إرسال بايتات الأوامر مباشرة إلى مخزن الطباعة Win32 Spooler دون وسيط رسومي
                        RawPrinterHelper.SendBytesToPrinter(targetPrinter, payloadBytes, request.JobTitle);
                        break;

                    case PrintDocumentType.Image:
                        // طباعة الصور بصمت عبر محرك GDI+ وإلغاء نافذة المعاينة
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
    /// طباعة الصور النقطية بصمت تام باستخدام PrintDocument مع إلغاء مربع حوار حالة الطباعة.
    /// </summary>
    /// <param name="printerName">اسم الطابعة.</param>
    /// <param name="imageBytes">بايتات ملف الصورة.</param>
    /// <param name="jobTitle">اسم المستند في قائمة الانتظار.</param>
    private static void PrintImageSilently(string printerName, byte[] imageBytes, string jobTitle)
    {
        using var memoryStream = new MemoryStream(imageBytes);
        using var image = Image.FromStream(memoryStream);

        using var printDocument = new PrintDocument();
        printDocument.PrinterSettings.PrinterName = printerName;
        printDocument.DocumentName = jobTitle;
        
        // استخدام StandardPrintController هو السر لإلغاء أي نافذة حوارية أو تنبيه في ويندوز
        printDocument.PrintController = new StandardPrintController();

        printDocument.PrintPage += (sender, e) =>
        {
            if (e.Graphics != null)
            {
                // ملاءمة الصورة داخل أبعاد وهوامش الصفحة
                Rectangle m = e.MarginBounds;
                e.Graphics.DrawImage(image, m);
            }
            e.HasMorePages = false;
        };

        printDocument.Print();
    }
}
