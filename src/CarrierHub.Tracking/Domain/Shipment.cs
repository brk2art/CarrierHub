namespace CarrierHub.Tracking.Domain;

public enum EventResult { Added, Duplicate }

// What adding an event did. StatusChanged decides whether others are told.
public sealed record EventOutcome(EventResult Result, ShipmentStatus Before, ShipmentStatus After)
{
    public bool StatusChanged => Before != After;
}

public sealed class Shipment
{
    private readonly List<TrackingEvent> _events = [];
    private readonly List<IDomainEvent> _domainEvents = [];

    public Shipment(string merchantId, string merchantReference, string carrier, string trackingNumber,
        DateTimeOffset promisedBy, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(merchantReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(carrier);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingNumber);
        if (promisedBy < createdAt)
            throw new ArgumentException("The promised time cannot be before the shipment was created.", nameof(promisedBy));

        Id = Guid.CreateVersion7(createdAt);   // time-ordered, so database inserts stay cheap
        MerchantId = merchantId;
        MerchantReference = merchantReference;
        Carrier = carrier;
        TrackingNumber = trackingNumber;
        PromisedBy = promisedBy.ToUniversalTime();
        CreatedAt = createdAt.ToUniversalTime();
        Status = ShipmentStatus.Created;

        _domainEvents.Add(new ShipmentCreated(Id, MerchantId, MerchantReference, Carrier, TrackingNumber, PromisedBy, CreatedAt));
    }

    private Shipment() { }   // for EF Core

    public Guid Id { get; private set; }

    // Every query filters on this. The Public API takes it from the caller's token, never from the request.
    public string MerchantId { get; private set; } = "";

    // The merchant's own order number: what a person would search by.
    public string MerchantReference { get; private set; } = "";

    public string Carrier { get; private set; } = "";
    public string TrackingNumber { get; private set; } = "";
    public DateTimeOffset PromisedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public ShipmentStatus Status { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }

    // Changes with every new event, so saving an event always updates the shipment row
    // and the database's concurrency check always runs.
    public int EventCount { get; private set; }

    // The stored events, in arrival order. EF Core maps this; read Timeline instead.
    public IReadOnlyCollection<TrackingEvent> Events => _events;

    // The events in the order they happened, whatever order they arrived in.
    public IReadOnlyList<TrackingEvent> Timeline =>
        _events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Status).ToList();

    // What happened since the last save. The DbContext turns these into outbox messages, in the same transaction.
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    public EventOutcome AddEvent(TrackingEvent trackingEvent)
    {
        ArgumentNullException.ThrowIfNull(trackingEvent);
        var before = Status;

        if (_events.Any(e => e.CarrierEventId == trackingEvent.CarrierEventId))
            return new EventOutcome(EventResult.Duplicate, before, before);

        _events.Add(trackingEvent);
        Recalculate();

        var outcome = new EventOutcome(EventResult.Added, before, Status);
        if (outcome.StatusChanged)
            _domainEvents.Add(new ShipmentStatusChanged(Id, MerchantId, MerchantReference, Carrier, TrackingNumber,
                before, Status, trackingEvent.ReceivedAt));

        return outcome;
    }

    // Late: delivered after the promise, or not delivered yet and the promise has passed.
    // A returned shipment is not counted as late; it has its own status.
    public bool IsLate(DateTimeOffset now) =>
        DeliveredAt is { } delivered
            ? delivered > PromisedBy
            : !Status.IsFinal() && now > PromisedBy;

    // Replays the whole timeline from the start, so the arrival order cannot change the result.
    private void Recalculate()
    {
        var status = ShipmentStatus.Created;
        DateTimeOffset? deliveredAt = null;

        foreach (var e in Timeline)
        {
            if (status.IsFinal())
                break;   // events after a final status stay in the timeline but don't change it

            status = e.Status;
            if (status == ShipmentStatus.Delivered)
                deliveredAt = e.OccurredAt;
        }

        Status = status;
        DeliveredAt = deliveredAt;
        EventCount = _events.Count;
    }
}