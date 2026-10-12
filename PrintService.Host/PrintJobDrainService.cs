using PrintService.Core.Interfaces;
using PrintService.Infrastructure.Services;

namespace PrintService.Host;

public sealed class PrintJobDrainService(IPrinterService printerService, ILogger<PrintJobDrainService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await ((WindowsPrinterService)printerService).DrainAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown deadline reached. Unfinished jobs will be marked Unknown after restart.");
        }
    }
}
