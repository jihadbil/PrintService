namespace PrintService.Infrastructure;

/// <summary>
/// خيارات وإعدادات تشغيل خدمة الطباعة والحدود الأمنية لموارد النظام وحجم المستندات.
/// </summary>
public sealed class PrintingOptions
{
    /// <summary>
    /// محرك معالجة ورسم المستندات المستخدم (PrintDocument أو Win32Gdi).
    /// </summary>
    public string DocumentEngine { get; set; } = "PrintDocument";

    /// <summary>
    /// الحد الأقصى لعدد النسخ المسموح بطلب طباعتها في العملية الواحدة (الافتراضي: 10 نسخ).
    /// </summary>
    public int MaxCopies { get; set; } = 10;

    /// <summary>
    /// الحد الأقصى لحجم المستند بالبايت (الافتراضي: 10 ميجابايت).
    /// </summary>
    public int MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// الحد الأقصى لعدد بكسلات الصورة المسموح بمعالجتها لمنع استهلاك الذاكرة المفرط (الافتراضي: 25 مليون بكسل).
    /// </summary>
    public int MaxImagePixels { get; set; } = 25_000_000;

    /// <summary>
    /// السعة القصوى لقائمة انتظار المهام المتزامنة في الذاكرة (الافتراضي: 32 مهمة).
    /// </summary>
    public int MaxQueuedJobs { get; set; } = 32;

    /// <summary>
    /// الحد الأقصى لعدد الطابعات التي يمكن إرسال أوامر طباعة إليها بالتوازي (الافتراضي: طابعتان).
    /// </summary>
    public int MaxConcurrentPrinters { get; set; } = 2;

    /// <summary>
    /// المهلة الزمنية القصوى بالثواني لانتظار محرك طباعة PDF الخارجي قبل إيقافه (الافتراضي: 60 ثانية).
    /// </summary>
    public int PdfTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// المسار المطلق لملف تشغيل محرك طباعة PDF (مثل SumatraPDF.exe)، اختياري.
    /// </summary>
    public string? PdfExecutablePath { get; set; }

    /// <summary>
    /// المسار المطلق لمجلد تخزين بيانات وسجلات ومخرجات خدمة الطباعة.
    /// </summary>
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintService");

    /// <summary>
    /// التحقق من صحة الإعدادات والحدود المكونة والتأكد من أنها تقع ضمن النطاقات المسموحة والمسارات الصحيحة.
    /// </summary>
    /// <exception cref="ArgumentException">يتم إطلاقها إذا كانت إحدى القيم خارج النطاق المدعوم أو المسارات غير مطلقة.</exception>
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
