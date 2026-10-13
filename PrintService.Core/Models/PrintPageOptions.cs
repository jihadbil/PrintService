namespace PrintService.Core.Models;

/// <summary>
/// خيارات تخطيط وتنسيق الصفحة المخصصة لكل عملية طباعة دون تغيير الإعدادات الافتراضية العامة للطابعة في ويندوز.
/// </summary>
public sealed record PrintPageOptions
{
    /// <summary>
    /// اسم مقاس الورق المعياري المعرف في الطابعة (مثل: A4, Letter, 80mm).
    /// </summary>
    public string? PaperName { get; init; }

    /// <summary>
    /// عرض الورق المخصص بالمليمتر (عند تحديد أبعاد ورق مخصصة).
    /// </summary>
    public double? PaperWidthMm { get; init; }

    /// <summary>
    /// ارتفاع الورق المخصص بالمليمتر (عند تحديد أبعاد ورق مخصصة).
    /// </summary>
    public double? PaperHeightMm { get; init; }

    /// <summary>
    /// اتجاه الصفحة: true للوضع الأفقي (Landscape)، وfalse أو null للوضع الرأسي (Portrait).
    /// </summary>
    public bool? Landscape { get; init; }

    /// <summary>
    /// هوامش الطباعة المخصصة لجميع جوانب الصفحة بالمليمتر.
    /// </summary>
    public PrintMargins? Margins { get; init; }

    /// <summary>
    /// معالجة وتوحيد خيارات الصفحة بإزالة الفراغات وإرجاع كائن فارغ إذا لم تتضمن أي إعدادات خاصة.
    /// </summary>
    /// <returns>نسخة موحدة من الخيارات، أو null إذا كانت جميع الخيارات خالية.</returns>
    public PrintPageOptions? Normalize()
    {
        var normalized = this with
        {
            PaperName = string.IsNullOrWhiteSpace(PaperName) ? null : PaperName.Trim(),
            Margins = Margins?.HasValues() == true ? Margins : null
        };
        return normalized.PaperName is null && normalized.PaperWidthMm is null &&
            normalized.PaperHeightMm is null && normalized.Landscape is null && normalized.Margins is null
            ? null : normalized;
    }

    /// <summary>
    /// التحقق من توافق وصحة خيارات الصفحة مع نوع المستند المستهدف وحدود الأبعاد الهندسية المسموحة.
    /// </summary>
    /// <param name="documentType">نوع المستند المراد طباعته.</param>
    /// <returns>رسالة الخطأ التوضيحية بالعربية إن وجدت، أو null إذا كانت الخيارات صالحة بالكامل.</returns>
    public string? Validate(PrintDocumentType documentType)
    {
        if (PaperName?.Length > 64 || PaperName?.Any(char.IsControl) == true)
            return "اسم مقاس الورق غير صالح.";
        if ((PaperWidthMm is null) != (PaperHeightMm is null))
            return "أرسل عرض الورق وارتفاعه معاً.";
        if (PaperName is not null && PaperWidthMm is not null)
            return "اختر اسم مقاس الورق أو أبعاده المخصصة، وليس كليهما.";
        if (PaperWidthMm is double width && (!double.IsFinite(width) || width is < 20 or > 1000) ||
            PaperHeightMm is double height && (!double.IsFinite(height) || height is < 20 or > 3000))
            return "عرض الورق يجب أن يكون بين 20 و1000 مم وارتفاعه بين 20 و3000 مم.";
        if (Margins is not null)
        {
            foreach (double? margin in new[] { Margins.LeftMm, Margins.RightMm, Margins.TopMm, Margins.BottomMm })
                if (margin is double value && (!double.IsFinite(value) || value is < 0 or > 100))
                    return "كل هامش يجب أن يكون بين 0 و100 مم.";
        }
        if (PaperWidthMm is double w && PaperHeightMm is double h)
        {
            if (Landscape == true) (w, h) = (h, w);
            if ((Margins?.LeftMm ?? 3) + (Margins?.RightMm ?? 3) >= w ||
                (Margins?.TopMm ?? 3) + (Margins?.BottomMm ?? 3) >= h)
                return "الهوامش لا تترك مساحة للطباعة على المقاس المحدد.";
        }
        if (documentType == PrintDocumentType.RawEscPos)
            return "خيارات الورق والهوامش في ESC/POS يجب أن تكون داخل الأوامر الخام.";
        if (documentType == PrintDocumentType.Pdf)
        {
            if (Landscape is not null)
                return "هذا المحرك لا يغيّر اتجاه الورق لملفات PDF؛ جهّز اتجاه الصفحة داخل الملف.";
            if (PaperWidthMm is not null || Margins is not null)
                return "أبعاد PDF وهوامشه تحدد داخل الملف؛ يمكن طلب اتجاه الطباعة ومقاس ورق قياسي فقط.";
            string[] supported = ["A2", "A3", "A4", "A5", "A6", "letter", "legal", "tabloid", "statement"];
            if (PaperName is not null && !supported.Contains(PaperName, StringComparer.OrdinalIgnoreCase))
                return "محرك PDF لا يدعم اسم مقاس الورق المطلوب.";
        }
        return null;
    }
}

/// <summary>
/// يمثل قيم هوامش الصفحة بالمليمتر لجميع الحواف (اليسار، اليمين، الأعلى، الأسفل).
/// </summary>
public sealed record PrintMargins
{
    /// <summary>
    /// الهامش الأيسر بالمليمتر.
    /// </summary>
    public double? LeftMm { get; init; }

    /// <summary>
    /// الهامش الأيمن بالمليمتر.
    /// </summary>
    public double? RightMm { get; init; }

    /// <summary>
    /// الهامش العلوي بالمليمتر.
    /// </summary>
    public double? TopMm { get; init; }

    /// <summary>
    /// الهامش السفلي بالمليمتر.
    /// </summary>
    public double? BottomMm { get; init; }

    /// <summary>
    /// التحقق مما إذا كان هناك أي هامش محدد بقيمة غير فارغة.
    /// </summary>
    /// <returns>صحيح إذا كان أي هامش يحتوي على قيمة؛ وإلا خطأ.</returns>
    public bool HasValues() => LeftMm is not null || RightMm is not null || TopMm is not null || BottomMm is not null;
}
