using Microsoft.AspNetCore.StaticFiles;
using Quartz;
using SharpIpp.Models;
using SharpIpp.Models.Requests;
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
        if(request.Document.Position > 0)
            request.Document.Seek(0, SeekOrigin.Begin);
        await SaveAsync(request.Document, GetFileName(prefix, request.OperationAttributes?.DocumentName, request.OperationAttributes?.DocumentFormat));
        await request.Document.DisposeAsync();
    }

    private async Task SaveAsync(string prefix, SendDocumentRequest request)
    {
        if (request.Document == null)
            return;
        request.Document.Seek(0, SeekOrigin.Begin);
        await SaveAsync(request.Document, GetFileName(prefix, request.OperationAttributes?.DocumentName, request.OperationAttributes?.DocumentFormat));
        await request.Document.DisposeAsync();
    }

    private async Task SaveAsync(string prefix, SendUriRequest request)
    {
        if (request.OperationAttributes is null || request.OperationAttributes.DocumentUri is null)
            return;
        using var client = new HttpClient();
        using var result = await client.GetAsync(request.OperationAttributes.DocumentUri);
        if (!result.IsSuccessStatusCode)
            return;
        using var stream = await result.Content.ReadAsStreamAsync();
        await SaveAsync(stream, GetFileName(prefix, request.OperationAttributes.DocumentName, request.OperationAttributes.DocumentFormat, fileSystem.Path.GetFileNameWithoutExtension(request.OperationAttributes.DocumentUri.LocalPath), fileSystem.Path.GetExtension(request.OperationAttributes.DocumentUri.LocalPath)));
    }

    private string GetFileName(string prefix, string? documentName, string? documentFormat, string? alternativeDocumentName = null, string? alternativeExtension = null)
    {
        var extension = documentFormat is null
            ? null
            : _contentTypeProvider.Mappings.Where(x => x.Value == documentFormat).Select(x => x.Key).FirstOrDefault();
        return $"{prefix}_{documentName ?? alternativeDocumentName ?? "no-name"}{extension ?? alternativeExtension ?? ".unknown"}";
    }

    private async Task SaveAsync(Stream stream, string fileName)
    {
        var path = fileSystem.Path.Combine(env.ContentRootPath, "jobs", fileName);
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(env.ContentRootPath, "jobs"));
        using var fileStream = fileSystem.FileStream.New(path, FileMode.OpenOrCreate);
        await stream.CopyToAsync(fileStream);
    }
}