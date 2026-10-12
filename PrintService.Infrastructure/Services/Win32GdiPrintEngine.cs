using System.Runtime.InteropServices;
using SkiaSharp;
using PrintService.Core.Models;

namespace PrintService.Infrastructure.Services;

/// <summary>Native Windows GDI printer path; no System.Drawing.Printing dependency.</summary>
public static class Win32GdiPrintEngine
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        public int Size = Marshal.SizeOf<DocInfo>();
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Output;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        public uint Type;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll", EntryPoint = "StartDocW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDoc(IntPtr dc, [In] DocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision,
        uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DrawText(IntPtr dc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, IntPtr bits,
        ref BitmapInfo info, uint usage, uint operation);

    public static uint PrintText(string printer, string text, string title, string? output, CancellationToken ct, PrintPageOptions? page = null) =>
        Print(printer, title, output, dc =>
        {
            int dpiX = GetDeviceCaps(dc, 88), dpiY = GetDeviceCaps(dc, 90);
            if (dpiX <= 0 || dpiY <= 0) throw new InvalidOperationException("دقة الطابعة غير صالحة.");
            var bounds = Bounds(dc, page);
            int marginX = bounds.Left, marginY = bounds.Top;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            IntPtr font = CreateFont(-(int)Math.Ceiling(dpiY * 9.5 / 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Courier New");
            if (font == IntPtr.Zero) throw new InvalidOperationException("تعذر إنشاء خط الطباعة.");
            IntPtr previous = SelectObject(dc, font);
            try
            {
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw RawPrinterHelper.Error("SelectObject");
                if (SetBkMode(dc, 1) == 0) throw RawPrinterHelper.Error("SetBkMode");
                const uint singleLine = 0x20 | 0x800;
                float Measure(string line)
                {
                    var rect = new Rect();
                    if (DrawText(dc, line, line.Length, ref rect, singleLine | 0x400) == 0)
                        throw RawPrinterHelper.Error("DrawText measurement");
                    return rect.Right - rect.Left;
                }
                var lineRect = new Rect();
                if (DrawText(dc, "Ag", 2, ref lineRect, singleLine | 0x400) == 0) throw RawPrinterHelper.Error("DrawText");
                int lineHeight = lineRect.Bottom + Math.Max(1, dpiY / 36);
                if (height < lineHeight) throw new InvalidOperationException("ارتفاع الورق لا يتسع لسطر واحد.");
                var lines = DocumentLayout.WrapText(text, width, Measure);
                int y = marginY;
                Check(StartPage(dc), "StartPage");
                foreach (string line in lines)
                {
                    ct.ThrowIfCancellationRequested();
                    if (y + lineHeight > marginY + height)
                    {
                        Check(EndPage(dc), "EndPage");
                        Check(StartPage(dc), "StartPage");
                        y = marginY;
                    }
                    if (line.Length > 0)
                    {
                        var rect = new Rect { Left = marginX, Top = y, Right = marginX + width, Bottom = y + lineHeight };
                        uint flags = singleLine | (DocumentLayout.IsRightToLeft(line) ? 0x20000u | 2u : 0u);
                        Check(DrawText(dc, line, line.Length, ref rect, flags), "DrawText");
                    }
                    y += lineHeight;
                }
                Check(EndPage(dc), "EndPage");
            }
            finally { SelectObject(dc, previous); DeleteObject(font); }
        }, ct, page);

    public static uint PrintImage(string printer, byte[] bytes, string title, string? output, CancellationToken ct, PrintPageOptions? page = null)
    {
        using var encoded = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(encoded) ?? throw new InvalidOperationException("الصورة غير صالحة.");
        using var decoded = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidOperationException("تعذر فك الصورة بالكامل.");
        // Flatten transparent images onto white before sending a 32-bit DIB to GDI.
        using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(decoded, 0, 0); }
        return Print(printer, title, output, dc =>
        {
            ct.ThrowIfCancellationRequested();
            var bounds = Bounds(dc, page);
            int mx = bounds.Left, my = bounds.Top;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            var fit = DocumentLayout.FitImageAtDpi(bitmap.Width, bitmap.Height, width, height,
                GetDeviceCaps(dc, 88), GetDeviceCaps(dc, 90));
            var info = new BitmapInfo
            {
                Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = bitmap.Width, Height = -bitmap.Height,
                Planes = 1, BitCount = 32, ImageSize = checked((uint)bitmap.ByteCount)
            };
            Check(StartPage(dc), "StartPage");
            int result = StretchDIBits(dc, mx + (width - fit.Width) / 2, my + (height - fit.Height) / 2,
                fit.Width, fit.Height, 0, 0, bitmap.Width, bitmap.Height, bitmap.GetPixels(), ref info, 0, 0x00CC0020);
            if (result is 0 or -1) throw RawPrinterHelper.Error("StretchDIBits");
            Check(EndPage(dc), "EndPage");
        }, ct, page);
    }

    private static uint Print(string printer, string title, string? output, Action<IntPtr> render, CancellationToken ct, PrintPageOptions? page)
    {
        ct.ThrowIfCancellationRequested();
        using var settings = new Win32PageSettings(printer, page);
        IntPtr dc = CreateDC("WINSPOOL", printer, null, settings.DevMode);
        if (dc == IntPtr.Zero) throw RawPrinterHelper.Error("CreateDC");
        bool started = false;
        try
        {
            if (page?.PaperWidthMm is double width && page.PaperHeightMm is double height)
            {
                if (settings.Landscape) (width, height) = (height, width);
                ValidateDimensions(dc, width, height);
            }
            _ = Bounds(dc, page);
            int jobId = StartDoc(dc, new DocInfo { Name = title, Output = output });
            Check(jobId, "StartDoc");
            started = true;
            render(dc);
            ct.ThrowIfCancellationRequested();
            Check(EndDoc(dc), "EndDoc");
            started = false;
            return checked((uint)jobId);
        }
        finally { if (started) AbortDoc(dc); DeleteDC(dc); }
    }

    internal static void ValidateDeviceMode(string printer, IntPtr mode, double width, double height, bool landscape)
    {
        IntPtr dc = CreateDC("WINSPOOL", printer, null, mode);
        if (dc == IntPtr.Zero) throw new PrintPreparationException("تعذر التحقق من مقاس الورق.");
        try
        {
            if (landscape) (width, height) = (height, width);
            ValidateDimensions(dc, width, height);
        }
        finally { DeleteDC(dc); }
    }

    private static void ValidateDimensions(IntPtr dc, double width, double height)
    {
        int dpiX = GetDeviceCaps(dc, 88), dpiY = GetDeviceCaps(dc, 90);
        double actualWidth = GetDeviceCaps(dc, 110) * 25.4 / dpiX;
        double actualHeight = GetDeviceCaps(dc, 111) * 25.4 / dpiY;
        if (dpiX <= 0 || dpiY <= 0 || Math.Abs(actualWidth - width) > 1 || Math.Abs(actualHeight - height) > 1)
            throw new PrintPreparationException($"تعريف الطابعة لم يقبل الأبعاد {width:0.0}×{height:0.0} مم؛ أعاد {actualWidth:0.0}×{actualHeight:0.0} مم.");
    }

    private static Rect Bounds(IntPtr dc, PrintPageOptions? page)
    {
        int dpiX = GetDeviceCaps(dc, 88), dpiY = GetDeviceCaps(dc, 90);
        int offsetX = GetDeviceCaps(dc, 112), offsetY = GetDeviceCaps(dc, 113);
        int Px(double mm, int dpi) => (int)Math.Round(mm * dpi / 25.4);
        var bounds = new Rect
        {
            Left = Math.Max(0, Px(page?.Margins?.LeftMm ?? 3, dpiX) - offsetX),
            Top = Math.Max(0, Px(page?.Margins?.TopMm ?? 3, dpiY) - offsetY),
            Right = Math.Min(GetDeviceCaps(dc, 8), GetDeviceCaps(dc, 110) - Px(page?.Margins?.RightMm ?? 3, dpiX) - offsetX),
            Bottom = Math.Min(GetDeviceCaps(dc, 10), GetDeviceCaps(dc, 111) - Px(page?.Margins?.BottomMm ?? 3, dpiY) - offsetY)
        };
        if (dpiX <= 0 || dpiY <= 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            throw new PrintPreparationException("الهوامش لا تترك مساحة قابلة للطباعة على الورق الحالي.");
        return bounds;
    }

    internal static (float Left, float Top, float Width, float Height) DrawingBounds(IntPtr dc, PrintMargins margins)
    {
        var bounds = Bounds(dc, new() { Margins = margins });
        float unitsX = 100f / GetDeviceCaps(dc, 88), unitsY = 100f / GetDeviceCaps(dc, 90);
        return (bounds.Left * unitsX, bounds.Top * unitsY,
            (bounds.Right - bounds.Left) * unitsX, (bounds.Bottom - bounds.Top) * unitsY);
    }

    private static void Check(int result, string operation)
    {
        if (result <= 0) throw RawPrinterHelper.Error(operation);
    }
}
