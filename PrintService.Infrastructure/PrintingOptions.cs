namespace PrintService.Infrastructure;

public sealed class PrintingOptions
{
    // Keep the existing engine until Win32Gdi has been verified with the installed drivers.
    public string DocumentEngine { get; set; } = "PrintDocument";
    public int MaxCopies { get; set; } = 10;
    public int MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxImagePixels { get; set; } = 25_000_000;
    public int MaxQueuedJobs { get; set; } = 32;
    public int MaxConcurrentPrinters { get; set; } = 2;
    public int PdfTimeoutSeconds { get; set; } = 60;
    public string? PdfExecutablePath { get; set; }
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintService");

    public void Validate()
    {
        if (DocumentEngine is not ("PrintDocument" or "Win32Gdi"))
            throw new ArgumentException("Printing:DocumentEngine must be PrintDocument or Win32Gdi.");
        if (MaxCopies is < 1 or > 100 || MaxDocumentBytes is < 1 or > 100 * 1024 * 1024 ||
            MaxImagePixels is < 1 or > 100_000_000 || MaxQueuedJobs is < 1 or > 256 ||
            MaxConcurrentPrinters is < 1 or > 16 || PdfTimeoutSeconds is < 1 or > 600)
            throw new ArgumentException("Printing limits are outside the supported ranges.");
        if (string.IsNullOrWhiteSpace(DataDirectory) || !Path.IsPathFullyQualified(DataDirectory))
            throw new ArgumentException("Printing:DataDirectory must be an absolute path.");
        if (PdfExecutablePath is not null && !Path.IsPathFullyQualified(PdfExecutablePath))
            throw new ArgumentException("Printing:PdfExecutablePath must be an absolute path.");
    }
}
