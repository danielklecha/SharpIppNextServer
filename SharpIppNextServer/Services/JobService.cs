using Microsoft.AspNetCore.StaticFiles;
using Quartz;
using SharpIpp.Models;
using SharpIpp.Protocol.Models;
using System.IO.Abstractions;

namespace SharpIppNextServer.Services;

public class JobService(
    PrinterService printerService,
    IWebHostEnvironment env,
    IFileSystem fileSystem) : IJob
{
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

    public async Task Execute(IJobExecutionContext context)
    {
        var job = await printerService.GetPendingJobAsync();
        if (job == null)
            return;
        try
        {
            for (var i = 0; i < job.Requests.Count; i++)
            {
                var prefix = $"{job.Id}.{i}";
                switch (job.Requests[i])
                {
                    case PrintJobRequest printJobRequest:
                        await SaveAsync(prefix, printJobRequest);
                        break;
                    case SendDocumentRequest sendJobRequest:
                        await SaveAsync(prefix, sendJobRequest);
                        break;
                    case SendUriRequest sendUriRequest:
                        await SaveAsync(prefix, sendUriRequest);
                        break;
                }
            }
            await printerService.AddCompletedJobAsync(job.Id);
        }
        catch (Exception ex)
        {
            await printerService.AddAbortedJobAsync(job.Id, ex);
        }
    }

    private async Task SaveAsync(string prefix, PrintJobRequest request)
    {
        if (request.Document == null)
            return;
        request.Document.Seek(0, SeekOrigin.Begin);
        await SaveAsync(request.Document, GetFileName(prefix, request.DocumentAttributes));
        await request.Document.DisposeAsync();
    }

    private async Task SaveAsync(string prefix, SendDocumentRequest request)
    {
        if (request.Document == null)
            return;
        request.Document.Seek(0, SeekOrigin.Begin);
        await SaveAsync(request.Document, GetFileName(prefix, request.DocumentAttributes));
        await request.Document.DisposeAsync();
    }

    private async Task SaveAsync(string prefix, SendUriRequest request)
    {
        if (request.DocumentUri == null)
            return;
        using var client = new HttpClient();
        using var result = await client.GetAsync(request.DocumentUri);
        if (!result.IsSuccessStatusCode)
            return;
        using var stream = await result.Content.ReadAsStreamAsync();
        await SaveAsync(stream, GetFileName(prefix, request.DocumentAttributes, fileSystem.Path.GetFileNameWithoutExtension(request.DocumentUri.LocalPath), fileSystem.Path.GetExtension(request.DocumentUri.LocalPath)));
    }

    private string GetFileName(string prefix, DocumentAttributes? documentAttributes, string? alternativeDocumentName = null, string? alternativeExtension = null)
    {
        var extension = documentAttributes?.DocumentFormat == null
            ? null
            : _contentTypeProvider.Mappings.Where(x => x.Value == documentAttributes.DocumentFormat).Select(x => x.Key).FirstOrDefault();
        return $"{prefix}_{documentAttributes?.DocumentName ?? alternativeDocumentName ?? "no-name"}{extension ?? alternativeExtension ?? ".unknown"}";
    }

    private async Task SaveAsync(Stream stream, string fileName)
    {
        var path = fileSystem.Path.Combine(env.ContentRootPath, "jobs", fileName);
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(env.ContentRootPath, "jobs"));
        using var fileStream = fileSystem.FileStream.New(path, FileMode.OpenOrCreate);
        await stream.CopyToAsync(fileStream);
    }
}