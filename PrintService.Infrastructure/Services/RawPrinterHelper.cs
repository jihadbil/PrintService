using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrintService.Infrastructure.Services;

public static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
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

    public static bool SendBytesToPrinter(string printerName, byte[] bytes, string documentName = "RAW_PRINT_JOB")
    {
        Submit(printerName, bytes, documentName, CancellationToken.None);
        return true;
    }

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

    internal static Win32Exception Error(string operation) => new(Marshal.GetLastPInvokeError(), operation);
}
