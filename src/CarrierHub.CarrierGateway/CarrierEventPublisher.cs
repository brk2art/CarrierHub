using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;

namespace CarrierHub.CarrierGateway;

// Puts carrier events on the "carrier-events" queue. The sender is long-lived and thread-safe: one per process.
public sealed class CarrierEventPublisher(ServiceBusClient client) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ServiceBusSender _sender = client.CreateSender("carrier-events");

    public Task PublishAsync(ActiveShipment shipment, CarrierEvent e, CancellationToken ct)
    {
        var carrier = shipment.Carrier.ToLowerInvariant();

        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(new
        {
            shipment.Carrier,
            shipment.TrackingNumber,
            e.CarrierEventId,
            e.Status,
            e.OccurredAt,
            e.Location,
            e.Description,
        }, Json))
        {
            // One session per shipment: Tracking handles its events one at a time.
            SessionId = $"{carrier}:{shipment.TrackingNumber}",
            // The bus drops the same MessageId within its duplicate-detection window.
            MessageId = $"{carrier}:{shipment.TrackingNumber}:{e.CarrierEventId}",
            ContentType = "application/json",
            Subject = "CarrierEvent",
        };

        return _sender.SendMessageAsync(message, ct);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}