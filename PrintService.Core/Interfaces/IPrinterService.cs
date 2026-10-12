using PrintService.Core.Models;

namespace PrintService.Core.Interfaces;

/// <summary>
/// واجهة إدارة الطابعات وتنفيذ أوامر الطباعة الصامتة على مستوى نظام التشغيل.
/// </summary>
public interface IPrinterService
{
    /// <summary>
    /// جلب قائمة بجميع الطابعات المثبتة في نظام ويندوز مع تفاصيلها وأحجام الورق المدعومة.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء العملية إن وجد.</param>
    /// <returns>قائمة غير قابلة للتعديل تحتوي على بيانات الطابعات المثبتة.</returns>
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// جلب بيانات الطابعة الافتراضية المحددة في نظام ويندوز.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء العملية إن وجد.</param>
    /// <returns>بيانات الطابعة الافتراضية، أو null في حال عدم وجود أي طابعة مسجلة في النظام.</returns>
    Task<PrinterInfo?> GetDefaultPrinterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// تنفيذ أمر طباعة صامت وإرسال البيانات مباشرة إلى مخزن الطابعة (Print Spooler) دون أي نوافذ حوارية.
    /// </summary>
    /// <param name="request">طلب أمر الطباعة المتضمن اسم الطابعة، نوع المستند، ومحتواه المشفر بنظام Base64.</param>
    /// <param name="cancellationToken">رمز إلغاء العملية إن وجد.</param>
    /// <returns>نتيجة معالجة أمر الطباعة مع التوضيح وحالة النجاح أو الفشل.</returns>
    Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default);
}
