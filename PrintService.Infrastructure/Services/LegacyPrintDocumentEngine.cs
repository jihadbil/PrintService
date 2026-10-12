using System.Drawing;
using System.Drawing.Printing;
using PrintService.Core.Models;
using System.Runtime.InteropServices;

namespace PrintService.Infrastructure.Services;

/// <summary>Retained compatibility engine. System.Drawing.Printing is not supported in Windows services.</summary>
public static class LegacyPrintDocumentEngine
{
    private static PrintDocument Create(string printer, string title, string? output, PrintPageOptions? page)
    {
        var document = new PrintDocument
        {
            DocumentName = title,
            PrintController = new StandardPrintController()
        };
        document.PrinterSettings.PrinterName = printer;
        try
        {
            int Units(double mm) => (int)Math.Round(mm * 100 / 25.4);
            if (page?.PaperName is string name)
                document.DefaultPageSettings.PaperSize = document.PrinterSettings.PaperSizes.Cast<PaperSize>()
                    .FirstOrDefault(size => string.Equals(size.PaperName, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new PrintPreparationException($"مقاس الورق '{name}' غير مدعوم على هذه الطابعة.");
            if (page?.PaperWidthMm is double width && page.PaperHeightMm is double height)
                document.DefaultPageSettings.PaperSize = new PaperSize("Custom", Units(width), Units(height));
            if (page?.Landscape is bool landscape) document.DefaultPageSettings.Landscape = landscape;
            document.DefaultPageSettings.Margins = new Margins(
                Units(page?.Margins?.LeftMm ?? 3), Units(page?.Margins?.RightMm ?? 3),
                Units(page?.Margins?.TopMm ?? 3), Units(page?.Margins?.BottomMm ?? 3));
            if (page?.PaperName is not null || page?.PaperWidthMm is not null)
            {
                var size = document.DefaultPageSettings.PaperSize;
                IntPtr handle = document.PrinterSettings.GetHdevmode(document.DefaultPageSettings);
                IntPtr mode = GlobalLock(handle);
                try
                {
                    if (mode == IntPtr.Zero) throw new PrintPreparationException("تعذر التحقق من إعدادات الورق.");
                    Win32GdiPrintEngine.ValidateDeviceMode(printer, mode, size.Width * 25.4 / 100,
                        size.Height * 25.4 / 100, document.DefaultPageSettings.Landscape);
                }
                finally { if (mode != IntPtr.Zero) GlobalUnlock(handle); GlobalFree(handle); }
            }
        }
        catch { document.Dispose(); throw; }
        if (output is not null)
        {
            document.PrinterSettings.PrintToFile = true;
            document.PrinterSettings.PrintFileName = output;
        }
        return document;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);

    public static void PrintText(string printer, string text, string title, string? output, CancellationToken ct, PrintPageOptions? pageOptions = null)
    {
        using var document = Create(printer, title, output, pageOptions);
        using var font = new Font("Courier New", 9.5f);
        int next = 0;
        IReadOnlyList<string>? lines = null;
        document.PrintPage += (_, page) =>
        {
            ct.ThrowIfCancellationRequested();
            var graphics = page.Graphics ?? throw new InvalidOperationException("لم يوفر محرك الطباعة سطح رسم.");
            float lineHeight = font.GetHeight(graphics) + 2;
            var bounds = Bounds(page);
            if (bounds.Height < lineHeight) throw new InvalidOperationException("ارتفاع الورق لا يتسع لسطر واحد.");
            using var measuring = (StringFormat)StringFormat.GenericTypographic.Clone();
            measuring.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            lines ??= DocumentLayout.WrapText(text, bounds.Width,
                value => graphics.MeasureString(value, font, int.MaxValue, measuring).Width);
            for (float y = bounds.Top; next < lines.Count && y + lineHeight <= bounds.Bottom; y += lineHeight)
            {
                ct.ThrowIfCancellationRequested();
                using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
                format.FormatFlags |= StringFormatFlags.NoWrap;
                if (DocumentLayout.IsRightToLeft(lines[next])) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                graphics.DrawString(lines[next++], font, Brushes.Black,
                    new RectangleF(bounds.Left, y, bounds.Width, lineHeight), format);
            }
            page.HasMorePages = next < lines.Count;
        };
        ct.ThrowIfCancellationRequested();
        document.Print();
    }

    public static void PrintImage(string printer, byte[] bytes, string title, string? output, CancellationToken ct, PrintPageOptions? pageOptions = null)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        using var document = Create(printer, title, output, pageOptions);
        document.PrintPage += (_, page) =>
        {
            ct.ThrowIfCancellationRequested();
            var graphics = page.Graphics ?? throw new InvalidOperationException("لم يوفر محرك الطباعة سطح رسم.");
            var bounds = Bounds(page);
            var fit = DocumentLayout.FitImage(image.Width, image.Height, (int)bounds.Width, (int)bounds.Height);
            graphics.DrawImage(image, bounds.Left + (bounds.Width - fit.Width) / 2,
                bounds.Top + (bounds.Height - fit.Height) / 2, fit.Width, fit.Height);
            page.HasMorePages = false;
        };
        ct.ThrowIfCancellationRequested();
        document.Print();
    }

    private static RectangleF Bounds(PrintPageEventArgs page)
    {
        var graphics = page.Graphics ?? throw new InvalidOperationException("لم يوفر محرك الطباعة سطح رسم.");
        var margins = page.PageSettings.Margins;
        IntPtr dc = graphics.GetHdc();
        try
        {
            var bounds = Win32GdiPrintEngine.DrawingBounds(dc, new()
            {
                LeftMm = margins.Left * 25.4 / 100, RightMm = margins.Right * 25.4 / 100,
                TopMm = margins.Top * 25.4 / 100, BottomMm = margins.Bottom * 25.4 / 100
            });
            return new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
        finally { graphics.ReleaseHdc(dc); }
    }
}
