using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Quartz;
using SharpIppNextServer.Models;
using SharpIppNextServer.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .Configure<KestrelServerOptions>(options => options.AllowSynchronousIO = true)
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
    $"/{printerOptions.Name}",
    "/ipp/printer",
    $"/ipp/printer/{printerOptions.Name}"
}.ForEach(path => app.MapPost(path, async (HttpContext context, PrinterService printerService) =>
{
    context.Response.ContentType = "application/ipp";
    await printerService.ProcessRequestAsync(context.Request.Body, context.Response.Body);
}));
app.Run();