using SharpIpp;
using SharpIpp.Protocol.Models;
using System.Collections.Concurrent;
using SharpIpp.Protocol;
using SharpIpp.Models;
using Microsoft.Extensions.Options;
using SharpIpp.Exceptions;
using SharpIppNextServer.Models;

namespace SharpIppNextServer.Services;

public class PrinterService(
    ISharpIppServer sharpIppServer,
    IHttpContextAccessor httpContextAccessor,
    ILogger<PrinterService> logger,
    IOptions<PrinterOptions> printerOptions,
    IDateTimeOffsetProvider dateTimeOffsetProvider) : IDisposable, IAsyncDisposable
{
    private bool disposedValue;
    private int _newJobIndex = dateTimeOffsetProvider.UtcNow.Day * 1000;
    private bool _isPaused;
    private readonly ConcurrentDictionary<int, PrinterJob> _jobs = new();
    private readonly DateTimeOffset _startTime = dateTimeOffsetProvider.UtcNow.AddMinutes(-1);

    private int GetNextValue()
    {
        return Interlocked.Increment(ref _newJobIndex);
    }

    public async Task ProcessRequestAsync(Stream inputStream, Stream outputStream)
    {
        try
        {
            IIppRequest request = await sharpIppServer.ReceiveRequestAsync(inputStream);
            IIppResponseMessage response = await GetResponseAsync(request);
            IIppResponseMessage rawResponse = await sharpIppServer.CreateRawResponseAsync(response);
            ImproveRawResponse(request, rawResponse);
            await sharpIppServer.SendRawResponseAsync(response, outputStream);
        }
        catch (IppRequestException ex)
        {
            logger.LogError(ex, "Unable to process request");
            var response = new IppResponseMessage
            {
                RequestId = ex.RequestMessage.RequestId,
                Version = ex.RequestMessage.Version,
                StatusCode = ex.StatusCode
            };
            var operation = new IppSection { Tag = SectionTag.OperationAttributesTag };
            operation.Attributes.Add(new IppAttribute(Tag.Charset, JobAttribute.AttributesCharset, "utf-8"));
            operation.Attributes.Add(new IppAttribute(Tag.NaturalLanguage, JobAttribute.AttributesNaturalLanguage, "en"));
            response.Sections.Add(operation);
            await sharpIppServer.SendRawResponseAsync(response, outputStream);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to process request");
            if (httpContextAccessor.HttpContext != null)
                httpContextAccessor.HttpContext.Response.StatusCode = 500;
        }
    }

    private async Task<IIppResponseMessage> GetResponseAsync(IIppRequest request)
    {
        return request switch
        {
            CancelJobRequest x => await GetCancelJobResponseAsync(x),
            CreateJobRequest x => GetCreateJobResponse(x),
            CUPSGetPrintersRequest x => GetCUPSGetPrintersResponse(x),
            GetJobAttributesRequest x => GetGetJobAttributesResponse(x),
            GetJobsRequest x => GetGetJobsResponse(x),
            GetPrinterAttributesRequest x => GetGetPrinterAttributesResponse(x),
            HoldJobRequest x => await GetHoldJobResponseAsync(x),
            PausePrinterRequest x => GetPausePrinterResponse(x),
            PrintJobRequest x => await GetPrintJobResponseAsync(x),
            PrintUriRequest x => GetPrintUriResponse(x),
            PurgeJobsRequest x => await GetPurgeJobsResponseAsync(x),
            ReleaseJobRequest x => await GetReleaseJobResponseAsync(x),
            RestartJobRequest x => await GetRestartJobResponseAsync(x),
            ResumePrinterRequest x => GetResumePrinterResponse(x),
            SendDocumentRequest x => await GetSendDocumentResponseAsync(x),
            SendUriRequest x => await GetSendUriResponseAsync(x),
            ValidateJobRequest x => GetValidateJobResponse(x),
            _ => throw new NotImplementedException()
        };
    }

    private void ImproveRawResponse(IIppRequest request, IIppResponseMessage rawResponse)
    {
        switch(request)
        {
            case GetPrinterAttributesRequest x:
                ImproveGetPrinterAttributesRawResponse(x, rawResponse);
                break;
        }
    }

    private void ImproveGetPrinterAttributesRawResponse(GetPrinterAttributesRequest request, IIppResponseMessage rawResponse)
    {
        if (request.OperationAttributes is null
            || request.OperationAttributes.RequestedAttributes is null || request.OperationAttributes.RequestedAttributes.Length == 0
            || request.OperationAttributes.RequestedAttributes.All(x => x == string.Empty)
            || request.OperationAttributes.RequestedAttributes.Any(x => x == "all"))
            return;
        var section = rawResponse.Sections.FirstOrDefault(x => x.Tag == SectionTag.PrinterAttributesTag);
        if(section is null)
            return;
        foreach (var attributeName in request.OperationAttributes.RequestedAttributes.Where(x => !string.IsNullOrEmpty(x)))
        {
            var attribute = section.Attributes.FirstOrDefault(x => x.Name == attributeName);
            if (attribute is not null)
                continue;
            section.Attributes.Add(new IppAttribute(Tag.NoValue, attributeName, NoValue.Instance));
            logger.LogDebug("{name} attribute has been added with no value.", attributeName);
        }
    }

    private ValidateJobResponse GetValidateJobResponse(ValidateJobRequest request)
    {
        logger.LogInformation("Job has been validated");
        return new ValidateJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private async Task<SendUriResponse> GetSendUriResponseAsync(SendUriRequest request)
    {
        var response = new SendUriResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        response.JobId = jobId.Value;
        response.JobUri = $"{GetPrinterUrl()}/{jobId.Value}";
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (request.OperationAttributes?.LastDocument ?? false)
        {
            if (!await copy.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
                return response;
            logger.LogInformation("Job {id} has been moved to queue", job.Id);
        }
        FillWithDefaultValues(request.OperationAttributes ??= new());
        job.Requests.Add(request);
        logger.LogInformation("Document has been added to job {id}", job.Id);
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        return response;
    }

    private async Task<SendDocumentResponse> GetSendDocumentResponseAsync(SendDocumentRequest request)
    {
        var response = new SendDocumentResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        response.JobId = jobId.Value;
        response.JobUri = $"{GetPrinterUrl()}/{jobId.Value}";
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (request.OperationAttributes?.LastDocument ?? false)
        {
            if (!await copy.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
                return response;
            logger.LogInformation("Job {id} has been moved to queue", job.Id);
        }
        FillWithDefaultValues(request.OperationAttributes ??= new());
        job.Requests.Add(request);
        logger.LogInformation("Document has been added to job {id}", job.Id);
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.JobState = JobState.Pending;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        return response;
    }

    private ReleaseJobResponse GetResumePrinterResponse(ResumePrinterRequest request)
    {
        _isPaused = false;
        logger.LogInformation("Printer has been resumed");
        return new ReleaseJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version
        };
    }

    private async Task<ReleaseJobResponse> GetRestartJobResponseAsync(RestartJobRequest request)
    {
        var response = new ReleaseJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been restarted", jobId);
        return response;
    }

    private async Task<ReleaseJobResponse> GetReleaseJobResponseAsync(ReleaseJobRequest request)
    {
        var response = new ReleaseJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been released", jobId);
        return response;
    }

    private async Task<PurgeJobsResponse> GetPurgeJobsResponseAsync(PurgeJobsRequest request)
    {
        foreach (var id in _jobs.Values.Where(x => x.State != JobState.Processing).Select(x => x.Id))
        {
            if (_jobs.TryRemove(id, out var job))
                await job.DisposeAsync();
        }
        logger.LogInformation("System purged jobs");
        return new PurgeJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private PrintUriResponse GetPrintUriResponse(PrintUriRequest request)
    {
        var response = new PrintUriResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            JobState = JobState.Pending,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow);
        response.JobId = job.Id;
        response.JobUri = $"{GetPrinterUrl()}/{job.Id}";
        FillWithDefaultValues(job.Id, request.OperationAttributes ??= new());
        FillWithDefaultValues(request.JobTemplateAttributes ??= new());
        job.Requests.Add(request);
        if (!_jobs.TryAdd(job.Id, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been added to queue", job.Id);
        return response;
    }

    private PausePrinterResponse GetPausePrinterResponse(PausePrinterRequest request)
    {
        _isPaused = true;
        logger.LogInformation("Printer has been paused");
        return new PausePrinterResponse
        {
            RequestId = request.RequestId,
            Version = request.Version
        };
    }

    private async Task<HoldJobResponse> GetHoldJobResponseAsync(HoldJobRequest request)
    {
        var response = new HoldJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(null, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been held", jobId);
        return response;
    }

    private GetPrinterAttributesResponse GetGetPrinterAttributesResponse(GetPrinterAttributesRequest request)
    {
        var options = printerOptions.Value;
        var allAttributes = PrinterAttribute.GetAttributes(request.Version).ToList();
        bool IsRequired(string attributeName)
        {
        if (request.OperationAttributes is null)
            return true;
        if (request.OperationAttributes.RequestedAttributes is null || request.OperationAttributes.RequestedAttributes.Length == 0)
            return true;
        if (request.OperationAttributes.RequestedAttributes.All(x => x == string.Empty))
            return true;
        return request.OperationAttributes.RequestedAttributes.Contains(attributeName);
    }
        logger.LogInformation("System returned printer attributes");
        return new GetPrinterAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            PrinterState = !IsRequired(PrinterAttribute.PrinterState)
                ? null
                : _jobs.Values.Any(x => x.State == JobState.Pending || x.State == JobState.Processing) ? PrinterState.Processing : PrinterState.Idle,
            PrinterStateReasons = !IsRequired(PrinterAttribute.PrinterStateReasons) ? null : ["none"],
            CharsetConfigured = !IsRequired(PrinterAttribute.CharsetConfigured) ? null : "utf-8",
            CharsetSupported = !IsRequired(PrinterAttribute.CharsetSupported) ? null : ["utf-8"],
            NaturalLanguageConfigured = !IsRequired(PrinterAttribute.NaturalLanguageConfigured) ? null : "en-us",
            GeneratedNaturalLanguageSupported = !IsRequired(PrinterAttribute.GeneratedNaturalLanguageSupported) ? null : ["en-us"],
            PrinterIsAcceptingJobs = !IsRequired(PrinterAttribute.PrinterIsAcceptingJobs) ? null : true,
            PrinterMakeAndModel = !IsRequired(PrinterAttribute.PrinterMakeAndModel) ? null : options.Name,
            PrinterName = !IsRequired(PrinterAttribute.PrinterName) ? null : options.Name,
            PrinterInfo = !IsRequired(PrinterAttribute.PrinterInfo) ? null : options.Name,
            IppVersionsSupported = !IsRequired(PrinterAttribute.IppVersionsSupported) ? null : [new IppVersion(1, 0), IppVersion.V1_1, new IppVersion(2, 0)],
            DocumentFormatDefault = !IsRequired(PrinterAttribute.DocumentFormatDefault) ? null : options.DocumentFormat,
            ColorSupported = !IsRequired(PrinterAttribute.ColorSupported) ? null : true,
            PrinterCurrentTime = !IsRequired(PrinterAttribute.PrinterCurrentTime) ? null : dateTimeOffsetProvider.Now,
            OperationsSupported = !IsRequired(PrinterAttribute.OperationsSupported) ? null :
            [
                IppOperation.PrintJob,
                IppOperation.PrintUri,
                IppOperation.ValidateJob,
                IppOperation.CreateJob,
                IppOperation.SendDocument,
                IppOperation.SendUri,
                IppOperation.CancelJob,
                IppOperation.GetJobAttributes,
                IppOperation.GetJobs,
                IppOperation.GetPrinterAttributes,
                IppOperation.HoldJob,
                IppOperation.ReleaseJob,
                IppOperation.RestartJob,
                IppOperation.PausePrinter,
                IppOperation.ResumePrinter
            ],
            QueuedJobCount = !IsRequired(PrinterAttribute.QueuedJobCount) ? null : _jobs.Values.Where(x => x.State == JobState.Pending || x.State == JobState.Processing).Count(),
            DocumentFormatSupported = !IsRequired(PrinterAttribute.DocumentFormatSupported) ? null : [options.DocumentFormat],
            MultipleDocumentJobsSupported = !IsRequired(PrinterAttribute.MultipleDocumentJobsSupported) ? null : true,
            CompressionSupported = !IsRequired(PrinterAttribute.CompressionSupported) ? null : [Compression.None],
            PrinterLocation = !IsRequired(PrinterAttribute.PrinterLocation) ? null : "Internet",
            PrintScalingDefault = !IsRequired(PrinterAttribute.PrintScalingDefault) ? null : options.PrintScaling.FirstOrDefault(),
            PrintScalingSupported = !IsRequired(PrinterAttribute.PrintScalingSupported) ? null : options.PrintScaling,
            PrinterUriSupported = !IsRequired(PrinterAttribute.PrinterUriSupported) ? null : [GetPrinterUrl()],
            UriAuthenticationSupported = !IsRequired(PrinterAttribute.UriAuthenticationSupported) ? null : [UriAuthentication.None],
            UriSecuritySupported = !IsRequired(PrinterAttribute.UriSecuritySupported) ? null : [GetUriSecuritySupported()],
            PrinterUpTime = !IsRequired(PrinterAttribute.PrinterUpTime) ? null : (int)(dateTimeOffsetProvider.UtcNow - _startTime).TotalSeconds,
            MediaDefault = !IsRequired(PrinterAttribute.MediaDefault) ? null : options.Media.FirstOrDefault(),
            MediaSupported = !IsRequired(PrinterAttribute.MediaSupported) ? null : options.Media,
            SidesDefault = !IsRequired(PrinterAttribute.SidesDefault) ? null : options.Sides.FirstOrDefault(),
            SidesSupported = !IsRequired(PrinterAttribute.SidesSupported) ? null : Enum.GetValues(typeof(Sides)).Cast<Sides>().Where(x => x != Sides.Unsupported).ToArray(),
            PdlOverrideSupported = !IsRequired(PrinterAttribute.PdlOverrideSupported) ? null : "attempted",
            MultipleOperationTimeOut = !IsRequired(PrinterAttribute.MultipleOperationTimeOut) ? null : 120,
            FinishingsDefault = !IsRequired(PrinterAttribute.FinishingsDefault) ? null : options.Finishings.FirstOrDefault(),
            FinishingsSupported = !IsRequired(PrinterAttribute.SidesSupported) ? null : options.Finishings,
            PrinterResolutionDefault = !IsRequired(PrinterAttribute.PrinterResolutionDefault) ? null : options.Resolution.FirstOrDefault(),
            PrinterResolutionSupported = !IsRequired(PrinterAttribute.PrinterResolutionSupported) ? null : [options.Resolution.FirstOrDefault()],
            PrintQualityDefault = !IsRequired(PrinterAttribute.PrintQualityDefault) ? null : options.PrintQuality.FirstOrDefault(),
            PrintQualitySupported = !IsRequired(PrinterAttribute.PrintQualitySupported) ? null : options.PrintQuality,
            JobPriorityDefault = !IsRequired(PrinterAttribute.JobPriorityDefault) ? null : options.JobPriority,
            JobPrioritySupported = !IsRequired(PrinterAttribute.JobPrioritySupported) ? null : options.JobPriority,
            CopiesDefault = !IsRequired(PrinterAttribute.CopiesDefault) ? null : options.Copies,
            CopiesSupported = !IsRequired(PrinterAttribute.CopiesSupported) ? null : new SharpIpp.Protocol.Models.Range(options.Copies, options.Copies),
            OrientationRequestedDefault = !IsRequired(PrinterAttribute.OrientationRequestedDefault) ? null : options.Orientation,
            OrientationRequestedSupported = !IsRequired(PrinterAttribute.OrientationRequestedSupported) ? null : Enum.GetValues(typeof(Orientation)).Cast<Orientation>().Where(x => x != Orientation.Unsupported).ToArray(),
            PageRangesSupported = !IsRequired(PrinterAttribute.PageRangesSupported) ? null : false,
            PagesPerMinute = !IsRequired(PrinterAttribute.PagesPerMinute) ? null : 20,
            PagesPerMinuteColor = !IsRequired(PrinterAttribute.PagesPerMinuteColor) ? null : 20,
            PrinterMoreInfo = !IsRequired(PrinterAttribute.PrinterMoreInfo) ? null : GetPrinterMoreInfo(),
            JobHoldUntilSupported = !IsRequired(PrinterAttribute.JobHoldUntilSupported) ? null : [JobHoldUntil.NoHold],
            JobHoldUntilDefault = !IsRequired(PrinterAttribute.JobHoldUntilDefault) ? null : JobHoldUntil.NoHold,
            ReferenceUriSchemesSupported = !IsRequired(PrinterAttribute.ReferenceUriSchemesSupported) ? null : [UriScheme.Ftp, UriScheme.Http, UriScheme.Https],
            OutputBinDefault = !IsRequired(PrinterAttribute.OutputBinDefault) ? null : options.OutputBin.FirstOrDefault(),
            OutputBinSupported = !IsRequired(PrinterAttribute.OutputBinSupported) ? null : options.OutputBin,
            MediaColDefault = !IsRequired(PrinterAttribute.MediaColDefault) ? null : new MediaCol
            {
                MediaBackCoating = MediaCoating.None,
                MediaBottomMargin = 10,
                MediaColor = "black",
                MediaLeftMargin = 10,
                MediaRightMargin = 10,
                MediaTopMargin = 10,
                MediaFrontCoating = MediaCoating.None,
                MediaGrain = MediaGrain.XDirection,
                MediaHoleCount = 0,
                MediaInfo = "my black color",
                MediaOrderCount = 1
            },
            PrintColorModeDefault = !IsRequired(PrinterAttribute.PrintColorModeDefault) ? null : options.PrintColorModes.FirstOrDefault(),
            PrintColorModeSupported = !IsRequired(PrinterAttribute.PrintColorModeSupported) ? null : options.PrintColorModes
        };
    }

    private UriSecurity GetUriSecuritySupported()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return request.IsHttps ? UriSecurity.Tls : UriSecurity.None;
    }

    private GetJobsResponse GetGetJobsResponse(GetJobsRequest request)
    {
        IEnumerable<PrinterJob> jobs = _jobs.Values;
        jobs = request.OperationAttributes?.WhichJobs switch
        {
            WhichJobs.Completed => jobs.Where(x => x.State == JobState.Completed || x.State == JobState.Aborted || x.State == JobState.Canceled),
            WhichJobs.NotCompleted => jobs.Where(x => x.State == JobState.Processing || x.State == JobState.Pending),
            _ => jobs.Where(x => x.State.HasValue)
        };
        if (request.OperationAttributes?.MyJobs ?? false)
            jobs = jobs.Where(x => x.UserName?.Equals(request.OperationAttributes.RequestingUserName) ?? false);
        jobs = jobs.OrderByDescending(x => x.State).ThenByDescending(x => x.Id);
        if (request.OperationAttributes?.Limit.HasValue ?? false)
            jobs = jobs.Take(request.OperationAttributes.Limit.Value);
        logger.LogInformation("System returned jobs attributes");
        return new GetJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            Jobs = jobs.Select(x => GetJobDescriptionAttributes(x, request.OperationAttributes?.RequestedAttributes, true)).ToArray()
        };
    }

    private GetJobAttributesResponse GetGetJobAttributesResponse(GetJobAttributesRequest request)
    {
        var response = new GetJobAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        response.JobAttributes = GetJobDescriptionAttributes(job, request.OperationAttributes?.RequestedAttributes, false);
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("System returned job attributes for job {id}", jobId);
        return response;
    }

    private JobDescriptionAttributes GetJobDescriptionAttributes(PrinterJob job, string[]? requestedAttributes, bool isBatch)
    {
        var jobAttributes = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.JobTemplateAttributes,
            PrintJobRequest printJobRequest => printJobRequest.JobTemplateAttributes,
            PrintUriRequest printUriRequest => printUriRequest.JobTemplateAttributes,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var jobName = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.OperationAttributes?.JobName,
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.JobName,
            PrintUriRequest printUriRequest => printUriRequest.OperationAttributes?.JobName,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var ippAttributeFidelity = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.OperationAttributes?.IppAttributeFidelity,
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.IppAttributeFidelity,
            PrintUriRequest printUriRequest => printUriRequest.OperationAttributes?.IppAttributeFidelity,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var compression = job.Requests.Select(x => x switch
        {
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.Compression,
            PrintUriRequest printUriRequest => printUriRequest.OperationAttributes?.Compression,
            SendDocumentRequest sendDocumentRequest => sendDocumentRequest.OperationAttributes?.Compression,
            SendUriRequest sendUriRequest => sendUriRequest.OperationAttributes?.Compression,
            _ => null,
        }).FirstOrDefault(x => x != null);


        bool IsRequired(string attributeName)
        {
            if (requestedAttributes is null || requestedAttributes.Length == 0)
                return !isBatch;
            if(requestedAttributes.All(x => x == "all"))
                return true;
            return requestedAttributes.Contains(attributeName);
        }
        var attributes = new JobDescriptionAttributes
        {
            JobId = job.Id,
            JobName = !IsRequired(JobAttribute.JobName) ? null : jobName,
            JobUri = $"{GetPrinterUrl()}/{job.Id}",
            JobPrinterUri = !IsRequired(JobAttribute.JobPrinterUri) ? null : GetPrinterUrl(),
            JobState = !IsRequired(JobAttribute.JobState) ? null : job.State,
            JobStateReasons = !IsRequired(JobAttribute.JobState) ? null : [JobStateReason.None],
            DateTimeAtCreation = !IsRequired(JobAttribute.DateTimeAtCreation) ? null : job.CreatedDateTime,
            TimeAtCreation = !IsRequired(JobAttribute.TimeAtCreation) ? null : (int)(job.CreatedDateTime - _startTime).TotalSeconds,
            DateTimeAtProcessing = !IsRequired(JobAttribute.DateTimeAtProcessing) ? null : job.ProcessingDateTime ?? DateTimeOffset.MinValue,
            TimeAtProcessing = !IsRequired(JobAttribute.TimeAtProcessing) ? null : job.ProcessingDateTime.HasValue ? (int)(job.ProcessingDateTime.Value - _startTime).TotalSeconds : -1,
            DateTimeAtCompleted = !IsRequired(JobAttribute.DateTimeAtCompleted) ? null : job.CompletedDateTime ?? DateTimeOffset.MinValue,
            TimeAtCompleted = !IsRequired(JobAttribute.TimeAtCompleted) ? null : job.CompletedDateTime.HasValue ? (int)(job.CompletedDateTime.Value - _startTime).TotalSeconds : -1,
            JobOriginatingUserName = !IsRequired(JobAttribute.JobOriginatingUserName) ? null : job.UserName,
            JobPrinterUpTime = !IsRequired(JobAttribute.JobPrinterUpTime) ? null : (int)(dateTimeOffsetProvider.UtcNow - _startTime).TotalSeconds
        };
        return attributes;
    }

    private static CUPSGetPrintersResponse GetCUPSGetPrintersResponse(CUPSGetPrintersRequest request)
    {
        return new CUPSGetPrintersResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private CreateJobResponse GetCreateJobResponse(CreateJobRequest request)
    {
        var response = new CreateJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            JobState = JobState.Pending,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobStateReasons = [JobStateReason.None]
        };
        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow);
        response.JobId = job.Id;
        response.JobUri = $"{GetPrinterUrl()}/{job.Id}";
        FillWithDefaultValues(job.Id, request.OperationAttributes ??= new());
        FillWithDefaultValues(request.JobTemplateAttributes ??= new());
        job.Requests.Add(request);
        if (!_jobs.TryAdd(job.Id, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been added to queue", job.Id);
        return response;
    }

    private async Task<CancelJobResponse> GetCancelJobResponseAsync(CancelJobRequest request)
    {
        var response = new CancelJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been canceled", jobId);
        return response;
    }

    public async Task<PrinterJob?> GetPendingJobAsync()
    {
        if (_isPaused)
            return null;
        foreach (var job in _jobs.Values.Where(x => x.State == JobState.Pending).OrderBy(x => x.Id))
        {
            var copy = new PrinterJob(job);
            if (!await copy.TrySetStateAsync(JobState.Processing, dateTimeOffsetProvider.UtcNow))
                continue;
            if (!_jobs.TryUpdate(job.Id, copy, job))
                continue;
            return copy;
        }
        return null;
    }

    public async Task AddCompletedJobAsync(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(JobState.Completed, dateTimeOffsetProvider.UtcNow))
            return;
        if (!_jobs.TryUpdate(jobId, copy, job))
            return;
        logger.LogInformation("Job {id} has been completed", job.Id);
    }

    public async Task AddAbortedJobAsync(int jobId, Exception ex)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(JobState.Aborted, dateTimeOffsetProvider.UtcNow))
            return;
        if (!_jobs.TryUpdate(jobId, copy, job))
            return;
        logger.LogError(ex, "Job {id} has been aborted", job.Id);
    }

    private async Task<PrintJobResponse> GetPrintJobResponseAsync(PrintJobRequest request)
    {
        var response = new PrintJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            JobState = JobState.Pending,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobStateReasons = [JobStateReason.None]
        };
        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow);
        response.JobId = job.Id;
        response.JobUri = $"{GetPrinterUrl()}/{job.Id}";
        FillWithDefaultValues(job.Id, request.OperationAttributes ??= new());
        FillWithDefaultValues(request.JobTemplateAttributes ??= new());
        job.Requests.Add(request);
        if (!await job.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryAdd(job.Id, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been added to queue", job.Id);
        return response;
    }

    private string GetPrinterUrl()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return $"ipp://{request.Host}{request.PathBase}{request.Path}";
    }

    private string GetPrinterMoreInfo()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return $"{request.Scheme}://{request.Host}{request.PathBase}";
    }

    private static int? GetJobId(IIppJobRequest request)
    {
        if(request.OperationAttributes is not JobOperationAttributes jobOperationAttributes)
            return null;
        if (jobOperationAttributes.JobUri != null && int.TryParse(jobOperationAttributes.JobUri.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return jobOperationAttributes.JobId;
    }

    private void FillWithDefaultValues(JobTemplateAttributes? attributes)
    {
        if (attributes == null)
            return;
        var options = printerOptions.Value;
        attributes.PrintScaling ??= options.PrintScaling.FirstOrDefault();
        attributes.Sides ??= options.Sides.FirstOrDefault();
        attributes.Media ??= options.Media.FirstOrDefault();
        attributes.PrinterResolution ??= options.Resolution.FirstOrDefault();
        attributes.Finishings ??= options.Finishings.FirstOrDefault();
        attributes.PrintQuality ??= options.PrintQuality.FirstOrDefault();
        attributes.JobPriority ??= options.JobPriority;
        attributes.Copies ??= options.Copies;
        attributes.OrientationRequested ??= options.Orientation;
        attributes.JobHoldUntil ??= options.JobHoldUntil;
        attributes.PrintColorMode ??= options.PrintColorModes.FirstOrDefault();
    }

    private void FillWithDefaultValues(SendDocumentOperationAttributes? attributes)
    {
        if (attributes is null)
            return;
        var options = printerOptions.Value;
        if (string.IsNullOrEmpty(attributes.DocumentFormat))
            attributes.DocumentFormat = options.DocumentFormat;
    }

    private void FillWithDefaultValues(int jobId, PrintJobOperationAttributes? attributes)
    {
        if (attributes is null)
            return;
        var options = printerOptions.Value;
        if (string.IsNullOrEmpty(attributes.DocumentFormat))
            attributes.DocumentFormat = options.DocumentFormat;
        FillWithDefaultValues(jobId, attributes as CreateJobOperationAttributes);
    }

    private void FillWithDefaultValues(int jobId, CreateJobOperationAttributes? attributes)
    {
        if (attributes is null)
            return;
        if (string.IsNullOrEmpty(attributes.JobName))
            attributes.JobName = $"Job {jobId}";
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        foreach (var job in _jobs.Values)
            await job.DisposeAsync();
        _jobs.Clear();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposedValue)
            return;
        if (disposing)
        {
            foreach (var job in _jobs.Values)
                job.Dispose();
            _jobs.Clear();
        }
        disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
