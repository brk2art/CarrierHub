using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarrierHub.CarrierGateway;

public sealed record ActiveShipment(string Carrier, string TrackingNumber);

public sealed record ForwardResult(string Result, bool StatusChanged);

// Talks to Tracking over HTTP until Phase 3 puts the bus in between.
public sealed class TrackingClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<ActiveShipment>> GetActiveShipmentsAsync(string carrier, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ActiveShipment>>(
            $"/carriers/{Uri.EscapeDataString(carrier)}/active-shipments", Json, ct) ?? [];

    public async Task<ForwardResult> SendEventAsync(ActiveShipment shipment, CarrierEvent e, CancellationToken ct)
    {
        // The carrier name goes back exactly as Tracking stored it, so the shipment is found.
        using var response = await http.PostAsJsonAsync("/carrier-events", new
        {
            shipment.Carrier,
            shipment.TrackingNumber,
            e.CarrierEventId,
            e.Status,
            e.OccurredAt,
            e.Location,
            e.Description,
        }, Json, ct);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ForwardResult>(Json, ct))!;
    }
}