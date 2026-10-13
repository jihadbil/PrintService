using System.Runtime.InteropServices;
using System.Text;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// كتالوج وفهرس طابعات نظام ويندوز؛ يكتشف الطابعات وحالاتها التشغيلية وأحجام الورق المدعومة عبر EnumPrinters و DeviceCapabilities.
/// </summary>
public sealed class WindowsPrinterCatalog : IPrinterCatalog
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location,
            DevMode, SeparatorFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer,
        uint size, out uint needed, out uint returned);
    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinter(StringBuilder? buffer, ref uint size);
    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode)]
    private static extern int DeviceCapabilities(string printer, string port, short capability, IntPtr output, IntPtr devMode);

    /// <summary>
    /// قراءة غير متزامنة لجميع الطابعات المعرفة في نظام التشغيل مع حالاتها وأحجام الورق.
    /// </summary>
    /// <param name="ct">رمز إلغاء العملية.</param>
    /// <returns>قائمة غير قابلة للتعديل تحتوي على كائنات PrinterInfo لكل طابعة مثبتة.</returns>
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken ct) =>
        Task.Run<IReadOnlyList<PrinterInfo>>(() => Enumerate(ct), ct);

    /// <summary>
    /// استدعاء دالة EnumPrinters وتفكيك هياكل PRINTER_INFO_2 لإنشاء كائنات معلومات الطابعات.
    /// </summary>
    /// <param name="ct">رمز إلغاء العملية.</param>
    /// <returns>قائمة الطابعات المثبتة في النظام.</returns>
    private static List<PrinterInfo> Enumerate(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool initial = EnumPrinters(6, null, 2, IntPtr.Zero, 0, out uint needed, out _);
        if (needed == 0)
        {
            if (!initial) throw RawPrinterHelper.Error("EnumPrinters");
            return [];
        }
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(6, null, 2, buffer, needed, out _, out uint count))
                throw RawPrinterHelper.Error("EnumPrinters");
            string? defaultName = DefaultPrinterName();
            var printers = new List<PrinterInfo>();
            int stride = Marshal.SizeOf<PrinterInfo2>();
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buffer, i * stride));
                string name = Marshal.PtrToStringUni(p.PrinterName) ?? "";
                string port = Marshal.PtrToStringUni(p.PortName) ?? "";
                var state = DescribeStatus(p.Status, (p.Attributes & 0x400) != 0);
                printers.Add(new PrinterInfo
                {
                    Name = name,
                    IsDefault = string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    IsOnline = state.Online, IsReady = state.Ready, StatusKnown = true,
                    Status = state.Description, SupportedPaperSizes = PaperNames(name, port)
                });
            }
            return printers;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// تحليل رايات الحالة الرقمية المعادة من برنامج التشغيل وتحديد ما إذا كانت الطابعة متصلة، وجاهزة، مع وصف نصي للحالة.
    /// </summary>
    /// <param name="status">قناع بتات حالة الطابعة (PRINTER_STATUS_*).</param>
    /// <param name="workOffline">ما إذا كانت سمة العمل بدون اتصال (PRINTER_ATTRIBUTE_WORK_OFFLINE) مفعلة.</param>
    /// <returns>ثلاثي يحتوي على (Online: هل متصلة، Ready: هل جاهزة للطباعة، Description: الوصف النصي للحالة).</returns>
    public static (bool Online, bool Ready, string Description) DescribeStatus(uint status, bool workOffline = false)
    {
        var names = new List<string>();
        (uint Flag, string Name)[] flags = [(1, "Paused"), (2, "Error"), (4, "PendingDeletion"),
            (8, "PaperJam"), (16, "PaperOut"), (32, "ManualFeed"), (64, "PaperProblem"),
            (128, "Offline"), (256, "IoActive"), (512, "Busy"), (1024, "Printing"),
            (2048, "OutputBinFull"), (4096, "NotAvailable"), (8192, "Waiting"),
            (16384, "Processing"), (32768, "Initializing"), (65536, "WarmingUp"),
            (131072, "TonerLow"), (262144, "NoToner"), (524288, "PagePunt"),
            (1048576, "UserIntervention"), (2097152, "OutOfMemory"), (4194304, "DoorOpen"),
            (8388608, "ServerUnknown"), (16777216, "PowerSave")];
        foreach (var flag in flags) if ((status & flag.Flag) != 0) names.Add(flag.Name);
        if (workOffline && !names.Contains("Offline")) names.Add("Offline");
        bool online = !workOffline && (status & (128u | 4096u | 8388608u)) == 0;
        const uint blocking = 1 | 2 | 4 | 8 | 16 | 32 | 64 | 2048 | 262144 | 524288 | 1048576 | 2097152 | 4194304;
        return (online, online && (status & blocking) == 0, names.Count == 0 ? "Ready" : string.Join(", ", names));
    }

    /// <summary>
    /// جلب اسم الطابعة الافتراضية المحددة في نظام ويندوز عبر GetDefaultPrinter.
    /// </summary>
    /// <returns>اسم الطابعة الافتراضية أو null إذا لم تكن هناك طابعة محددة.</returns>
    private static string? DefaultPrinterName()
    {
        uint size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0) return null;
        var name = new StringBuilder(checked((int)size));
        return GetDefaultPrinter(name, ref size) ? name.ToString() : null;
    }

    /// <summary>
    /// استعلام أسماء مقاسات الورق التي يدعمها برنامج تشغيل الطابعة عبر DeviceCapabilities.
    /// </summary>
    /// <param name="printer">اسم الطابعة في ويندوز.</param>
    /// <param name="port">اسم المنفذ المتصلة به الطابعة.</param>
    /// <returns>قائمة بأسماء مقاسات الورق المدعومة.</returns>
    private static List<string> PaperNames(string printer, string port)
    {
        int count = DeviceCapabilities(printer, port, 16, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0 || count > 4096) return [];
        IntPtr buffer = Marshal.AllocHGlobal(count * 64 * sizeof(char));
        try
        {
            int actual = DeviceCapabilities(printer, port, 16, buffer, IntPtr.Zero);
            if (actual < 0 || actual > count) return [];
            var names = new List<string>();
            for (int i = 0; i < actual; i++)
                names.Add(DecodePaperName(Marshal.PtrToStringUni(IntPtr.Add(buffer, i * 128), 64) ?? ""));
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// فك تشفير وتجريد اسم مقاس الورق الثابت الطول المرتجع من Win32 API باقتطاعه عند أول محرف فارغ (null character).
    /// </summary>
    /// <param name="fixedWidthName">السلسلة النصية ذات العرض الثابت المرتجعة من الذاكرة.</param>
    /// <returns>الاسم النظيف لمقاس الورق.</returns>
    public static string DecodePaperName(string fixedWidthName) => fixedWidthName.Split('\0')[0];
}
