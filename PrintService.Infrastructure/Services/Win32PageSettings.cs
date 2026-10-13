using System.Runtime.InteropServices;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// إدارة وتطبيق إعدادات الورق واتجاه الطباعة المخصصة للمهمة الحالية عبر بنية DEVMODE في الذاكرة دون المساس بإعدادات النظام العامة.
/// </summary>
internal sealed class Win32PageSettings : IDisposable
{
    /// <summary>
    /// مؤشر في الذاكرة غير المدارة لبنية DEVMODE المهيأة للمهمة.
    /// </summary>
    public IntPtr DevMode { get; private set; }

    /// <summary>
    /// يحدد ما إذا كانت الصفحة مضبوطة بالوضع الأفقي (Landscape).
    /// </summary>
    public bool Landscape { get; private set; }
    private const int Fields = 72, Orientation = 76, PaperSize = 78, PaperLength = 80, PaperWidth = 82;
    private const int DmOrientation = 1, DmPaperSize = 2, DmPaperLength = 4, DmPaperWidth = 8, DmFormName = 0x10000;
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(IntPtr window, IntPtr printer, string name, IntPtr output, IntPtr input, uint mode);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    private static extern bool GetPrinter(IntPtr printer, uint level, IntPtr output, uint size, out uint needed);
    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode)]
    private static extern int DeviceCapabilities(string printer, string port, short capability, IntPtr output, IntPtr devMode);

    /// <summary>
    /// تهيئة وضبط إعدادات DEVMODE لطابعة محددة بناءً على خيارات الصفحة المطلوبة (المقاس المعياري، الأبعاد المخصصة، أو الاتجاه).
    /// </summary>
    /// <param name="name">اسم الطابعة في نظام ويندوز.</param>
    /// <param name="page">خيارات الصفحة المطلوبة للعملية.</param>
    /// <exception cref="PrintPreparationException">إذا فشل فتح الطابعة أو رفض برنامج التشغيل إعدادات الورق المطلوبة.</exception>
    public Win32PageSettings(string name, PrintPageOptions? page)
    {
        if (page is null || page.PaperName is null && page.PaperWidthMm is null && page.Landscape is null) return;
        if (!RawPrinterHelper.OpenPrinter(name, out var printer, IntPtr.Zero))
            throw new PrintPreparationException($"تعذر فتح الطابعة لإعداد الورق: {name}");
        try
        {
            int size = DocumentProperties(IntPtr.Zero, printer, name, IntPtr.Zero, IntPtr.Zero, 0);
            if (size < 84) throw new PrintPreparationException("تعريف الطابعة لا يوفر إعدادات ورق صالحة.");
            DevMode = Marshal.AllocHGlobal(size);
            if (DocumentProperties(IntPtr.Zero, printer, name, DevMode, IntPtr.Zero, 2) != 1)
                throw new PrintPreparationException("تعذر قراءة إعدادات الطابعة.");
            if ((ushort)Marshal.ReadInt16(DevMode, 68) < 84)
                throw new PrintPreparationException("بنية إعدادات تعريف الطابعة غير مدعومة.");
            int fields = Marshal.ReadInt32(DevMode, Fields);
            short? expectedPaperId = null;
            if (page.Landscape is bool landscape)
            {
                Marshal.WriteInt16(DevMode, Orientation, (short)(landscape ? 2 : 1)); fields |= DmOrientation;
            }
            if (page.PaperWidthMm is double width && page.PaperHeightMm is double height)
            {
                Marshal.WriteInt16(DevMode, PaperSize, 0);
                Marshal.WriteInt16(DevMode, PaperWidth, checked((short)Math.Round(width * 10)));
                Marshal.WriteInt16(DevMode, PaperLength, checked((short)Math.Round(height * 10)));
                fields = (fields & ~DmFormName) | DmPaperSize | DmPaperWidth | DmPaperLength;
            }
            else if (page.PaperName is string paper)
            {
                expectedPaperId = FindPaperId(printer, name, paper);
                Marshal.WriteInt16(DevMode, PaperSize, expectedPaperId.Value);
                fields = (fields & ~(DmFormName | DmPaperWidth | DmPaperLength)) | DmPaperSize;
            }
            Marshal.WriteInt32(DevMode, Fields, fields);
            if (DocumentProperties(IntPtr.Zero, printer, name, DevMode, DevMode, 2 | 8) != 1)
                throw new PrintPreparationException("تعريف الطابعة رفض خيارات الورق المطلوبة.");
            if (expectedPaperId is short id && Marshal.ReadInt16(DevMode, PaperSize) != id)
                throw new PrintPreparationException("تعريف الطابعة استبدل مقاس الورق المطلوب بمقاس آخر.");
            if (page.Landscape is bool requested && Marshal.ReadInt16(DevMode, Orientation) != (requested ? 2 : 1))
                throw new PrintPreparationException("الطابعة لا تدعم اتجاه الورق المطلوب.");
            Landscape = Marshal.ReadInt16(DevMode, Orientation) == 2;
        }
        catch { Dispose(); throw; }
        finally { RawPrinterHelper.ClosePrinter(printer); }
    }

    /// <summary>
    /// البحث عن المعرف الرقمي لمقاس الورق (Paper ID) المدعوم من قبل برنامج تشغيل الطابعة.
    /// </summary>
    /// <param name="printer">مقبض الطابعة المفتوح.</param>
    /// <param name="name">اسم الطابعة.</param>
    /// <param name="paper">اسم مقاس الورق المطلوب مطابقتها.</param>
    /// <returns>المعرف الرقمي المعرف في برنامج التشغيل لمقاس الورق.</returns>
    /// <exception cref="PrintPreparationException">إذا كان مقاس الورق غير مدعوم أو تعذر قراءة قدرات الجهاز.</exception>
    private static short FindPaperId(IntPtr printer, string name, string paper)
    {
        GetPrinter(printer, 2, IntPtr.Zero, 0, out uint needed);
        if (needed < 4 * IntPtr.Size) throw new PrintPreparationException("تعذر قراءة معلومات منفذ الطابعة.");
        IntPtr info = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!GetPrinter(printer, 2, info, needed, out _)) throw RawPrinterHelper.Error("GetPrinter");
            string port = Marshal.PtrToStringUni(Marshal.ReadIntPtr(info, 3 * IntPtr.Size)) ?? "";
            int count = DeviceCapabilities(name, port, 16, IntPtr.Zero, IntPtr.Zero);
            if (count is <= 0 or > 4096) throw new PrintPreparationException("تعذر قراءة مقاسات ورق الطابعة.");
            IntPtr names = Marshal.AllocHGlobal(count * 128), ids = IntPtr.Zero;
            try
            {
                ids = Marshal.AllocHGlobal(count * sizeof(short));
                int nameCount = DeviceCapabilities(name, port, 16, names, IntPtr.Zero);
                int idCount = DeviceCapabilities(name, port, 2, ids, IntPtr.Zero);
                if (nameCount < 0 || idCount < 0 || nameCount > count || idCount > count)
                    throw new PrintPreparationException("تعذر قراءة مقاسات الورق بصورة متسقة.");
                for (int i = 0; i < Math.Min(nameCount, idCount); i++)
                {
                    string label = WindowsPrinterCatalog.DecodePaperName(Marshal.PtrToStringUni(IntPtr.Add(names, i * 128), 64) ?? "");
                    if (string.Equals(label, paper, StringComparison.OrdinalIgnoreCase)) return Marshal.ReadInt16(ids, i * sizeof(short));
                }
                throw new PrintPreparationException($"مقاس الورق '{paper}' غير مدعوم على هذه الطابعة.");
            }
            finally { Marshal.FreeHGlobal(names); if (ids != IntPtr.Zero) Marshal.FreeHGlobal(ids); }
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>
    /// تحرير الذاكرة غير المدارة المخصصة لبنية DEVMODE.
    /// </summary>
    public void Dispose()
    {
        if (DevMode != IntPtr.Zero) Marshal.FreeHGlobal(DevMode);
        DevMode = IntPtr.Zero;
    }
}
