namespace PrintService.Core.Models;

/// <summary>
/// يمثل النتيجة المرتجعة بعد معالجة أمر الطباعة وتمريره إلى نظام ويندوز.
/// </summary>
public class PrintJobResult
{
    public string? SystemId { get; set; }
    /// <summary>ValidationFailed, Queued, Processing, Submitted, Failed, Unknown, or Conflict.</summary>
    public string Status { get; set; } = "ValidationFailed";
    public int CompletedCopies { get; set; }
    public List<uint> SpoolerJobIds { get; set; } = [];
    public List<string> OutputFiles { get; set; } = [];
    /// <summary>
    /// يشير إلى ما إذا كانت عملية إرسال أمر الطباعة قد تمت بنجاح.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// رسالة توضيحية لنتيجة العملية أو تفاصيل الخطأ في حال الفشل.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// المعرف الفريد لعملية الطباعة المطابق للطلب الأصلي.
    /// </summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    /// اسم الطابعة التي تم توجيه أمر الطباعة إليها فعلياً.
    /// </summary>
    public string? PrinterName { get; set; }

    /// <summary>
    /// التوقيت العالمي (UTC) لمعالجة الطلب.
    /// </summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// إنشاء كائن نتيجة ناجحة لأمر الطباعة.
    /// </summary>
    /// <param name="jobId">معرف العملية.</param>
    /// <param name="printerName">اسم الطابعة المستهدفة.</param>
    /// <param name="message">رسالة النجاح.</param>
    public static PrintJobResult Ok(string jobId, string printerName, string message = "تم إرسال أمر الطباعة بنجاح إلى مخزن الطابعة.")
    {
        return new PrintJobResult
        {
            Success = true,
            Status = "Submitted",
            JobId = jobId,
            PrinterName = printerName,
            Message = message
        };
    }

    /// <summary>
    /// إنشاء كائن نتيجة فاشلة لأمر الطباعة مع تفاصيل الخطأ.
    /// </summary>
    /// <param name="jobId">معرف العملية.</param>
    /// <param name="printerName">اسم الطابعة المستهدفة (إن وجد).</param>
    /// <param name="errorMessage">نص رسالة الخطأ.</param>
    public static PrintJobResult Fail(string jobId, string? printerName, string errorMessage)
    {
        return new PrintJobResult
        {
            Success = false,
            JobId = jobId,
            PrinterName = printerName,
            Message = errorMessage
        };
    }
}
