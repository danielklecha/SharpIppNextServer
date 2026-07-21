using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using SharpIpp;
using IppPrinter.Models;
using IppPrinter.Services;
using System.IO.Abstractions;

if (args.Contains("--test"))
{
    var jobsDir = Path.Combine(AppContext.BaseDirectory, "jobs");
    Directory.CreateDirectory(jobsDir);
    var textConverter = new TextConverter();
    var sampleText = "Hello World!\nThis is a line of plain text.\nThis is a very long line of plain text that should be wrapped automatically by the TextConverter when it exceeds the width of the A4 page print margins.";
    var testFilePath = Path.Combine(jobsDir, "test_text.pdf");
    using (var outputStream = File.Create(testFilePath))
    {
        await textConverter.ConvertAsync(System.Text.Encoding.UTF8.GetBytes(sampleText), outputStream);
    }
    Console.WriteLine($"Self-test completed. Generated {testFilePath}.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "IppPrinter";
    });
}
builder.Services
    .AddSingleton<IDateTimeProvider, DateTimeProvider>()
    .AddSingleton<IDateTimeOffsetProvider, DateTimeOffsetProvider>()
    .AddSingleton<ISharpIppServer, SharpIppServer>()
    .AddSingleton<IFileSystem, FileSystem>()
    .Configure<KestrelServerOptions>(options => { })
    .Configure<IISServerOptions>(options => { })
    .Configure<PrinterOptions>(builder.Configuration.GetSection("Printer"))
    .AddSingleton<SubscriptionsService>()
    .AddSingleton<PrinterService>()
    .AddSingleton<IDocumentConverter, PwgRasterConverter>()
    .AddSingleton<IDocumentConverter, UrfRasterConverter>()
    .AddSingleton<IDocumentConverter, ImageConverter>()
    .AddSingleton<IDocumentConverter, TextConverter>()
    .AddSingleton<JobService>()
    .AddSingleton<JobQueue>()
    .AddHostedService<JobProcessorService>()
    .AddHostedService<JobTimeoutService>()
    .AddHttpContextAccessor()
    .AddCors()
    .AddHostedService<PrinterDiscoveryService>();
var app = builder.Build();
var printerOptions = app.Services.GetRequiredService<IOptions<PrinterOptions>>().Value;
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors(x => x.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

new List<string>
{
    "/",                               // Catch-all for basic root-level queries (e.g., when port is specified without a path)
    "/ipp",                            // Standard IPP fallback endpoint used by various print clients
    $"/{printerOptions.Name}",         // Friendly queue name endpoint (e.g., /IppPrinter)
    "/ipp/print",                      // Mopria, AirPrint, and IPP Everywhere standard endpoint
    $"/printers/{printerOptions.Name}" // CUPS-style printer queue path (e.g., /printers/IppPrinter)
}.ForEach(path =>
{
    app.MapGet(path, () => "IPP printer");
    app.MapPost(path, async (HttpContext context, PrinterService printerService) =>
    {
        context.Response.ContentType = "application/ipp";
        await printerService.ProcessRequestAsync(context.Request.Body, context.Response.Body);
    });
});

app.Run();