using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// فئة مساعدة للتواصل المباشر مع مكتبة winspool.drv لإرسال بايتات الطباعة الخام (ESC/POS) لمخزن طابعات ويندوز.
/// </summary>
public static class RawPrinterHelper
{
    /// <summary>
    /// بنية بيانات معلومات المستند المرسل لمخزن طابعة ويندوز عبر دالة StartDocPrinter.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal sealed class DocInfo
    {
        /// <summary>اسم المستند كما يظهر في قائمة انتظار الطابعة.</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? DocumentName;
        /// <summary>مسار ملف الإخراج (اختياري).</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        /// <summary>نوع البيانات المرسلة (الافتراضي: RAW للبيانات الخام).</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    internal static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartDocPrinter(IntPtr printer, uint level, [In] DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool AbortPrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printer, IntPtr data, int count, out int written);

    /// <summary>
    /// إرسال مصفوفة بايتات خام مباشرة إلى الطابعة المحددة بدون رمز إلغاء.
    /// </summary>
    /// <param name="printerName">اسم الطابعة في نظام ويندوز.</param>
    /// <param name="bytes">البايتات الخام المراد إرسالها (مثل أوامر ESC/POS).</param>
    /// <param name="documentName">اسم المستند في قائمة انتظار الطابعة.</param>
    /// <returns>صحيح إذا تم الإرسال بنجاح.</returns>
    public static bool SendBytesToPrinter(string printerName, byte[] bytes, string documentName = "RAW_PRINT_JOB")
    {
        Submit(printerName, bytes, documentName, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// إرسال وكتابة بايتات الطباعة الخام مباشرة إلى مخزن الطابعة (Spooler) مع دعم الإلغاء وتتبع معرف المهمة.
    /// </summary>
    /// <param name="printerName">اسم الطابعة في نظام ويندوز.</param>
    /// <param name="bytes">مصفوفة بايتات البيانات الخام.</param>
    /// <param name="documentName">اسم المهمة في مخزن ويندوز.</param>
    /// <param name="ct">رمز إلغاء العملية.</param>
    /// <returns>رقم عملية الطباعة المسجل في مخزن ويندوز (Spooler Job ID).</returns>
    /// <exception cref="ArgumentNullException">إذا كانت مصفوفة البايتات فارغة (null).</exception>
    /// <exception cref="ArgumentException">إذا كان طول مصفوفة البايتات صفراً.</exception>
    /// <exception cref="Win32Exception">إذا فشل أحد نداءات Win32 API.</exception>
    /// <exception cref="IOException">إذا لم يكتب مخزن الطباعة عدد البايتات المتوقع بالكامل.</exception>
    public static uint Submit(string printerName, byte[] bytes, string documentName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("بيانات الطباعة فارغة.", nameof(bytes));
        ct.ThrowIfCancellationRequested();
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero)) throw Error("OpenPrinter");
        GCHandle pinned = default;
        bool documentStarted = false;
        try
        {
            pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            uint jobId = StartDocPrinter(printer, 1, new DocInfo { DocumentName = documentName });
            if (jobId == 0) throw Error("StartDocPrinter");
            documentStarted = true;
            if (!StartPagePrinter(printer)) throw Error("StartPagePrinter");
            int offset = 0;
            while (offset < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                if (!WritePrinter(printer, IntPtr.Add(pinned.AddrOfPinnedObject(), offset),
                    bytes.Length - offset, out int written)) throw Error("WritePrinter");
                if (written <= 0 || written > bytes.Length - offset)
                    throw new IOException("لم يكتب مخزن الطباعة عدد البايتات المتوقع.");
                offset += written;
            }
            if (!EndPagePrinter(printer)) throw Error("EndPagePrinter");
            if (!EndDocPrinter(printer)) throw Error("EndDocPrinter");
            documentStarted = false;
            return jobId;
        }
        finally
        {
            if (documentStarted) AbortPrinter(printer);
            if (pinned.IsAllocated) pinned.Free();
            ClosePrinter(printer);
        }
    }

    /// <summary>
    /// إنشاء كائن استثناء Win32Exception مفصل يحتوي على رمز الخطأ الأصلي من نظام التشغيل واسم العملية التي أخفقت.
    /// </summary>
    /// <param name="operation">اسم عملية أو دالة Win32 API التي فشلت.</param>
    /// <returns>كائن Win32Exception يحمل تفاصيل الخطأ.</returns>
    internal static Win32Exception Error(string operation) => new(Marshal.GetLastPInvokeError(), operation);
}
