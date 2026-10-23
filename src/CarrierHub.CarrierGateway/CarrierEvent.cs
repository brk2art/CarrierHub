namespace CarrierHub.CarrierGateway;

// The one status set the rest of CarrierHub understands. The names must match Tracking's, because they're sent as strings.
public enum ShipmentStatus { PickedUp, InTransit, OutForDelivery, DeliveryFailed, Delivered, Returned }

// A carrier event after translation: our status, UTC time. Nothing carrier-specific is left.
public sealed record CarrierEvent(string CarrierEventId, ShipmentStatus Status, DateTimeOffset OccurredAt,
    string? Location, string? Description);

// What a carrier answer turned into, plus anything we couldn't translate (reported, not thrown).
public sealed record ParseResult(IReadOnlyList<CarrierEvent> Events, IReadOnlyList<string> Unknown);

public interface ICarrierAdapter
{
    // The key the Gateway and Tracking use for this carrier, e.g. "fastship".
    string Carrier { get; }

    Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string trackingNumber, CancellationToken ct);
}