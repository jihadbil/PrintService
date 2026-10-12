using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

public interface IPrintBackend
{
    Task<BackendPrintResult> SubmitAsync(PrintJobRequest request, byte[] content,
        string printerName, CancellationToken cancellationToken);
}

public sealed record BackendPrintResult(uint? SpoolerJobId = null, string? OutputFilePath = null);

/// <summary>A failure known to occur before invoking a printer or external process.</summary>
public sealed class PrintPreparationException(string message) : InvalidOperationException(message);

public sealed class PrintSubmissionException(string message, uint? spoolerJobId, Exception inner)
    : IOException(message, inner)
{
    public uint? SpoolerJobId { get; } = spoolerJobId;
}

public interface IPrinterCatalog
{
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken);
}
