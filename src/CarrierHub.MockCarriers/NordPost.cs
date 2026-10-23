using System.Globalization;
using System.Xml.Linq;

namespace CarrierHub.MockCarriers;

// NordPost: an older XML API. Date and time are separate fields in Danish local time with no offset,
// so the receiver must know the carrier's time zone, and when the clocks go back one local hour happens twice.
public static class NordPost
{
    public const string Name = "nordpost";

    public static void MapNordPost(this WebApplication app) =>
        app.MapGet("/nordpost/api/track",
            async (string barcode, CarrierState state, HttpContext http, CancellationToken ct) =>
            {
                if (await FaultInjection.BeforeAnswerAsync(Name, state, http, ct) is { } failure)
                    return failure;

                var events = state.HappenedSoFar(Name, barcode).Select(e =>
                {
                    var local = TimeZoneInfo.ConvertTime(e.At, Copenhagen.Zone);
                    return new XElement("Event",
                        new XElement("EventId", $"{barcode}:{e.Step.Index}"),
                        new XElement("Type", Type(e.Step.Step)),
                        new XElement("Date", local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XElement("Time", local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement("Place", e.Step.Location));
                });

                var xml = new XDocument(new XElement("TrackingResponse",
                    new XElement("Item", new XAttribute("barcode", barcode),
                        FaultInjection.Mangle(events, state.FaultsFor(Name)))));

                return Results.Content(xml.ToString(), "application/xml");
            });

    private static string Type(Step step) => step switch
    {
        Step.PickedUp => "ACCEPTED",
        Step.InTransit => "IN_TRANSIT",
        Step.OutForDelivery => "OUT_FOR_DELIVERY",
        Step.DeliveryFailed => "NOT_DELIVERED",
        Step.Delivered => "DELIVERED",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };
}