using Microsoft.Extensions.Logging;
using SharpIpp.Models;
using SharpIpp.Models.Requests;
using SharpIpp.Models.Responses;
using SharpIpp.Protocol.Models;
using System.Collections.Concurrent;

namespace IppPrinter.Services;

public class SubscriptionsService(ILogger<SubscriptionsService> logger)
{
    private class PrinterSubscription
    {
        public int Id { get; set; }
        public string[] Events { get; set; } = [];
    }

    private readonly ConcurrentDictionary<int, PrinterSubscription> _subscriptions = new();
    private int _newSubscriptionIndex = 1;

    public CreatePrinterSubscriptionsResponse GetCreatePrinterSubscriptionsResponse(CreatePrinterSubscriptionsRequest request)
    {
        var subId = Interlocked.Increment(ref _newSubscriptionIndex);
        var sub = new PrinterSubscription
        {
            Id = subId,
            Events = [ "printer-state-changed" ]
        };
        _subscriptions[subId] = sub;
        logger.LogInformation("Registered printer subscription {id}", subId);

        return new CreatePrinterSubscriptionsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            SubscriptionsAttributes =
            [
                new SubscriptionDescriptionAttributes
                {
                    NotifySubscriptionId = sub.Id,
                    NotifyPullMethod = NotifyPullMethod.IppGet,
                    NotifyEvents = sub.Events.Select(e => (NotifyEvent)e).ToArray()
                }
            ]
        };
    }

    public CancelSubscriptionResponse GetCancelSubscriptionResponse(CancelSubscriptionRequest request)
    {
        var response = new CancelSubscriptionResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };

        var subId = request.OperationAttributes?.NotifySubscriptionId;
        if (subId.HasValue)
        {
            if (_subscriptions.TryRemove(subId.Value, out _))
            {
                logger.LogInformation("Subscription {id} has been cancelled", subId.Value);
            }
            else
            {
                response.StatusCode = IppStatusCode.ClientErrorNotPossible;
                logger.LogWarning("CancelSubscription failed: subscription {id} not found", subId.Value);
            }
        }
        else
        {
            response.StatusCode = IppStatusCode.ClientErrorBadRequest;
            logger.LogWarning("CancelSubscription failed: missing subscription id");
        }

        return response;
    }

    public GetSubscriptionAttributesResponse GetGetSubscriptionAttributesResponse(GetSubscriptionAttributesRequest request)
    {
        var subId = request.OperationAttributes?.NotifySubscriptionId;
        if (subId.HasValue)
        {
            if (_subscriptions.TryGetValue(subId.Value, out var sub))
            {
                logger.LogInformation("Retrieved subscription attributes for subscription {id}", subId.Value);
                return new GetSubscriptionAttributesResponse
                {
                    RequestId = request.RequestId,
                    Version = request.Version,
                    StatusCode = IppStatusCode.SuccessfulOk,
                    SubscriptionAttributes = new SubscriptionDescriptionAttributes
                    {
                        NotifySubscriptionId = sub.Id,
                        NotifyPullMethod = NotifyPullMethod.IppGet,
                        NotifyEvents = sub.Events.Select(e => (NotifyEvent)e).ToArray()
                    }
                };
            }
            else
            {
                logger.LogWarning("GetSubscriptionAttributes failed: subscription {id} not found", subId.Value);
                return new GetSubscriptionAttributesResponse
                {
                    RequestId = request.RequestId,
                    Version = request.Version,
                    StatusCode = IppStatusCode.ClientErrorNotFound
                };
            }
        }

        logger.LogWarning("GetSubscriptionAttributes failed: missing subscription id");
        return new GetSubscriptionAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorBadRequest
        };
    }
}
