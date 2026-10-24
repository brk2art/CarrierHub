using System.Text.Json;

namespace CarrierHub.CarrierGateway;

public sealed record ActiveShipment(string Carrier, string TrackingNumber);

// Asks Tracking which shipments are still active. Events no longer go this way: they go on the bus.
public sealed class TrackingClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ActiveShipment>> GetActiveShipmentsAsync(string carrier, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ActiveShipment>>(
            $"/carriers/{Uri.EscapeDataString(carrier)}/active-shipments", Json, ct) ?? [];
}