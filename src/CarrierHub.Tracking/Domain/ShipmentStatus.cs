namespace CarrierHub.Tracking.Domain;

// The order of the values is the order of progress.
// It breaks ties between events that happened at the same time.
public enum ShipmentStatus
{
    Created = 0,
    PickedUp = 1,
    InTransit = 2,
    OutForDelivery = 3,
    DeliveryFailed = 4,
    Delivered = 5,
    Returned = 6,
}

public static class ShipmentStatusExtensions
{
    // Final statuses can't be left again.
    public static bool IsFinal(this ShipmentStatus status) =>
        status is ShipmentStatus.Delivered or ShipmentStatus.Returned;
}