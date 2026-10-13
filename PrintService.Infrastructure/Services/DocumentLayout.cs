using System.Globalization;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// فئة مساعدة لحساب تخطيط المستندات وتنسيق النصوص والتفاف الأسطر وتكييف مقاسات الصور بدقة.
/// </summary>
public static class DocumentLayout
{
    /// <summary>
    /// فحص ما إذا كان النص يحتوي على محارف لغات تُكتب من اليمين إلى اليسار (مثل العربية والعبرية).
    /// </summary>
    /// <param name="text">النص المراد فحصه.</param>
    /// <returns>صحيح إذا كان النص يبدأ أو يحتوي في غالبيته على محارف من اليمين إلى اليسار؛ وإلا خطأ.</returns>
    public static bool IsRightToLeft(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            return c is >= '\u0590' and <= '\u08ff' or >= '\ufb1d' and <= '\ufdff' or >= '\ufe70' and <= '\ufeff';
        }
        return false;
    }

    /// <summary>
    /// تقسيم والتفاف النص إلى أسطر تتناسب مع العرض المتاح للطباعة، مع الحفاظ على الكلمات والمحارف المركبة (Surrogate Pairs).
    /// </summary>
    /// <param name="text">النص الكامل المراد تجزئته.</param>
    /// <param name="width">العرض المتاح للطباعة على الصفحة.</param>
    /// <param name="measure">دالة لقياس العرض الفعلي لكل جزء من النص بوحدات الرسم المحددة.</param>
    /// <returns>قائمة بالأسطر المجزأة التي تلائم عرض الصفحة تماماً.</returns>
    /// <exception cref="InvalidOperationException">يتم إطلاقها إذا كانت المساحة غير صالحة أو العرض لا يتسع لحرف واحد.</exception>
    public static IReadOnlyList<string> WrapText(string text, float width, Func<string, float> measure)
    {
        if (width <= 0) throw new InvalidOperationException("مساحة الورق القابلة للطباعة غير صالحة.");
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(""); continue; }
            int[] elements = StringInfo.ParseCombiningCharacters(paragraph);
            int start = 0;
            while (start < elements.Length)
            {
                int low = start + 1, high = elements.Length, fit = start;
                while (low <= high)
                {
                    int middle = low + (high - low) / 2;
                    int end = middle == elements.Length ? paragraph.Length : elements[middle];
                    if (measure(paragraph[elements[start]..end]) <= width) { fit = middle; low = middle + 1; }
                    else high = middle - 1;
                }
                if (fit == start) throw new InvalidOperationException("عرض الورق لا يتسع لحرف واحد بالخط الحالي.");
                int finish = fit == elements.Length ? paragraph.Length : elements[fit];
                if (fit < elements.Length)
                {
                    int space = paragraph.LastIndexOf(' ', finish - 1, finish - elements[start]);
                    if (space > elements[start])
                    {
                        int boundary = Array.BinarySearch(elements, space + 1);
                        if (boundary > start) { fit = boundary; finish = space + 1; }
                    }
                }
                lines.Add(paragraph[elements[start]..finish]);
                start = fit;
            }
        }
        return lines;
    }

    /// <summary>
    /// ملاءمة أبعاد الصورة مع المساحة المتاحة للطباعة مع الحفاظ التام على النسبة التناسبية (Aspect Ratio).
    /// </summary>
    /// <param name="imageWidth">عرض الصورة الأصلي.</param>
    /// <param name="imageHeight">ارتفاع الصورة الأصلي.</param>
    /// <param name="width">عرض المساحة المتاحة للطباعة.</param>
    /// <param name="height">ارتفاع المساحة المتاحة للطباعة.</param>
    /// <returns>زوج يمثل العرض والارتفاع الجديدين بعد الملاءمة التناسبية.</returns>
    /// <exception cref="InvalidOperationException">يتم إطلاقها إذا كانت أبعاد الصورة أو الورق سالبة أو مساوية للصفر.</exception>
    public static (int Width, int Height) FitImage(int imageWidth, int imageHeight, int width, int height)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || width <= 0 || height <= 0)
            throw new InvalidOperationException("أبعاد الصورة أو الورق غير صالحة.");
        double scale = Math.Min((double)width / imageWidth, (double)height / imageHeight);
        return (Math.Max(1, (int)(imageWidth * scale)), Math.Max(1, (int)(imageHeight * scale)));
    }

    /// <summary>
    /// ملاءمة أبعاد الصورة مع المساحة المتاحة للطباعة مع مراعاة كثافة نقط الشاشة ودقة الطابعة (DPI) في كلا المحورين.
    /// </summary>
    /// <param name="imageWidth">عرض الصورة بالبكسل.</param>
    /// <param name="imageHeight">ارتفاع الصورة بالبكسل.</param>
    /// <param name="width">عرض مساحة الصفحة بوحدات الجهاز.</param>
    /// <param name="height">ارتفاع مساحة الصفحة بوحدات الجهاز.</param>
    /// <param name="dpiX">دقة الطابعة الأفقية (DPI X).</param>
    /// <param name="dpiY">دقة الطابعة الرأسية (DPI Y).</param>
    /// <returns>زوج يمثل العرض والارتفاع المناسبين لوحدات الرسم.</returns>
    /// <exception cref="InvalidOperationException">يتم إطلاقها إذا كانت الأبعاد أو قيم DPI غير صالحة.</exception>
    public static (int Width, int Height) FitImageAtDpi(int imageWidth, int imageHeight,
        int width, int height, int dpiX, int dpiY)
    {
        if (dpiX <= 0 || dpiY <= 0 || imageWidth <= 0 || imageHeight <= 0 || width <= 0 || height <= 0)
            throw new InvalidOperationException("أبعاد الصورة أو دقة الطابعة غير صالحة.");
        double scale = Math.Min((double)width / dpiX / imageWidth, (double)height / dpiY / imageHeight);
        return (Math.Max(1, (int)(imageWidth * scale * dpiX)), Math.Max(1, (int)(imageHeight * scale * dpiY)));
    }
}
