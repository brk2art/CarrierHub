using System.Net.Http.Json;
using CarrierHub.PublicApi.V1;

namespace CarrierHub.PublicApi;

// Calls the Tracking service. The base address "https+http://tracking" is resolved by Aspire's
// service discovery, and ServiceDefaults adds retries, timeouts and a circuit breaker to this client.
public sealed class TrackingClient(HttpClient http)
{
    public Task<HttpResponseMessage> CreateAsync(string merchantId, CreateShipmentRequest request, CancellationToken ct) =>
        http.PostAsJsonAsync(ShipmentsPath(merchantId), request, ct);

    public Task<HttpResponseMessage> GetAsync(string merchantId, Guid id, CancellationToken ct) =>
        http.GetAsync($"{ShipmentsPath(merchantId)}/{id}", ct);

    public Task<HttpResponseMessage> SearchAsync(string merchantId, string? status, bool? late, string? reference,
        int? limit, CancellationToken ct) =>
        http.GetAsync(ShipmentsPath(merchantId) + Query(status, late, reference, limit), ct);

    // The same filters as the search, counted by Tracking without loading any shipment.
    public Task<HttpResponseMessage> CountAsync(string merchantId, string? status, bool? late, string? reference,
        CancellationToken ct) =>
        http.GetAsync($"{ShipmentsPath(merchantId)}/count" + Query(status, late, reference, limit: null), ct);

    private static string Query(string? status, bool? late, string? reference, int? limit)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (late is { } isLate) query.Add($"late={(isLate ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(reference)) query.Add($"reference={Uri.EscapeDataString(reference)}");
        if (limit is { } take) query.Add($"limit={take}");

        return query.Count > 0 ? "?" + string.Join("&", query) : "";
    }

    private static string ShipmentsPath(string merchantId) => $"/merchants/{Uri.EscapeDataString(merchantId)}/shipments";
}