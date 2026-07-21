using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IppPrinter.Services;

public class JobProcessorService(
    JobQueue jobQueue,
    JobService jobService,
    ILogger<JobProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job Processor Service started.");
        jobService.VerifyJobsPathAccess();
        try
        {
            await foreach (var jobId in jobQueue.Reader.ReadAllAsync(stoppingToken))
            {
                logger.LogInformation("Processing Job {JobId} from queue", jobId);
                try
                {
                    await jobService.ProcessJobAsync(jobId, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing job {JobId}", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Job Processor Service is stopping.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception in JobProcessorService");
        }
    }
}
