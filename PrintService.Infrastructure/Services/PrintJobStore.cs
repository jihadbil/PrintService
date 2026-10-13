using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// مخزن وسجل مهام الطباعة المحلي (Journal Store)؛ يحفظ حالات المهام وبصماتها لمنع تكرار التنفيذ وضمان الثبات عبر إعادة التشغيل.
/// </summary>
public sealed class PrintJobStore : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;

    /// <summary>
    /// تهيئة مخزن المهام والتأكد من وجود مجلد التخزين وحجز قفل حصري للملف (agent.lock) لمنع تشغيل نسخ متعددة متضاربة.
    /// </summary>
    /// <param name="options">خيارات خدمة الطباعة المتضمنة مسار مجلد البيانات.</param>
    public PrintJobStore(PrintingOptions options)
    {
        _directory = Path.Combine(options.DataDirectory, "jobs");
        Directory.CreateDirectory(_directory);
        // Only one agent may use this journal, including during a restart.
        _lease = new FileStream(Path.Combine(_directory, "agent.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// يمثل قيداً محفوظاً في سجل المهام يحتوي على بصمة المحتوى الفريدة ونتيجة معالجة العملية.
    /// </summary>
    /// <param name="Fingerprint">بصمة تجزئة SHA256 لمحتوى وإعدادات طلب الطباعة لمنع التضارب.</param>
    /// <param name="Result">نتيجة وحالة معالجة العملية المحفوظة.</param>
    public sealed record Entry(string Fingerprint, PrintJobResult Result);

    /// <summary>
    /// قراءة واسترجاع قيد مهمة طباعة سابقة من السجل الدائم باستخدام معرف المهمة ونطاق النظام.
    /// </summary>
    /// <param name="jobId">المعرف الفريد لعملية الطباعة.</param>
    /// <param name="systemId">معرف أو نطاق النظام الفرعي (إن وجد).</param>
    /// <returns>قيد المهمة المخزن، أو null إذا لم تكن مسجلة مسبقاً.</returns>
    /// <exception cref="IOException">يتم إطلاقها إذا كان ملف السجل تالفاً أو لا يمكن قراءته كـ JSON صالح.</exception>
    public Entry? Read(string jobId, string? systemId = null)
    {
        string path = JobPath(PrintJobIdentity.StorageKey(jobId, systemId));
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path))
            ?? throw new IOException("سجل مهمة الطباعة غير صالح؛ لن تتم إعادة الطباعة تلقائياً.");
    }

    /// <summary>
    /// حفظ وتحديث حالة مهمة الطباعة وبصمتها في السجل بطريقة ذرية (Atomic Write) لضمان عدم تلف الملف عند انقطاع الطاقة.
    /// </summary>
    /// <param name="fingerprint">بصمة التجزئة الفريدة لطلب الطباعة.</param>
    /// <param name="result">كائن نتيجة وحالة أمر الطباعة الحالي.</param>
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

    /// <summary>
    /// توليد مسار الملف الفيزيائي لسجل المهمة بناءً على تجزئة SHA256 لمفتاح التخزين لمنع المشاكل في أسماء الملفات بنظام ويندوز.
    /// </summary>
    /// <param name="jobId">مفتاح التخزين الموحد للمهمة.</param>
    /// <returns>المسار الكامل لملف السجل بصيغة JSON.</returns>
    private string JobPath(string jobId) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId))) + ".json");

    /// <summary>
    /// تحرير قفل الملف الحصري عند إيقاف تشغيل الخدمة.
    /// </summary>
    public void Dispose() => _lease.Dispose();
}
