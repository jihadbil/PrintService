namespace PrintService.Core.Models;

/// <summary>
/// يمثل النتيجة المرتجعة بعد معالجة أمر الطباعة وتمريره إلى نظام ويندوز.
/// </summary>
public class PrintJobResult
{
    /// <summary>
    /// نطاق أو معرف النظام الفرعي التابع له هذا الطلب (إن وجد).
    /// </summary>
    public string? SystemId { get; set; }

    /// <summary>
    /// الحالة التفصيلية لمهمة الطباعة (ValidationFailed, Queued, Processing, Submitted, Failed, Unknown, Conflict, Busy).
    /// </summary>
    public string Status { get; set; } = "ValidationFailed";

    /// <summary>
    /// عدد النسخ التي تم إرسالها وتأكيد وصولها بنجاح إلى مخزن الطباعة.
    /// </summary>
    public int CompletedCopies { get; set; }

    /// <summary>
    /// قائمة بأرقام مهام الطباعة المسجلة في مخزن طباعة ويندوز (Spooler Job IDs) لكل نسخة تم إرسالها.
    /// </summary>
    public List<uint> SpoolerJobIds { get; set; } = [];

    /// <summary>
    /// مسارات ملفات الإخراج الناتجة (في حال كانت الطابعة افتراضية تطبع إلى ملفات مثل PDF).
    /// </summary>
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
