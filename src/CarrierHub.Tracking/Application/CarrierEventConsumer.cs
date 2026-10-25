using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using CarrierHub.Tracking.Api;
using CarrierHub.Tracking.Data;

namespace CarrierHub.Tracking.Application;

// Reads carrier events from Service Bus. One session per shipment: a shipment's events are handled one at a time,
// different shipments in parallel.
public sealed class CarrierEventConsumer(ServiceBusClient client, IServiceScopeFactory scopes, TimeProvider clock,
    IConfiguration configuration, ILogger<CarrierEventConsumer> logger) : BackgroundService
{
    public const string QueueName = "carrier-events";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private ServiceBusSessionProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = client.CreateSessionProcessor(QueueName, new ServiceBusSessionProcessorOptions
        {
            // Many shipments, each with a message or two waiting: many slots and a short idle, so a slot doesn't sit
            // waiting on a shipment with nothing more to say. 8 slots and 2 s fell behind at 5 shipments/s (ADR 12).
            MaxConcurrentSessions = configuration.GetValue("ServiceBus:MaxConcurrentSessions", 32),
            MaxConcurrentCallsPerSession = 1,       // one event at a time per shipment
            AutoCompleteMessages = false,           // we decide: complete, or dead-letter with a reason
            SessionIdleTimeout = configuration.GetValue("ServiceBus:SessionIdleTimeout", TimeSpan.FromMilliseconds(200)),
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
        CarrierEventRequest? request;
        try
        {
            request = args.Message.Body.ToObjectFromJson<CarrierEventRequest>(Json);
        }
        catch (JsonException ex)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", ex.Message, args.CancellationToken);
            return;
        }

        if (request is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidMessage", "Empty message body.", args.CancellationToken);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackingDbContext>();
        var result = await CarrierEvents.ApplyAsync(db, request, clock.GetUtcNow(), args.CancellationToken);

        switch (result.Status)
        {
            case ApplyStatus.Invalid:
                await args.DeadLetterMessageAsync(args.Message, "InvalidEvent", result.Error, args.CancellationToken);
                break;

            case ApplyStatus.UnknownShipment:
                // Parked, not dropped: it can be replayed once the shipment exists.
                await args.DeadLetterMessageAsync(args.Message, "UnknownShipment", result.Error, args.CancellationToken);
                break;

            default:
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                if (result.Outcome!.StatusChanged)
                    logger.LogInformation("{Carrier} {TrackingNumber} is now {Status}",
                        request.Carrier, request.TrackingNumber, result.Outcome.After);
                break;
        }

        // An exception thrown here (say, the database is down) abandons the message: the bus delivers it again,
        // and after MaxDeliveryCount attempts moves it to the dead-letter queue.
    }
}