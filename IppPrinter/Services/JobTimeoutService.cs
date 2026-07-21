using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IppPrinter.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public class JobTimeoutService(
    PrinterService printerService,
    IDateTimeOffsetProvider dateTimeOffsetProvider,
    IOptions<PrinterOptions> printerOptions,
    ILogger<JobTimeoutService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job Timeout Service started.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckTimeoutAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking job timeouts");
            }
        }
    }

    private async Task CheckTimeoutAsync()
    {
        var options = printerOptions.Value;
        var now = dateTimeOffsetProvider.UtcNow;
        var timeoutLimit = TimeSpan.FromSeconds(options.JobCancelAfter);

        foreach (var job in printerService.ActiveJobs)
        {
            var age = now - job.CreatedDateTime;
            if (age > timeoutLimit)
            {
                logger.LogWarning("Job {JobId} has been active for {Age} and will be canceled (timeout limit is {Timeout}).", 
                    job.Id, age, timeoutLimit);
                await printerService.CancelJobAsync(job.Id);
            }
        }
    }
}
