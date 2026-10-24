namespace CarrierHub.Tracking.Domain;

// Something that happened to a shipment that others may need to know about.
public interface IDomainEvent;

public sealed record ShipmentCreated(Guid ShipmentId, string MerchantId, string MerchantReference, string Carrier,
    string TrackingNumber, DateTimeOffset PromisedBy, DateTimeOffset CreatedAt) : IDomainEvent;

// Raised only when the status really changes: not for duplicates, not for late events that change nothing.
public sealed record ShipmentStatusChanged(Guid ShipmentId, string MerchantId, string MerchantReference, string Carrier,
    string TrackingNumber, ShipmentStatus PreviousStatus, ShipmentStatus Status, DateTimeOffset ChangedAt) : IDomainEvent;