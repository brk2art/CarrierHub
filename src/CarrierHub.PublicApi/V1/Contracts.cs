namespace CarrierHub.PublicApi.V1;

// The public v1 contract. Tracking's responses are read straight into these types,
// so any field Tracking has that isn't listed here (like receivedAt) never reaches a merchant.

public sealed record CreateShipmentRequest(string MerchantReference, string Carrier, string TrackingNumber, DateTimeOffset PromisedBy);

public sealed record TimelineEntry(string Status, DateTimeOffset OccurredAt, string? Location, string? Description);

public sealed record Shipment(Guid Id, string MerchantReference, string Carrier, string TrackingNumber, string Status,
    DateTimeOffset PromisedBy, DateTimeOffset? DeliveredAt, bool IsLate, IReadOnlyList<TimelineEntry> Timeline);