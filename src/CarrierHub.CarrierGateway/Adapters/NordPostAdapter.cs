using System.Globalization;
using System.Xml.Linq;

namespace CarrierHub.CarrierGateway.Adapters;

// NordPost: XML, separate date and time fields, Danish local time with no offset.
public sealed class NordPostAdapter(HttpClient http, ILogger<NordPostAdapter> logger) : ICarrierAdapter
{
    // From NordPost's API documentation: all times are Danish local time.
    private static readonly TimeZoneInfo CarrierTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    public string Carrier => "nordpost";

    public async Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string trackingNumber, CancellationToken ct)
    {
        var xml = await http.GetStringAsync($"/nordpost/api/track?barcode={Uri.EscapeDataString(trackingNumber)}", ct);
        var result = Parse(xml, CarrierTimeZone);

        if (result.Unknown.Count > 0)
            logger.LogWarning("NordPost sent types we don't know for {TrackingNumber}: {Types}",
                trackingNumber, string.Join(", ", result.Unknown));

        return result.Events;
    }

    public static ParseResult Parse(string xml, TimeZoneInfo zone)
    {
        var events = new List<CarrierEvent>();
        var unknown = new List<string>();

        foreach (var e in XDocument.Parse(xml).Descendants("Event"))
        {
            var id = (string?)e.Element("EventId");
            var type = (string?)e.Element("Type");
            var date = (string?)e.Element("Date");
            var time = (string?)e.Element("Time");

            if (id is null || type is null || date is null || time is null)
            {
                unknown.Add("an event with missing fields");
                continue;
            }

            if (Map(type) is not { } status)
            {
                unknown.Add(type);
                continue;
            }

            var occurredAt = ToUtc(
                DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeOnly.ParseExact(time, "HH:mm:ss", CultureInfo.InvariantCulture),
                zone);

            events.Add(new CarrierEvent(id, status, occurredAt, (string?)e.Element("Place"), null));
        }

        return new ParseResult(events, unknown);
    }

    // A local time with no offset only means something together with the time zone, and twice a year it's tricky.
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        // Spring: the clocks jump from 02:00 to 03:00, so 02:30 never happens. Treat it as 03:30.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);

        // Autumn: 02:00 to 03:00 happens twice. We can't tell which one the carrier meant,
        // so we take the first (summer time, the larger offset). See the ADR.
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static ShipmentStatus? Map(string type) => type switch
    {
        "ACCEPTED" => ShipmentStatus.PickedUp,
        "IN_TRANSIT" => ShipmentStatus.InTransit,
        "OUT_FOR_DELIVERY" => ShipmentStatus.OutForDelivery,
        "NOT_DELIVERED" => ShipmentStatus.DeliveryFailed,
        "DELIVERED" => ShipmentStatus.Delivered,
        _ => null,
    };
}