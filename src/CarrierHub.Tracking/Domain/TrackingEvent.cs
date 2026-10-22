namespace CarrierHub.Tracking.Domain;

// One event reported by a carrier, such as "picked up" or "out for delivery".
public sealed class TrackingEvent
{
    public TrackingEvent(string carrierEventId, ShipmentStatus status,
        DateTimeOffset occurredAt, DateTimeOffset receivedAt,
        string? location = null, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(carrierEventId);
        if (status == ShipmentStatus.Created)
            throw new ArgumentException("A carrier event cannot set the status to Created.", nameof(status));

        CarrierEventId = carrierEventId;
        Status = status;
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
        Location = location;
        Description = description;
    }

    private TrackingEvent() { }   // for EF Core in step 3

    // The carrier's own ID for this event. The same ID twice is the same event.
    public string CarrierEventId { get; private set; } = "";
    public ShipmentStatus Status { get; private set; }

    // When it happened, by the carrier's clock. The timeline is ordered by this.
    public DateTimeOffset OccurredAt { get; private set; }

    // When it reached us, by our clock. The gap to OccurredAt shows how late a carrier reports.
    public DateTimeOffset ReceivedAt { get; private set; }

    public string? Location { get; private set; }
    public string? Description { get; private set; }
}