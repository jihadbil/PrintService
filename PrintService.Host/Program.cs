using System.Text;
using Microsoft.AspNetCore.Mvc;
using PrintService.Core.Interfaces;
using PrintService.Core.Models;
using PrintService.Infrastructure.Services;

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
builder.Services.AddSingleton<IPrinterService, WindowsPrinterService>();

var app = builder.Build();

// تفعيل Middleware الخاص بـ CORS في بداية خط المعالجة للتعامل الفوري مع طلبات الـ Preflight (OPTIONS)
app.UseCors();

// تفعيل استكشاف OpenAPI في بيئة التطوير
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// ------------------------------------------------------------------------------------------
// Endpoints: نقاط النهاية البرمجية للخدمة
// ------------------------------------------------------------------------------------------

/// <summary>
/// الصفحة الرئيسية (GET /)
/// نقطة وصول سريعة تعرض معلومات الخدمة، حالتها، وقائمة الواجهات المتاحة.
/// </summary>
app.MapGet("/", () => Results.Ok(new
{
    service = "Local PrintService Agent",
    status = "Running",
    version = "1.0.0",
    docs = "/openapi/v1.json",
    endpoints = new[]
    {
        "GET  /api/health",
        "GET  /api/printers",
        "GET  /api/printers/default",
        "POST /api/print",
        "POST /api/print/test"
    }
}));

/// <summary>
/// فحص الجاهزية وحالة الاتصال (GET /api/health)
/// يستدعيها تطبيق React للتأكد من أن خدمة الطباعة مثبتة ومشغلة محلياً على جهاز العميل.
/// </summary>
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "PrintService",
    version = "1.0.0",
    timestamp = DateTime.UtcNow,
    os = Environment.OSVersion.VersionString
}));

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
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

/// <summary>
/// اختبار الطابعة الذاتي (POST /api/print/test?printerName=...)
/// يطبع إيصالاً تجريبياً سريعاً للتأكد من اتصال الطابعة وجاهزيتها بدون الحاجة لتجهيز فاتورة حقيقية.
/// </summary>
app.MapPost("/api/print/test", async (string? printerName, IPrinterService printerService, CancellationToken ct) =>
{
    string testContent = $"""
    ================================
       PrintService Test Receipt
    ================================
    Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
    Machine: {Environment.MachineName}
    Status: Printer is Working!
    ================================



    """;

    var bytes = Encoding.UTF8.GetBytes(testContent);
    var request = new PrintJobRequest
    {
        PrinterName = printerName,
        DocumentType = PrintDocumentType.Text,
        ContentBase64 = Convert.ToBase64String(bytes),
        JobTitle = "PrintService_SelfTest"
    };

    var result = await printerService.PrintAsync(request, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

// بدء الاستماع لطلبات الـ HTTP
app.Run();
