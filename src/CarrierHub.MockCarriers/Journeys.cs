namespace CarrierHub.MockCarriers;

// A carrier-neutral step in a parcel's journey. Each carrier turns it into its own format.
public enum Step { PickedUp, InTransit, OutForDelivery, DeliveryFailed, Delivered }

public sealed record JourneyStep(int Index, Step Step, TimeSpan After, string Location);

public static class Journeys
{
    // Seconds, not days, so a whole journey plays out in a few minutes while you watch.
    private static readonly JourneyStep[] Normal =
    [
        new(0, Step.PickedUp, TimeSpan.FromSeconds(20), "Aarhus terminal"),
        new(1, Step.InTransit, TimeSpan.FromSeconds(45), "Copenhagen hub"),
        new(2, Step.OutForDelivery, TimeSpan.FromSeconds(90), "Copenhagen depot"),
        new(3, Step.Delivered, TimeSpan.FromSeconds(120), "Customer address"),
    ];

    private static readonly JourneyStep[] FailedAttemptFirst =
    [
        new(0, Step.PickedUp, TimeSpan.FromSeconds(20), "Aarhus terminal"),
        new(1, Step.InTransit, TimeSpan.FromSeconds(45), "Copenhagen hub"),
        new(2, Step.OutForDelivery, TimeSpan.FromSeconds(90), "Copenhagen depot"),
        new(3, Step.DeliveryFailed, TimeSpan.FromSeconds(120), "Customer address"),
        new(4, Step.OutForDelivery, TimeSpan.FromSeconds(180), "Copenhagen depot"),
        new(5, Step.Delivered, TimeSpan.FromSeconds(210), "Customer address"),
    ];

    // The same tracking number always gets the same journey; about one in four has a failed attempt first.
    public static IReadOnlyList<JourneyStep> For(string trackingNumber) =>
        StableHash(trackingNumber) % 4 == 0 ? FailedAttemptFirst : Normal;

    // string.GetHashCode() changes every time the process starts, so it can't be used here.
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
                hash = hash * 31 + c;
            return hash & 0x7fffffff;
        }
    }
}

public static class Copenhagen
{
    // The carriers' local time zone, with its summer/winter clock changes.
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
}