using System.Runtime.InteropServices;

namespace PrintService.Infrastructure.Services;

/// <summary>
/// فئة مساعدة للتواصل المباشر مع مخزن الطباعة في ويندوز (Win32 Print Spooler)
/// عبر استدعاءات مكتبة winspool.drv لإرسال أوامر الطباعة الخام (RAW / ESC-POS)
/// إلى الطابعات الحرارية وطابعات الإيصالات والباركود بدون المرور بمحرك الرسوميات GDI.
/// </summary>
public static class RawPrinterHelper
{
    /// <summary>
    /// بنية بيانات معلومات المستند لتمريرها إلى دالة StartDocPrinter في ويندوز.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        /// <summary>
        /// اسم المستند الذي يظهر في قائمة انتظار الطابعة في ويندوز.
        /// </summary>
        [MarshalAs(UnmanagedType.LPStr)]
        public string? pDocName;

        /// <summary>
        /// مسار ملف الإخراج (يترك فارغاً للطباعة المباشرة على المنفذ).
        /// </summary>
        [MarshalAs(UnmanagedType.LPStr)]
        public string? pOutputFile;

        /// <summary>
        /// نوع البيانات المرسلة للطابعة (مثلاً: "RAW" للبيانات الخام بدون معالجة ويندوز).
        /// </summary>
        [MarshalAs(UnmanagedType.LPStr)]
        public string? pDataType;
    }

    /// <summary>
    /// دالة استدعاء ويندوز لفتح مقبض اتصال مباشر بالطابعة المحددة.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    /// <summary>
    /// دالة استدعاء ويندوز لإغلاق مقبض الطابعة بعد إتمام عملية الإرسال.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    /// <summary>
    /// دالة استدعاء ويندوز لبدء تسجيل مهمة طباعة جديدة في مخزن الطباعة (Spooler).
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

    /// <summary>
    /// دالة استدعاء ويندوز لإنهاء مهمة الطباعة الحالية وإرسالها للتنفيذ الفعلي.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool EndDocPrinter(IntPtr hPrinter);

    /// <summary>
    /// دالة استدعاء ويندوز لبدء صفحة طباعة جديدة داخل المهمة.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool StartPagePrinter(IntPtr hPrinter);

    /// <summary>
    /// دالة استدعاء ويندوز لإنهاء الصفحة الحالية في مهمة الطباعة.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool EndPagePrinter(IntPtr hPrinter);

    /// <summary>
    /// دالة استدعاء ويندوز لكتابة وتمرير مصفوفة البايتات مباشرة إلى مخزن الطابعة.
    /// </summary>
    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    /// <summary>
    /// إرسال مصفوفة بايتات خام (RAW bytes) مباشرة إلى الطابعة المحددة بصمت تام ودون نوافذ حوارية.
    /// </summary>
    /// <param name="printerName">اسم الطابعة المستهدفة في نظام ويندوز.</param>
    /// <param name="bytes">مصفوفة البايتات المراد طباعتها (أوامر ESC/POS أو بايتات النص).</param>
    /// <param name="documentName">اسم المهمة في قائمة انتظار ويندوز.</param>
    /// <returns>صحيح (true) في حال نجاح الإرسال بالكامل لمخزن الطابعة.</returns>
    /// <exception cref="ArgumentException">في حال كانت مصفوفة البايتات فارغة.</exception>
    /// <exception cref="InvalidOperationException">في حال حدوث خطأ في فتح الطابعة أو إرسال الأوامر عبر ويندوز.</exception>
    public static bool SendBytesToPrinter(string printerName, byte[] bytes, string documentName = "RAW_PRINT_JOB")
    {
        if (bytes == null || bytes.Length == 0)
        {
            throw new ArgumentException("بيانات الطباعة فارغة ولا يمكن إرسال أمر طباعة فارغ.", nameof(bytes));
        }

        // حجز مساحة ذاكرة غير مدارة (Unmanaged Memory) لتمريرها إلى دوال Win32 C-API
        IntPtr pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);

        IntPtr hPrinter = IntPtr.Zero;
        var di = new DOCINFOA
        {
            pDocName = documentName,
            pDataType = "RAW" // تعيين نمط البيانات كـ RAW لضمان عدم قيام ويندوز بأي تعديل أو محاولة تفسير رسومي للبايتات
        };

        bool success = false;

        try
        {
            // 1. فتح مقبض الطابعة
            if (OpenPrinter(printerName.Normalize(), out hPrinter, IntPtr.Zero))
            {
                // 2. بدء مهمة الطباعة
                if (StartDocPrinter(hPrinter, 1, di))
                {
                    // 3. بدء الصفحة
                    if (StartPagePrinter(hPrinter))
                    {
                        // 4. كتابة البايتات إلى مخزن الطابعة
                        success = WritePrinter(hPrinter, pUnmanagedBytes, bytes.Length, out _);
                        EndPagePrinter(hPrinter);
                    }
                    EndDocPrinter(hPrinter);
                }
                ClosePrinter(hPrinter);
            }

            // في حال فشل أي خطوة، يتم قراءة كود الخطأ الدقيق من نظام ويندوز
            if (!success)
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"فشل إرسال البيانات إلى الطابعة '{printerName}'. رمز الخطأ من ويندوز: {errorCode}");
            }

            return success;
        }
        finally
        {
            // تحرير مساحة الذاكرة غير المدارة دائماً لمنع أي تسريب للذاكرة (Memory Leak)
            Marshal.FreeCoTaskMem(pUnmanagedBytes);
        }
    }
}
