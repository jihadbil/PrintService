using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// واجهة محرك الطباعة الخلفي المسؤول عن التفاعل المباشر مع برامج تشغيل الطابعات ونظام ويندوز.
/// </summary>
public interface IPrintBackend
{
    /// <summary>
    /// إرسال أمر ومحتوى الطباعة إلى الطابعة المستهدفة عبر محرك المعالجة المناسب.
    /// </summary>
    /// <param name="request">طلب الطباعة المتضمن البيانات الوصفية والخيارات.</param>
    /// <param name="content">المحتوى الثنائي المفكك للمستند.</param>
    /// <param name="printerName">اسم الطابعة في نظام التشغيل.</param>
    /// <param name="cancellationToken">رمز إلغاء العملية.</param>
    /// <returns>نتيجة الإرسال متضمنة رقم العملية في مخزن الطباعة ومسار ملف الإخراج إن وجد.</returns>
    Task<BackendPrintResult> SubmitAsync(PrintJobRequest request, byte[] content,
        string printerName, CancellationToken cancellationToken);
}

/// <summary>
/// يمثل النتيجة المباشرة الناتجة عن محرك الطباعة الخلفي بعد تمرير البيانات للويندوز.
/// </summary>
/// <param name="SpoolerJobId">رقم عملية الطباعة في مخزن ويندوز (Spooler Job ID) إن وجد.</param>
/// <param name="OutputFilePath">المسار الكامل لملف الإخراج الناتج (في حال كانت الطابعة تطبع لملف مثل PDF).</param>
public sealed record BackendPrintResult(uint? SpoolerJobId = null, string? OutputFilePath = null);

/// <summary>
/// استثناء يقع أثناء مرحلة تجهيز والتحقق من بيانات أمر الطباعة قبل إرساله إلى برنامج التشغيل أو مخزن ويندوز.
/// </summary>
/// <param name="message">رسالة توضيح سبب فشل التجهيز.</param>
public sealed class PrintPreparationException(string message) : InvalidOperationException(message);

/// <summary>
/// استثناء يقع أثناء إرسال أمر الطباعة إلى مخزن الطباعة (Spooler) مع الاحتفاظ بمعرف المهمة إن وُجد لتتبعها.
/// </summary>
/// <param name="message">رسالة تفاصيل الخطأ.</param>
/// <param name="spoolerJobId">رقم المهمة في مخزن ويندوز في حال تم إنشاؤها قبل وقوع الخطأ.</param>
/// <param name="inner">الاستثناء الداخلي المسبب.</param>
public sealed class PrintSubmissionException(string message, uint? spoolerJobId, Exception inner)
    : IOException(message, inner)
{
    /// <summary>
    /// رقم العملية المسجل في مخزن طباعة ويندوز وقت وقوع الاستثناء.
    /// </summary>
    public uint? SpoolerJobId { get; } = spoolerJobId;
}

/// <summary>
/// واجهة استكشاف وفهرسة الطابعات المثبتة في نظام التشغيل وقراءة خصائصها.
/// </summary>
public interface IPrinterCatalog
{
    /// <summary>
    /// قراءة قائمة الطابعات المثبتة في نظام التشغيل وحالاتها التشغيلية وأحجام الورق المدعومة.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء العملية.</param>
    /// <returns>قائمة غير قابلة للتعديل ببيانات الطابعات.</returns>
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken);
}
