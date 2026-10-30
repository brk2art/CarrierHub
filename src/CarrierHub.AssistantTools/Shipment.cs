namespace CarrierHub.AssistantTools;

// The Public API's v1 shipment, as this service reads it. Our own copy: the assistant is a client of the
// public contract, like any merchant, and never sees Tracking's internal fields.
public sealed record TimelineEntry(string Status, DateTimeOffset OccurredAt, string? Location, string? Description);

public sealed record Shipment(Guid Id, string MerchantReference, string Carrier, string TrackingNumber, string Status,
    DateTimeOffset PromisedBy, DateTimeOffset? DeliveredAt, bool IsLate, IReadOnlyList<TimelineEntry> Timeline);