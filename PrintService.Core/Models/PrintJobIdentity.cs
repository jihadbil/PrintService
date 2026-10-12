namespace PrintService.Core.Models;

public static class PrintJobIdentity
{
    public static string? NormalizeSystemId(string? systemId) =>
        string.IsNullOrWhiteSpace(systemId) ? null : systemId.Trim().ToLowerInvariant();

    public static bool IsValidSystemId(string? systemId)
    {
        string? normalized = NormalizeSystemId(systemId);
        return normalized is null || (normalized.Length <= 64 && normalized.All(c =>
            c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'));
    }

    // Legacy IDs cannot contain NUL. Keep their existing hash and journal path unchanged.
    public static string StorageKey(string jobId, string? systemId) =>
        NormalizeSystemId(systemId) is string scope ? scope + '\0' + jobId : jobId;
}
