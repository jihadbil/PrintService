namespace PrintService.Core.Models;

/// <summary>
/// يمثل طلب أمر الطباعة الصادر من تطبيق React أو أي نظام خارجي إلى خدمة الطباعة.
/// </summary>
public class PrintJobRequest
{
    /// <summary>
    /// المعرف الفريد لعملية الطباعة (يستخدم للتتبع ومنع تكرار معالجة نفس الطلب).
    /// </summary>
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// اسم الطابعة المستهدفة في نظام ويندوز.
    /// في حال تركه فارغاً أو null، سيتم استخدام الطابعة الافتراضية للنظام تلقائياً.
    /// </summary>
    public string? PrinterName { get; set; }

    /// <summary>
    /// نوع المستند المراد طباعته (ESC/POS خام، PDF، نص، صورة).
    /// القيمة الافتراضية هي RawEscPos.
    /// </summary>
    public PrintDocumentType DocumentType { get; set; } = PrintDocumentType.RawEscPos;

    /// <summary>
    /// محتوى المستند مشفراً بنظام Base64 لتجنب أي مشاكل في الترميز أو تلف البيانات أثناء النقل عبر HTTP.
    /// </summary>
    public string ContentBase64 { get; set; } = string.Empty;

    /// <summary>
    /// عدد النسخ المطلوب طباعتها (الافتراضي: نسخة واحدة).
    /// </summary>
    public int Copies { get; set; } = 1;

    /// <summary>
    /// عنوان المستند الذي يظهر في قائمة انتظار الطابعة (Spooler) في ويندوز.
    /// </summary>
    public string JobTitle { get; set; } = "SilentPrintJob";
}
