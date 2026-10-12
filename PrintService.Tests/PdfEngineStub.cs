namespace PrintService.Tests;

// The test executable doubles as an isolated PDF process fixture. It never prints.
internal static class PdfEngineStub
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--verify-page-options"])
        {
            string printer = "Microsoft Print to PDF";
            if (!(await new PrintService.Infrastructure.Services.WindowsPrinterCatalog().GetPrintersAsync(default)).Any(p => p.Name == printer && p.IsReady))
                throw new InvalidOperationException("The Microsoft Print to PDF test printer is not ready.");
            string root = Path.Combine(Path.GetTempPath(), "PrintService.PageVerification", Guid.NewGuid().ToString("N"));
            var results = new List<object>();
            foreach (string engine in new[] { "PrintDocument", "Win32Gdi" })
            {
                var backend = new PrintService.Infrastructure.Services.WindowsPrintBackend(new() { DataDirectory = root, DocumentEngine = engine });
                (string Name, PrintService.Core.Models.PrintPageOptions? Page)[] cases =
                [
                    ("DefaultBefore", null),
                    ("Custom80x200", new() { PaperWidthMm = 80, PaperHeightMm = 200, Landscape = false,
                        Margins = new() { LeftMm = 2, RightMm = 4, TopMm = 5, BottomMm = 6 } }),
                    ("A5Landscape", new() { PaperName = "A5", Landscape = true, Margins = new() { LeftMm = 7, TopMm = 10 } }),
                    ("DefaultAfter", null)
                ];
                foreach (var scenario in cases)
                {
                    var job = new PrintService.Core.Models.PrintJobRequest
                    {
                        SystemId = "verification", DocumentType = PrintService.Core.Models.PrintDocumentType.Text,
                        Page = scenario.Page, JobTitle = "Per-job page verification"
                    };
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes("فاتورة اختبار\nPaper options apply to this job only.\n" +
                        string.Join(" ", Enumerable.Repeat("العربية وEnglish للتحقق من التفاف النص", 8)));
                    try
                    {
                        var result = await backend.SubmitAsync(job, bytes, printer, default);
                        results.Add(new { Engine = engine, Scenario = scenario.Name, result.OutputFilePath, Unsupported = false });
                    }
                    catch (PrintService.Infrastructure.Services.PrintPreparationException) when (scenario.Name == "Custom80x200")
                    {
                        results.Add(new { Engine = engine, Scenario = scenario.Name, OutputFilePath = (string?)null, Unsupported = true });
                    }
                }
            }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results));
            return 0;
        }
        if (args is ["--verify-virtual-printer"])
        {
            string printer = "Microsoft Print to PDF";
            var catalog = new PrintService.Infrastructure.Services.WindowsPrinterCatalog();
            if (!(await catalog.GetPrintersAsync(default)).Any(p => p.Name == printer && p.IsReady))
                throw new InvalidOperationException("The Microsoft Print to PDF test printer is not ready.");
            string root = Path.Combine(Path.GetTempPath(), "PrintService.Verification", Guid.NewGuid().ToString("N"));
            var results = new List<object>();
            foreach (string engine in new[] { "PrintDocument", "Win32Gdi" })
            {
                var backend = new PrintService.Infrastructure.Services.WindowsPrintBackend(new()
                    { DataDirectory = root, DocumentEngine = engine });
                string text = string.Join(" ", Enumerable.Repeat("التحقق من التفاف السطر الطويل مع العربية وEnglish دون قص المحتوى.", 8)) + "\n" +
                    string.Join('\n', Enumerable.Range(1, 100).Select(i =>
                    $"{i}: فاتورة اختبار الطباعة العربية — الإجمالي 150 د.ل / PrintService long line wrapping verification."));
                var job = new PrintService.Core.Models.PrintJobRequest
                    { DocumentType = PrintService.Core.Models.PrintDocumentType.Text, JobTitle = "PrintService verification" };
                var result = await backend.SubmitAsync(job, System.Text.Encoding.UTF8.GetBytes(text), printer, default);
                results.Add(new { Engine = engine, Type = "Text", result.OutputFilePath, result.SpoolerJobId });
                using var image = new SkiaSharp.SKBitmap(400, 200);
                using (var canvas = new SkiaSharp.SKCanvas(image))
                {
                    canvas.Clear(SkiaSharp.SKColors.White);
                    using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.DarkBlue };
                    canvas.DrawRect(0, 0, 400, 200, paint);
                    paint.Color = SkiaSharp.SKColors.White; canvas.DrawCircle(200, 100, 75, paint);
                }
                using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                job.DocumentType = PrintService.Core.Models.PrintDocumentType.Image;
                result = await backend.SubmitAsync(job, encoded.ToArray(), printer, default);
                results.Add(new { Engine = engine, Type = "Image", result.OutputFilePath, result.SpoolerJobId });
            }
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results));
            return 0;
        }
        if (args.Length < 5 || args[0] != "-print-to" || !File.Exists(args[^1])) return 9;
        if (args[1] == "Timeout") await Task.Delay(TimeSpan.FromSeconds(30));
        return args[1] == "Fail" ? 7 : 0;
    }
}
