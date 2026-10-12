namespace PrintService.Core.Models;

/// <summary>Per-job layout. Never changes shared Windows printer defaults.</summary>
public sealed record PrintPageOptions
{
    public string? PaperName { get; init; }
    public double? PaperWidthMm { get; init; }
    public double? PaperHeightMm { get; init; }
    public bool? Landscape { get; init; }
    public PrintMargins? Margins { get; init; }

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

public sealed record PrintMargins
{
    public double? LeftMm { get; init; }
    public double? RightMm { get; init; }
    public double? TopMm { get; init; }
    public double? BottomMm { get; init; }

    public bool HasValues() => LeftMm is not null || RightMm is not null || TopMm is not null || BottomMm is not null;
}
