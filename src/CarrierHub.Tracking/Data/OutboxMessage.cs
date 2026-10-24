namespace CarrierHub.Tracking.Data;

// A message waiting to be published, saved in the same transaction as the change it announces.
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Type { get; init; }        // "ShipmentCreated", "ShipmentStatusChanged"
    public required string Payload { get; init; }     // the message as JSON
    public required string SessionId { get; init; }   // the shipment: its messages stay in order for subscribers
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PublishedAt { get; set; }  // null until the bus has it
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    // W3C trace context of the change that wrote this row. The publisher runs later on a timer;
    // with this it continues the same trace instead of starting a new one.
    public string? TraceParent { get; init; }
}