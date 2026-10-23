using System.Text.Json;

namespace CarrierHub.CarrierGateway.Adapters;

// FastShip: JSON, short codes, local times with an offset.
public sealed class FastShipAdapter(HttpClient http, ILogger<FastShipAdapter> logger) : ICarrierAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Carrier => "fastship";

    public async Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string trackingNumber, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"/fastship/v2/shipments/{Uri.EscapeDataString(trackingNumber)}/events", ct);
        var result = Parse(json);

        if (result.Unknown.Count > 0)
            logger.LogWarning("FastShip sent codes we don't know for {TrackingNumber}: {Codes}",
                trackingNumber, string.Join(", ", result.Unknown));

        return result.Events;
    }

    // Pure translation, no network: what the contract tests check.
    public static ParseResult Parse(string json)
    {
        var response = JsonSerializer.Deserialize<Response>(json, Json)
            ?? throw new FormatException("FastShip returned an empty response.");

        var events = new List<CarrierEvent>();
        var unknown = new List<string>();

        foreach (var e in response.Events)
        {
            if (Map(e.Code) is { } status)
                events.Add(new CarrierEvent(e.Id, status, e.Timestamp.ToUniversalTime(), e.Location, null));
            else
                unknown.Add(e.Code);
        }

        return new ParseResult(events, unknown);
    }

    private static ShipmentStatus? Map(string code) => code switch
    {
        "PU" => ShipmentStatus.PickedUp,
        "IT" => ShipmentStatus.InTransit,
        "OD" => ShipmentStatus.OutForDelivery,
        "NA" => ShipmentStatus.DeliveryFailed,
        "DL" => ShipmentStatus.Delivered,
        _ => null,
    };

    private sealed record Event(string Id, string Code, DateTimeOffset Timestamp, string? Location);
    private sealed record Response(string TrackingNumber, List<Event> Events);
}