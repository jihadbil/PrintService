using System.Text;
using Microsoft.AspNetCore.Mvc;
using PrintService.Core.Interfaces;
using PrintService.Core.Models;
using PrintService.Infrastructure.Services;
using PrintService.Infrastructure;
using System.ServiceProcess;

// ==========================================================================================
// PrintService.Host: خادم وكيل الطباعة المحلي (Local Print Agent)
// يعمل كـ REST API محلي ويستضاف كخدمة ويندوز (Windows Service) للربط بين تطبيقات الويب
// مثل React وطابعات النظام بدون الحاجة لأي تدخل من المستخدم أو ظهور نوافذ معاينة.
// ==========================================================================================

var builder = WebApplication.CreateBuilder(args);

// 1. تفعيل استضافة التطبيق كخدمة ويندوز (Windows Service) لتعمل تلقائياً في الخلفية مع إقلاع النظام
builder.Host.UseWindowsService();

// 2. تفعيل توثيق OpenAPI / Swagger للواجهات البرمجية
builder.Services.AddOpenApi();

var printingOptions = builder.Configuration.GetSection("Printing").Get<PrintingOptions>() ?? new();
printingOptions.Validate();
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize =
    ((printingOptions.MaxDocumentBytes + 2L) / 3 * 4) + 64 * 1024);
builder.Services.AddSingleton(printingOptions);
builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
builder.Services.AddSingleton<IPrintBackend, WindowsPrintBackend>();
builder.Services.AddSingleton<PrintJobStore>();
builder.Services.AddHostedService<PrintService.Host.PrintJobDrainService>();

// 3. إعداد وتفعيل سياسة مشاركة الموارد عبر الأصول (CORS)
// هذا الإعداد جوهري للسماح لتطبيق React (سواء كان يعمل محلياً أو عبر SaaS) بالاتصال بالمنفذ المحلي
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()      // السماح بالطلبات من أي أصل (Origin)
              .AllowAnyHeader()      // السماح بجميع الترويسات (Headers)
              .AllowAnyMethod();     // السماح بجميع أفعال HTTP (GET, POST, OPTIONS, إلخ)
    });
});

// 4. تسجيل خدمة الطابعات كـ Singleton لضمان إعادة استخدام الموارد
builder.Services.AddSingleton<IPrinterService>(services => new WindowsPrinterService(
    services.GetRequiredService<ILogger<WindowsPrinterService>>(),
    services.GetRequiredService<IPrinterCatalog>(), services.GetRequiredService<IPrintBackend>(),
    services.GetRequiredService<PrintJobStore>(), printingOptions,
    services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));

var app = builder.Build();

// Acquire the journal lease at startup, rather than after accepting the first request.
_ = app.Services.GetRequiredService<IPrinterService>();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    await context.Response.WriteAsJsonAsync(new { success = false, message = "تعذر الوصول إلى خدمات الطباعة أو سجل المهام." });
}));

// تفعيل Middleware الخاص بـ CORS في بداية خط المعالجة للتعامل الفوري مع طلبات الـ Preflight (OPTIONS)
app.UseCors();

// تفعيل استكشاف OpenAPI في بيئة التطوير
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// تفعيل تقديم الملفات الثابتة ولوحة التحكم التفاعلية من مجلد wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// ------------------------------------------------------------------------------------------
// Endpoints: نقاط النهاية البرمجية للخدمة
// ------------------------------------------------------------------------------------------

/// <summary>
/// معلومات الخدمة (GET /api)
/// نقطة وصول سريعة تعرض معلومات الخدمة، حالتها، وقائمة الواجهات المتاحة.
/// </summary>
app.MapGet("/api", () => Results.Ok(new
{
    service = "Local PrintService Agent",
    status = "Running",
    version = "1.0.0",
    dashboard = "/",
    docs = app.Environment.IsDevelopment() ? "/openapi/v1.json" : null,
    documentEngine = printingOptions.DocumentEngine,
    maxCopies = printingOptions.MaxCopies,
    maxDocumentBytes = printingOptions.MaxDocumentBytes,
    supportsSystemId = true,
    supportsPageOptions = true,
    endpoints = new[]
    {
        "GET  /api/health",
        "GET  /api/printers",
        "GET  /api/printers/default",
        "GET  /api/jobs/{jobId}",
        "POST /api/print",
        "POST /api/print/test"
    }
}));

/// <summary>
/// فحص الجاهزية وحالة الاتصال (GET /api/health)
/// يستدعيها تطبيق React للتأكد من أن خدمة الطباعة مثبتة ومشغلة محلياً على جهاز العميل.
/// </summary>
app.MapGet("/api/health", async (IPrinterService service, CancellationToken ct) =>
{
    using var spooler = new ServiceController("Spooler");
    bool spoolerRunning = spooler.Status == ServiceControllerStatus.Running;
    int printerCount = 0;
    if (spoolerRunning)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        printerCount = (await service.GetPrintersAsync(timeout.Token).WaitAsync(timeout.Token)).Count;
    }
    bool healthy = spoolerRunning && printerCount > 0;
    return Results.Json(new
    {
        status = healthy ? "Healthy" : "Degraded", service = "PrintService", version = "1.0.0",
        timestamp = DateTime.UtcNow, os = Environment.OSVersion.VersionString,
        spoolerRunning, printerCount, documentEngine = printingOptions.DocumentEngine,
        pdfEngineAvailable = ((WindowsPrintBackend)app.Services.GetRequiredService<IPrintBackend>()).FindPdfExecutable() is not null
    }, statusCode: healthy ? 200 : 503);
});

/// <summary>
/// الاستعلام عن حالة مهمة طباعة بالمعرف (GET /api/jobs/{jobId}?systemId=...)
/// يتيح لتطبيقات الويب متابعة تقدم أمر الطباعة والتأكد من إرسالها دون تكرار.
/// </summary>
app.MapGet("/api/jobs/{jobId}", async (string jobId, string? systemId, IPrinterService service, CancellationToken ct) =>
{
    if (jobId.Length > 128) return Results.BadRequest(new { message = "JobId طويل جداً." });
    if (!PrintJobIdentity.IsValidSystemId(systemId)) return Results.BadRequest(new { message = "SystemId غير صالح." });
    var job = await service.GetJobAsync(jobId, systemId, ct);
    return job is null ? Results.NotFound(new { message = "المهمة غير موجودة." }) : Results.Ok(job);
});

/// <summary>
/// تحويل نتيجة معالجة أمر الطباعة إلى استجابة HTTP متوافقة مع رمز الحالة المناسب (200, 400, 409, 429, 503).
/// </summary>
/// <param name="result">كائن نتيجة معالجة أمر الطباعة.</param>
/// <returns>استجابة IResult بصيغة JSON مع رمز الحالة المطابق.</returns>
static IResult PrintResponse(PrintJobResult result) => Results.Json(result, statusCode: result.Success ? 200 : result.Status switch
{
    "Conflict" => 409,
    "Busy" => 429,
    "Unknown" => 503,
    "Failed" => 503,
    _ => 400
});

/// <summary>
/// استعلام الطابعات المثبتة (GET /api/printers)
/// تعيد قائمة بجميع الطابعات المعرفة في ويندوز مع أحجام الورق المدعومة لكل طابعة،
/// وتُستخدم لملء القوائم المنسدلة في واجهة React لاختيار طابعة الفواتير أو المطبخ.
/// </summary>
app.MapGet("/api/printers", async (IPrinterService printerService, CancellationToken ct) =>
{
    var printers = await printerService.GetPrintersAsync(ct);
    return Results.Ok(printers);
});

/// <summary>
/// استعلام الطابعة الافتراضية (GET /api/printers/default)
/// تعيد بيانات الطابعة الافتراضية للنظام لاستخدامها تلقائياً إذا لم يحدد المستخدم طابعة معينة.
/// </summary>
app.MapGet("/api/printers/default", async (IPrinterService printerService, CancellationToken ct) =>
{
    var defaultPrinter = await printerService.GetDefaultPrinterAsync(ct);
    return defaultPrinter != null
        ? Results.Ok(defaultPrinter)
        : Results.NotFound(new { message = "لا توجد طابعة افتراضية مثبتة في النظام." });
});

/// <summary>
/// تنفيذ أمر طباعة صامت (POST /api/print)
/// تستقبل كائن PrintJobRequest يحتوي على اسم الطابعة، نوع المستند، ومحتواه بـ Base64.
/// تقوم الخدمة بتمريره مباشرة للويندوز بدون أي نوافذ منبثقة.
/// </summary>
app.MapPost("/api/print", async ([FromBody] PrintJobRequest request, IPrinterService printerService, CancellationToken ct) =>
{
    var result = await printerService.PrintAsync(request, ct);
    return PrintResponse(result);
});

/// <summary>
/// اختبار الطابعة الذاتي (POST /api/print/test?printerName=...)
/// يطبع إيصالاً تجريبياً سريعاً للتأكد من اتصال الطابعة وجاهزيتها بدون الحاجة لتجهيز فاتورة حقيقية.
/// </summary>
app.MapPost("/api/print/test", async (string? printerName, string? jobId, string? systemId, IPrinterService printerService, CancellationToken ct) =>
{
    string testContent = $"""
    ================================
       PrintService Test Receipt
    ================================
    Machine: {Environment.MachineName}
    PrintService test page
    ================================



    """;

    var bytes = Encoding.UTF8.GetBytes(testContent);
    var request = new PrintJobRequest
    {
        PrinterName = printerName,
        JobId = jobId ?? Guid.NewGuid().ToString("N"),
        SystemId = systemId,
        DocumentType = PrintDocumentType.Text,
        ContentBase64 = Convert.ToBase64String(bytes),
        JobTitle = "PrintService_SelfTest"
    };

    var result = await printerService.PrintAsync(request, ct);
    return PrintResponse(result);
});

// بدء الاستماع لطلبات الـ HTTP
app.Run();
