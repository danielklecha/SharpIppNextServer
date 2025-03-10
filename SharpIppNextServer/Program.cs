using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Quartz;
using SharpIpp;
using SharpIppNextServer.Models;
using SharpIppNextServer.Services;
using System.IO.Abstractions;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddSingleton<IDateTimeProvider, DateTimeProvider>()
    .AddSingleton<IDateTimeOffsetProvider, DateTimeOffsetProvider>()
    .AddSingleton<ISharpIppServer, SharpIppServer>()
    .AddSingleton<IFileSystem, FileSystem>()
    .Configure<KestrelServerOptions>(options => options.AllowSynchronousIO = true)
    .Configure<IISServerOptions>(options => options.AllowSynchronousIO = true)
    .Configure<PrinterOptions>(builder.Configuration.GetSection("Printer"))
    .AddSingleton<PrinterService>()
    .AddHttpContextAccessor()
    .AddCors()
    .AddQuartz(q =>
    {
        var jobKey = new JobKey("printerQueue");
        q.AddJob<JobService>(opts => opts.WithIdentity(jobKey));
        q.AddTrigger(opts => opts
            .ForJob(jobKey)
            .WithIdentity($"printerQueue-trigger")
            .WithCronSchedule("0/10 * * * * ?"));
    })
    .AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
var app = builder.Build();
var printerOptions = app.Services.GetRequiredService<IOptions<PrinterOptions>>().Value;
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors(x => x.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

app.MapGet("/", () => "IPP printer");
new List<string>
{
    "/",
    "/ipp",
    $"/{printerOptions.Name}",
    "/ipp/printer",
    $"/ipp/printer/{printerOptions.Name}"
}.ForEach(path => app.MapPost(path, async (HttpContext context, PrinterService printerService) =>
{
    context.Response.ContentType = "application/ipp";
    await printerService.ProcessRequestAsync(context.Request.Body, context.Response.Body);
}));
/*
app.MapMethods("/{**catchAll}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD", "TRACE" }, async context =>
{
    await context.Response.WriteAsync("OK");
});
*/
app.Run();