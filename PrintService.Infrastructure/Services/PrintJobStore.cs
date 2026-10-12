using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

public sealed class PrintJobStore : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;

    public PrintJobStore(PrintingOptions options)
    {
        _directory = Path.Combine(options.DataDirectory, "jobs");
        Directory.CreateDirectory(_directory);
        // Only one agent may use this journal, including during a restart.
        _lease = new FileStream(Path.Combine(_directory, "agent.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public sealed record Entry(string Fingerprint, PrintJobResult Result);

    public Entry? Read(string jobId, string? systemId = null)
    {
        string path = JobPath(PrintJobIdentity.StorageKey(jobId, systemId));
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path))
            ?? throw new IOException("سجل مهمة الطباعة غير صالح؛ لن تتم إعادة الطباعة تلقائياً.");
    }

    public void Write(string fingerprint, PrintJobResult result)
    {
        string path = JobPath(PrintJobIdentity.StorageKey(result.JobId, result.SystemId));
        string temporary = path + ".tmp";
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Entry(fingerprint, result));
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(data);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string JobPath(string jobId) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId))) + ".json");

    public void Dispose() => _lease.Dispose();
}
