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

    // The key goes in the body to Tracking, which stores it. The retries ServiceDefaults adds to this client
    // repeat the POST with the same key, so a retry after a timeout can never open a second claim.
    public Task<HttpResponseMessage> OpenClaimAsync(string merchantId, OpenClaimRequest request, string idempotencyKey,
        CancellationToken ct) =>
        http.PostAsJsonAsync(ClaimsPath(merchantId),
            new TrackingOpenClaimRequest(request.ShipmentId, request.Reason, request.Note, idempotencyKey), ct);

    public Task<HttpResponseMessage> GetClaimAsync(string merchantId, Guid id, CancellationToken ct) =>
        http.GetAsync($"{ClaimsPath(merchantId)}/{id}", ct);

    public Task<HttpResponseMessage> ListClaimsAsync(string merchantId, Guid? shipmentId, int? limit, CancellationToken ct)
    {
        var query = new List<string>();
        if (shipmentId is { } id) query.Add($"shipmentId={id}");
        if (limit is { } take) query.Add($"limit={take}");

        return http.GetAsync(ClaimsPath(merchantId) + (query.Count > 0 ? "?" + string.Join("&", query) : ""), ct);
    }

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

    private static string ClaimsPath(string merchantId) => $"/merchants/{Uri.EscapeDataString(merchantId)}/claims";

    // Tracking's request shape: the public body plus the key from the header.
    private sealed record TrackingOpenClaimRequest(Guid ShipmentId, string Reason, string? Note, string IdempotencyKey);
}