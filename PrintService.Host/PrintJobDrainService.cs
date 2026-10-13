using PrintService.Core.Interfaces;
using PrintService.Infrastructure.Services;

namespace PrintService.Host;

/// <summary>
/// خدمة خلفية مستضافة تضمن إفراغ واكتمال معالجة مهام الطباعة الجارية قبل إيقاف تشغيل الخادم بأمان (Graceful Shutdown).
/// </summary>
/// <param name="printerService">خدمة إدارة الطابعات المسؤولة عن معالجة وإفراغ المهام.</param>
/// <param name="logger">مسجل الأحداث لتتبع تفاصيل الإيقاف والتحذيرات.</param>
public sealed class PrintJobDrainService(IPrinterService printerService, ILogger<PrintJobDrainService> logger) : IHostedService
{
    /// <summary>
    /// يتم استدعاؤها عند بدء تشغيل الخدمة المستضافة.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء العملية.</param>
    /// <returns>مهمة مكتملة فوراً لعدم الحاجة لعمليات تهيئة خلفية عند الإقلاع.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// يتم استدعاؤها عند إيقاف الخادم للانتظار حتى انتهاء جميع مهام الطباعة قيد التنفيذ قبل إغلاق التطبيق.
    /// </summary>
    /// <param name="cancellationToken">رمز إلغاء يحدد المهلة القصوى المتاحة للإيقاف الآمن.</param>
    /// <returns>مهمة غير متزامنة تمثل عملية الانتظار والإفراغ.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await ((WindowsPrinterService)printerService).DrainAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown deadline reached. Unfinished jobs will be marked Unknown after restart.");
        }
    }
}
