using Microsoft.Extensions.Options;
using SharpIpp;
using SharpIpp.Exceptions;
using SharpIpp.Models;
using SharpIpp.Models.Requests;
using SharpIpp.Models.Responses;
using SharpIpp.Protocol;
using SharpIpp.Protocol.Models;
using SharpIppNextServer.Models;
using System.Collections.Concurrent;
using System.Text;

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
            IIppResponse response = await GetResponseAsync(request);
            IIppResponseMessage rawResponse = await sharpIppServer.CreateRawResponseAsync(response);
            ImproveRawResponse(request, response, rawResponse);
            await sharpIppServer.SendRawResponseAsync(rawResponse, outputStream);
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
            response.OperationAttributes.Add([
                new IppAttribute(Tag.Charset, IppAttributeNames.AttributesCharset, "utf-8"),
                new IppAttribute(Tag.NaturalLanguage, IppAttributeNames.AttributesNaturalLanguage, "en")]);
            await sharpIppServer.SendRawResponseAsync(response, outputStream);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to process request");
            if (httpContextAccessor.HttpContext != null)
                httpContextAccessor.HttpContext.Response.StatusCode = 500;
        }
    }

    private async Task<IIppResponse> GetResponseAsync(IIppRequest request)
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
            ReleaseJobRequest x => await GetReleaseJobResponseAsync(x),
            ResumePrinterRequest x => GetResumePrinterResponse(x),
            SendDocumentRequest x => await GetSendDocumentResponseAsync(x),
            ValidateJobRequest x => GetValidateJobResponse(x),
            CloseJobRequest x => await GetCloseJobResponseAsync(x),
            IdentifyPrinterRequest x => GetIdentifyPrinterResponse(x),
            ResubmitJobRequest x => await GetResubmitJobResponseAsync(x),
            CancelJobsRequest x => await GetCancelJobsResponseAsync(x),
            CancelMyJobsRequest x => await GetCancelMyJobsResponseAsync(x),
            _ => throw new NotImplementedException()
        };
    }

    private void ImproveRawResponse(IIppRequest request, IIppResponse response, IIppResponseMessage rawResponse)
    {
        switch(request)
        {
            case GetPrinterAttributesRequest x:
                ImproveGetPrinterAttributesRawResponse(x, rawResponse);
                break;
            case CloseJobRequest x when response is CloseJobServerResponse closeJobResponse:
                ImproveCloseJobRawResponse(closeJobResponse, rawResponse);
                break;
            case ResubmitJobRequest x when response is ResubmitJobServerResponse resubmitJobResponse:
                ImproveResubmitJobRawResponse(resubmitJobResponse, rawResponse);
                break;
        }
    }

    private void ImproveGetPrinterAttributesRawResponse(GetPrinterAttributesRequest request, IIppResponseMessage rawResponse)
    {
        var list = rawResponse.PrinterAttributes.FirstOrDefault();
        if(list is null)
            return;
        bool IsRequired(string attributeName) => !list.Any(x => x.Name.Equals(attributeName))
            && IsAttributeRequired(request, attributeName);
        var options = printerOptions.Value;
        if(IsRequired("printer-dns-sd-name"))
            list.Add(new IppAttribute(Tag.NameWithoutLanguage, "printer-dns-sd-name", options.DnsSdName));
        if(IsRequired("printer-make-and-model"))
            list.Add(new IppAttribute(Tag.TextWithoutLanguage, "printer-make-and-model", options.Name));
        if(IsRequired("printer-firmware-name"))
            list.Add(new IppAttribute(Tag.NameWithoutLanguage, "printer-firmware-name", options.FirmwareName));
        if(IsRequired("printer-firmware-string-version"))
            list.Add(new IppAttribute(Tag.TextWithoutLanguage, "printer-firmware-string-version", options.FirmwareName));
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

    private string GetPrinterDeviceId(PrinterOptions options)
    {
        return new StringBuilder()
            .Append($"MFG:{options.Manufacturer};") //Manufacturer
            .Append($"MDL:{options.Model};") //Model
            .Append("CMD:Automatic,JPEG;") //Command Set
            .Append("CLS:PRINTER") //Class
            .Append("DES:SIN1;") //Designator or Description
            .Append($"CID:{options.Model}_1;") //Compatible ID
            .Append("LEDMDIS:USB#FF#CC#00,USB#07#01#02,USB#FF#04#01;") //Legacy Device ID String
            .Append($"SN:{options.SerialNumber};") //Serial Number
            .Append("S:038000C480a00001002c240005ac1400032;") //Status
            .Append("Z:05000008000009,12000,17000000000000,181;") //Vendor-Specific
            .ToString();
    }


    private async Task<SendDocumentResponse> GetSendDocumentResponseAsync(SendDocumentRequest request)
    {
        var response = new SendDocumentResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        response.JobAttributes.JobId = jobId.Value;
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
        response.JobAttributes.JobState = JobState.Pending;
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

    private async Task<IIppResponse> GetCloseJobResponseAsync(CloseJobRequest request)
    {
        var response = new CloseJobServerResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        response.JobId = jobId.Value;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!await copy.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.JobState = JobState.Pending;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been closed", jobId.Value);
        return response;
    }

    private async Task<IIppResponse> GetResubmitJobResponseAsync(ResubmitJobRequest request)
    {
        var response = new ResubmitJobServerResponse
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
        var newJob = new PrinterJob(GetNextValue(), job.UserName, dateTimeOffsetProvider.UtcNow);
        foreach (var r in job.Requests)
        {
            if (r is PrintJobRequest pjr)
            {
                newJob.Requests.Add(pjr);
            }
            else if (r is SendDocumentRequest sdr)
            {
                newJob.Requests.Add(sdr);
            }
        }
        if (!await newJob.TrySetStateAsync(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryAdd(newJob.Id, newJob))
            return response;
        response.NewJobId = newJob.Id;
        response.JobState = JobState.Pending;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been resubmitted as new job {newId}", jobId, newJob.Id);
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

    private async Task<CancelJobsResponse> GetCancelJobsResponseAsync(CancelJobsRequest request)
    {
        foreach (var job in _jobs.Values.Where(x => x.State == JobState.Pending || x.State == JobState.Processing || !x.State.HasValue))
        {
            var copy = new PrinterJob(job);
            if (await copy.TrySetStateAsync(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            {
                _jobs.TryUpdate(job.Id, copy, job);
            }
        }
        logger.LogInformation("System canceled all jobs");
        return new CancelJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private async Task<CancelMyJobsResponse> GetCancelMyJobsResponseAsync(CancelMyJobsRequest request)
    {
        var userName = request.OperationAttributes?.RequestingUserName;
        foreach (var job in _jobs.Values.Where(x => (x.State == JobState.Pending || x.State == JobState.Processing || !x.State.HasValue) && x.UserName == userName))
        {
            var copy = new PrinterJob(job);
            if (await copy.TrySetStateAsync(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            {
                _jobs.TryUpdate(job.Id, copy, job);
            }
        }
        logger.LogInformation("System canceled all jobs for user {user}", userName);
        return new CancelMyJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private IdentifyPrinterResponse GetIdentifyPrinterResponse(IdentifyPrinterRequest request)
    {
        logger.LogInformation("Printer identify requested");
        return new IdentifyPrinterResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
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

    private static bool IsAttributeRequired(GetPrinterAttributesRequest request, string attributeName)
    {
        return request.OperationAttributes is null
                || request.OperationAttributes.RequestedAttributes is null
                || request.OperationAttributes.RequestedAttributes.Length == 0
                || request.OperationAttributes.RequestedAttributes.All(x => x == string.Empty)
                || request.OperationAttributes.RequestedAttributes.Any(x => x.Equals("all", StringComparison.InvariantCultureIgnoreCase))
                || request.OperationAttributes.RequestedAttributes.Contains(attributeName);
    }

    private GetPrinterAttributesResponse GetGetPrinterAttributesResponse(GetPrinterAttributesRequest request)
    {
        var options = printerOptions.Value;
        bool IsRequired(string attributeName) => IsAttributeRequired(request, attributeName);
        logger.LogInformation("System returned printer attributes");
        return new GetPrinterAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            PrinterAttributes = new()
            {
                PrinterState = !IsRequired(IppAttributeNames.PrinterState)
                ? null
                : _isPaused ? PrinterState.Stopped : _jobs.Values.Any(x => x.State == JobState.Pending || x.State == JobState.Processing) ? PrinterState.Processing : PrinterState.Idle,
                PrinterStateReasons = !IsRequired(IppAttributeNames.PrinterStateReasons) ? null : (_isPaused ? ["paused"] : ["none"]),
                CharsetConfigured = !IsRequired(IppAttributeNames.CharsetConfigured) ? null : "utf-8",
                CharsetSupported = !IsRequired(IppAttributeNames.CharsetSupported) ? null : ["utf-8"],
                NaturalLanguageConfigured = !IsRequired(IppAttributeNames.NaturalLanguageConfigured) ? (NaturalLanguage?)null : NaturalLanguage.EnUs,
                GeneratedNaturalLanguageSupported = !IsRequired(IppAttributeNames.GeneratedNaturalLanguageSupported) ? null : ["en-us"],
                PrinterIsAcceptingJobs = !IsRequired(IppAttributeNames.PrinterIsAcceptingJobs) ? null : true,
                PrinterMakeAndModel = !IsRequired(IppAttributeNames.PrinterMakeAndModel) ? null : options.Name,
                PrinterName = !IsRequired(IppAttributeNames.PrinterName) ? null : options.Name,
                PrinterInfo = !IsRequired(IppAttributeNames.PrinterInfo) ? null : options.Name,
                IppVersionsSupported = !IsRequired(IppAttributeNames.IppVersionsSupported) ? null : [new IppVersion(1, 0), new IppVersion(1, 1), new IppVersion(2, 0), new IppVersion(2, 1), new IppVersion(2, 2)],
                DocumentFormatDefault = !IsRequired(IppAttributeNames.DocumentFormatDefault) ? null : options.DocumentFormat,
                ColorSupported = !IsRequired(IppAttributeNames.ColorSupported) ? null : true,
                PrinterCurrentTime = !IsRequired(IppAttributeNames.PrinterCurrentTime) ? null : dateTimeOffsetProvider.Now,
                OperationsSupported = !IsRequired(IppAttributeNames.OperationsSupported) ? null :
                [
                    IppOperation.PrintJob,
                    IppOperation.ValidateJob,
                    IppOperation.CreateJob,
                    IppOperation.SendDocument,
                    IppOperation.CancelJob,
                    IppOperation.GetJobAttributes,
                    IppOperation.GetJobs,
                    IppOperation.GetPrinterAttributes,
                    IppOperation.HoldJob,
                    IppOperation.ReleaseJob,
                    IppOperation.PausePrinter,
                    IppOperation.ResumePrinter,
                    IppOperation.CloseJob,
                    IppOperation.IdentifyPrinter,
                    IppOperation.ResubmitJob,
                    IppOperation.CancelJobs,
                    IppOperation.CancelMyJobs
                ],
                QueuedJobCount = !IsRequired(IppAttributeNames.QueuedJobCount) ? null : _jobs.Values.Where(x => x.State == JobState.Pending || x.State == JobState.Processing).Count(),
                DocumentFormatSupported = !IsRequired(IppAttributeNames.DocumentFormatSupported) ? null : [options.DocumentFormat],
                MultipleDocumentJobsSupported = !IsRequired(IppAttributeNames.MultipleDocumentJobsSupported) ? null : options.MultipleDocumentJobsSupported,
                CompressionSupported = !IsRequired(IppAttributeNames.CompressionSupported) ? null : [Compression.None],
                PrinterLocation = !IsRequired(IppAttributeNames.PrinterLocation) ? null : options.Location,
                PrintScalingDefault = !IsRequired(IppAttributeNames.PrintScalingDefault) ? null : options.PrintScaling.FirstOrDefault(),
                PrintScalingSupported = !IsRequired(IppAttributeNames.PrintScalingSupported) ? null : options.PrintScaling,
                PrinterUriSupported = !IsRequired(IppAttributeNames.PrinterUriSupported) ? null : [GetPrinterUrl("/ipp/print")],
                UriAuthenticationSupported = !IsRequired(IppAttributeNames.UriAuthenticationSupported) ? null : [UriAuthentication.None],
                UriSecuritySupported = !IsRequired(IppAttributeNames.UriSecuritySupported) ? null : [GetUriSecuritySupported()],
                PrinterUpTime = !IsRequired(IppAttributeNames.PrinterUpTime) ? null : (int)(dateTimeOffsetProvider.UtcNow - _startTime).TotalSeconds,
                MediaDefault = !IsRequired(IppAttributeNames.MediaDefault) ? null : options.Media.FirstOrDefault(),
                MediaSupported = !IsRequired(IppAttributeNames.MediaSupported) ? null : options.Media,
                SidesDefault = !IsRequired(IppAttributeNames.SidesDefault) ? null : options.Sides.FirstOrDefault(),
                SidesSupported = !IsRequired(IppAttributeNames.SidesSupported) ? null : Enum.GetValues(typeof(Sides)).Cast<Sides>().ToArray(),
                PdlOverrideSupported = !IsRequired(IppAttributeNames.PdlOverrideSupported) ? null : "attempted",
                MultipleOperationTimeOut = !IsRequired(IppAttributeNames.MultipleOperationTimeOut) ? null : options.MultipleOperationTimeout,
                FinishingsDefault = !IsRequired(IppAttributeNames.FinishingsDefault) ? null : options.Finishings.FirstOrDefault(),
                FinishingsSupported = !IsRequired(IppAttributeNames.FinishingsSupported) ? null : options.Finishings,
                PrinterResolutionDefault = !IsRequired(IppAttributeNames.PrinterResolutionDefault) ? null : options.Resolution.FirstOrDefault(),
                PrinterResolutionSupported = !IsRequired(IppAttributeNames.PrinterResolutionSupported) ? null : options.Resolution,
                PrintQualityDefault = !IsRequired(IppAttributeNames.PrintQualityDefault) ? null : options.PrintQuality.FirstOrDefault(),
                PrintQualitySupported = !IsRequired(IppAttributeNames.PrintQualitySupported) ? null : options.PrintQuality,
                JobPriorityDefault = !IsRequired(IppAttributeNames.JobPriorityDefault) ? null : options.JobPriority,
                JobPrioritySupported = !IsRequired(IppAttributeNames.JobPrioritySupported) ? null : options.JobPriority,
                CopiesDefault = !IsRequired(IppAttributeNames.CopiesDefault) ? null : options.Copies,
                CopiesSupported = !IsRequired(IppAttributeNames.CopiesSupported) ? null : new SharpIpp.Protocol.Models.Range(options.Copies, options.Copies),
                OrientationRequestedDefault = !IsRequired(IppAttributeNames.OrientationRequestedDefault) ? null : options.Orientation,
                OrientationRequestedSupported = !IsRequired(IppAttributeNames.OrientationRequestedSupported) ? null : Enum.GetValues(typeof(Orientation)).Cast<Orientation>().ToArray(),
                PageRangesSupported = !IsRequired(IppAttributeNames.PageRangesSupported) ? null : options.PageRangesSupported,
                PagesPerMinute = !IsRequired(IppAttributeNames.PagesPerMinute) ? null : options.PagesPerMinute,
                PagesPerMinuteColor = !IsRequired(IppAttributeNames.PagesPerMinuteColor) ? null : options.PagesPerMinuteColor,
                PrinterMoreInfo = !IsRequired(IppAttributeNames.PrinterMoreInfo) ? null : GetPrinterMoreInfo(),
                JobHoldUntilSupported = !IsRequired(IppAttributeNames.JobHoldUntilSupported) ? null : options.JobHoldUntilSupported,
                JobHoldUntilDefault = !IsRequired(IppAttributeNames.JobHoldUntilDefault) ? null : options.JobHoldUntil,
                ReferenceUriSchemesSupported = !IsRequired(IppAttributeNames.ReferenceUriSchemesSupported) ? null : options.ReferenceUriSchemesSupported,
                OutputBinDefault = !IsRequired(IppAttributeNames.OutputBinDefault) ? null : options.OutputBin.FirstOrDefault(),
                OutputBinSupported = !IsRequired(IppAttributeNames.OutputBinSupported) ? null : options.OutputBin,
                MediaColDefault = !IsRequired(IppAttributeNames.MediaColDefault) ? null : new MediaCol
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
                PrintColorModeDefault = !IsRequired(IppAttributeNames.PrintColorModeDefault) ? null : options.PrintColorModes.FirstOrDefault(),
                PrintColorModeSupported = !IsRequired(IppAttributeNames.PrintColorModeSupported) ? null : options.PrintColorModes,
                PrinterDeviceId = !IsRequired(IppAttributeNames.PrinterDeviceId) ? null : GetPrinterDeviceId(options),
                PrinterUUID = !IsRequired(IppAttributeNames.PrinterUUID) ? null : $"urn:uuid:{options.UUID}"
            }
        };
    }

    private UriSecurity GetUriSecuritySupported()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return request.IsHttps ? UriSecurity.Tls : UriSecurity.None;
    }

    private IEnumerable<PrinterJob> GetPrinterJobs(WhichJobs? whichJobs)
    {
        if (whichJobs == WhichJobs.Completed)
            return _jobs.Values.Where(x => x.State == JobState.Completed || x.State == JobState.Aborted || x.State == JobState.Canceled);
        if (whichJobs == WhichJobs.NotCompleted)
            return _jobs.Values.Where(x => x.State == JobState.Processing || x.State == JobState.Pending);
        return _jobs.Values.Where(x => x.State.HasValue);
    }

    private GetJobsResponse GetGetJobsResponse(GetJobsRequest request)
    {
        IEnumerable<PrinterJob> jobs = GetPrinterJobs(request.OperationAttributes?.WhichJobs);
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
            JobsAttributes = jobs.Select(x => GetJobDescriptionAttributes(x, request.OperationAttributes?.RequestedAttributes, true)).ToArray()
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
            _ => null,
        }).FirstOrDefault(x => x != null);
        var jobName = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.OperationAttributes?.JobName,
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.JobName,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var ippAttributeFidelity = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.OperationAttributes?.IppAttributeFidelity,
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.IppAttributeFidelity,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var compression = job.Requests.Select(x => x switch
        {
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.Compression,
            SendDocumentRequest sendDocumentRequest => sendDocumentRequest.OperationAttributes?.Compression,
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
            JobName = !IsRequired(IppAttributeNames.JobName) ? null : jobName,
            JobPrinterUri = !IsRequired(IppAttributeNames.JobPrinterUri) ? null : GetPrinterUrl(),
            JobState = !IsRequired(IppAttributeNames.JobState) ? null : job.State,
            JobStateReasons = !IsRequired(IppAttributeNames.JobState) ? null : [JobStateReason.None],
            DateTimeAtCreation = !IsRequired(IppAttributeNames.DateTimeAtCreation) ? null : job.CreatedDateTime,
            TimeAtCreation = !IsRequired(IppAttributeNames.TimeAtCreation) ? null : (int)(job.CreatedDateTime - _startTime).TotalSeconds,
            DateTimeAtProcessing = !IsRequired(IppAttributeNames.DateTimeAtProcessing) ? null : job.ProcessingDateTime ?? DateTimeOffset.MinValue,
            TimeAtProcessing = !IsRequired(IppAttributeNames.TimeAtProcessing) ? null : job.ProcessingDateTime.HasValue ? (int)(job.ProcessingDateTime.Value - _startTime).TotalSeconds : -1,
            DateTimeAtCompleted = !IsRequired(IppAttributeNames.DateTimeAtCompleted) ? null : job.CompletedDateTime ?? DateTimeOffset.MinValue,
            TimeAtCompleted = !IsRequired(IppAttributeNames.TimeAtCompleted) ? null : job.CompletedDateTime.HasValue ? (int)(job.CompletedDateTime.Value - _startTime).TotalSeconds : -1,
            JobOriginatingUserName = !IsRequired(IppAttributeNames.JobOriginatingUserName) ? null : job.UserName,
            JobPrinterUpTime = !IsRequired(IppAttributeNames.JobPrinterUpTime) ? null : (int)(dateTimeOffsetProvider.UtcNow - _startTime).TotalSeconds
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
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
            {
                JobState = JobState.Pending,
                JobStateReasons = [JobStateReason.None]
            }
        };
        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow);
        response.JobAttributes.JobId = job.Id;
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
        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow);
        var response = new PrintJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
            {
                JobId = job.Id,
                JobState = JobState.Pending,
                JobStateReasons = [JobStateReason.None]
            }
        };
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

    private Uri GetPrinterUrl(string? path = null, string? appendPath = null)
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        path ??= request.Path;
        if(!string.IsNullOrEmpty(appendPath))
        {
            if (!path.EndsWith('/'))
                path += '/';
            path += appendPath;
        }
        return new Uri($"ipp://{request.Host}{request.PathBase}{(path is null ? request.Path : path)}");
    }

    private Uri GetPrinterMoreInfo()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return new Uri($"{request.Scheme}://{request.Host}{request.PathBase}");
    }

    private static int? GetJobId(IIppJobRequest request)
    {
        if(request.OperationAttributes is not JobOperationAttributes jobOperationAttributes)
            return null;
        if (jobOperationAttributes.JobUri != null && int.TryParse(jobOperationAttributes.JobUri.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return jobOperationAttributes.JobId;
    }

    private static int? GetJobId(CloseJobRequest request)
    {
        if (request.OperationAttributes is not CloseJobOperationAttributes attr)
            return null;
        if (attr.JobUri != null && int.TryParse(attr.JobUri.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return attr.JobId;
    }

    private static int? GetJobId(ResubmitJobRequest request)
    {
        if (request.OperationAttributes is not ResubmitJobOperationAttributes attr)
            return null;
        if (attr.JobUri != null && int.TryParse(attr.JobUri.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return attr.JobId;
    }

    private void ImproveCloseJobRawResponse(CloseJobServerResponse response, IIppResponseMessage rawResponse)
    {
        if (response.StatusCode != IppStatusCode.SuccessfulOk)
            return;
        var list = rawResponse.JobAttributes.FirstOrDefault();
        if (list is null)
        {
            list = [];
            rawResponse.JobAttributes.Add(list);
        }
        list.Add(new IppAttribute(Tag.Integer, "job-id", response.JobId));
        list.Add(new IppAttribute(Tag.Enum, "job-state", (int)response.JobState));
        list.Add(new IppAttribute(Tag.Keyword, "job-state-reasons", "none"));
    }

    private void ImproveResubmitJobRawResponse(ResubmitJobServerResponse response, IIppResponseMessage rawResponse)
    {
        if (response.StatusCode != IppStatusCode.SuccessfulOk)
            return;
        var list = rawResponse.JobAttributes.FirstOrDefault();
        if (list is null)
        {
            list = [];
            rawResponse.JobAttributes.Add(list);
        }
        list.Add(new IppAttribute(Tag.Integer, "job-id", response.NewJobId));
        list.Add(new IppAttribute(Tag.Enum, "job-state", (int)response.JobState));
        list.Add(new IppAttribute(Tag.Keyword, "job-state-reasons", "none"));
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
        attributes.Finishings ??= options.Finishings;
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

public class CloseJobServerResponse : CloseJobResponse
{
    public int JobId { get; set; }
    public JobState JobState { get; set; }
}

public class ResubmitJobServerResponse : ResubmitJobResponse
{
    public int NewJobId { get; set; }
    public JobState JobState { get; set; }
}
