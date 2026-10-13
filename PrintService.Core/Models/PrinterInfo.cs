namespace PrintService.Core.Models;

/// <summary>
/// يمثل بيانات الطابعة المثبتة على نظام ويندوز وحالتها التشغيلية.
/// </summary>
public class PrinterInfo
{
    /// <summary>
    /// اسم الطابعة المسجل في نظام ويندوز.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// يحدد ما إذا كانت هذه الطابعة هي الطابعة الافتراضية للنظام.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// يحدد ما إذا كانت الطابعة متصلة وجاهزة لاستقبال الأوامر.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// يحدد ما إذا كانت الطابعة جاهزة للطباعة الفورية وخالية من أي أعطال أو نفاد في الورق أو الحبر.
    /// </summary>
    public bool IsReady { get; set; }

    /// <summary>
    /// يشير إلى ما إذا كان قد تم التحقق بنجاح من حالة الطابعة الحالية عبر نظام التشغيل.
    /// </summary>
    public bool StatusKnown { get; set; }

    /// <summary>
    /// الوصف النصي لحالة الطابعة (مثل: Ready, InvalidOrOffline).
    /// </summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>
    /// قائمة بجميع أحجام الورق التي تدعمها هذه الطابعة (مثل: A4, Letter, 80mm).
    /// </summary>
    public List<string> SupportedPaperSizes { get; set; } = [];
}
