namespace PrintService.Core.Models;

/// <summary>
/// فئة مساعدة مسؤولة عن إدارة هويات ونطاقات مهام الطباعة وعزل الأنظمة المختلفة لمنع تداخل المعرفات.
/// </summary>
public static class PrintJobIdentity
{
    /// <summary>
    /// توحيد وضبط صيغة معرف النظام (SystemId) بإزالة الفراغات وتحويله إلى أحرف صغيرة.
    /// </summary>
    /// <param name="systemId">معرف النظام أو التطبيق المدخل.</param>
    /// <returns>المعرف المعياري الموحد، أو null إذا كانت القيمة فارغة.</returns>
    public static string? NormalizeSystemId(string? systemId) =>
        string.IsNullOrWhiteSpace(systemId) ? null : systemId.Trim().ToLowerInvariant();

    /// <summary>
    /// التحقق من صلاحية معرف النظام (SystemId) وتوافقه مع المعايير الأمنية المسموحة (أحرف صغيرة، أرقام، ورموز محددة).
    /// </summary>
    /// <param name="systemId">معرف النظام المراد فحصه.</param>
    /// <returns>صحيح إذا كان المعرف صالحاً أو فارغاً؛ وإلا خطأ.</returns>
    public static bool IsValidSystemId(string? systemId)
    {
        string? normalized = NormalizeSystemId(systemId);
        return normalized is null || (normalized.Length <= 64 && normalized.All(c =>
            c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'));
    }

    /// <summary>
    /// إنشاء مفتاح تخزين فريد للمهمة يجمع بين نطاق النظام ومعرف العملية لضمان العزل التام في السجل.
    /// </summary>
    /// <param name="jobId">المعرف الفريد لعملية الطباعة.</param>
    /// <param name="systemId">معرف النظام الفرعي (إن وجد).</param>
    /// <returns>مفتاح التخزين الموحد المستخدم في السجل وفهرسة المهام.</returns>
    public static string StorageKey(string jobId, string? systemId) =>
        NormalizeSystemId(systemId) is string scope ? scope + '\0' + jobId : jobId;
}
