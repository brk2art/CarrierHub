using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace CarrierHub.Notification.Webhooks;

// Reads the "notifications" subscription of the shipment-events topic. One session per shipment:
// a shipment's messages are delivered in order, and a failing one holds up only its own shipment.
public sealed class NotificationConsumer(ServiceBusClient client, IServiceScopeFactory scopes,
    ILogger<NotificationConsumer> logger) : BackgroundService
{
    public const string TopicName = "shipment-events";
    public const string SubscriptionName = "notifications";

    private ServiceBusSessionProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = client.CreateSessionProcessor(TopicName, SubscriptionName, new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = 8,
            MaxConcurrentCallsPerSession = 1,
            AutoCompleteMessages = false,
            SessionIdleTimeout = TimeSpan.FromSeconds(2),
        });

        _processor.ProcessMessageAsync += HandleAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogWarning(args.Exception, "Service Bus error ({Source})", args.ErrorSource);
            return Task.CompletedTask;
        };

        await _processor.StartProcessingAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(ProcessSessionMessageEventArgs args)
    {
        using var scope = scopes.CreateScope();
        var result = await HandleOneAsync(args.Message, scope.ServiceProvider, args.CancellationToken);

        switch (result)
        {
            case DeliveryResult.Invalid:
                await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", "No merchantId in the message.", args.CancellationToken);
                break;

            case DeliveryResult.Failed:
                // The bus delivers it again; after MaxDeliveryCount it goes to the dead-letter queue.
                // The shipment's later messages wait behind it, so the merchant never gets them out of order.
                logger.LogWarning("Delivery of {MessageId} failed (delivery {Count}); the bus will retry",
                    args.Message.MessageId, args.Message.DeliveryCount);
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                break;

            default:
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                break;
        }
    }

    // Shared with the dead-letter replay, so both deliver the same way.
    public static async Task<DeliveryResult> HandleOneAsync(ServiceBusReceivedMessage message, IServiceProvider services, CancellationToken ct)
    {
        // Merchants hear about status changes. Other messages on the topic aren't for them.
        if (message.Subject != "ShipmentStatusChanged")
            return DeliveryResult.Skipped;

        string? merchantId;
        try
        {
            using var document = JsonDocument.Parse(message.Body.ToMemory());
            merchantId = document.RootElement.GetProperty("merchantId").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return DeliveryResult.Invalid;
        }

        if (string.IsNullOrEmpty(merchantId))
            return DeliveryResult.Invalid;

        var deliverer = services.GetRequiredService<WebhookDeliverer>();
        return await deliverer.DeliverAsync(message.MessageId, message.Subject, merchantId, message.Body.ToString(), ct);
    }
}