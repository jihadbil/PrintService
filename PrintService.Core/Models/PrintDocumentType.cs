namespace PrintService.Core.Models;

/// <summary>
/// يحدد نوع المستند أو صيغة البيانات المرسلة لأمر الطباعة.
/// </summary>
public enum PrintDocumentType
{
    /// <summary>
    /// أوامر بايتات حرارية مباشرة بنظام ESC/POS (لطابعات الكاشير والمطابخ).
    /// </summary>
    RawEscPos = 1,

    /// <summary>
    /// مستند بصيغة PDF للطباعة الصامتة على الطابعات العادية أو الحرارية.
    /// </summary>
    Pdf = 2,

    /// <summary>
    /// نص عادي خام يتم إرساله مباشرة لمخزن الطابعة.
    /// </summary>
    Text = 3,

    /// <summary>
    /// صورة عادية (PNG, JPEG, BMP) تتم طباعتها عبر محرك الرسوميات.
    /// </summary>
    Image = 4
}
