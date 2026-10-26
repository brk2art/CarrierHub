using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CarrierHub.OpsAssistant;

public sealed record ApiResult<T>(int StatusCode, T? Value);

public sealed record ShipmentCount(int Count);

// The assistant's only way to data: the Public API, with the caller's own token. The API decides what this
// merchant may see, exactly as for any other client, so the assistant can't see more than the person asking.
public sealed class PublicApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ApiResult<List<Shipment>>> SearchAsync(string token, string? status, bool? late, string? reference,
        int? limit, CancellationToken ct) =>
        GetAsync<List<Shipment>>(token, "/v1/shipments" + Query(status, late, reference, limit), ct);

    public Task<ApiResult<ShipmentCount>> CountAsync(string token, string? status, bool? late, string? reference,
        CancellationToken ct) =>
        GetAsync<ShipmentCount>(token, "/v1/shipments/count" + Query(status, late, reference, limit: null), ct);

    private async Task<ApiResult<T>> GetAsync<T>(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new((int)response.StatusCode, default);

        return new(200, await response.Content.ReadFromJsonAsync<T>(Json, ct));
    }

    private static string Query(string? status, bool? late, string? reference, int? limit)
    {
        var query = new List<string>();
        if (status is not null) query.Add($"status={Uri.EscapeDataString(status)}");
        if (late is not null) query.Add($"late={(late.Value ? "true" : "false")}");
        if (reference is not null) query.Add($"reference={Uri.EscapeDataString(reference)}");
        if (limit is not null) query.Add($"limit={limit.Value}");

        return query.Count > 0 ? "?" + string.Join('&', query) : "";
    }
}