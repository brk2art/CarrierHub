using System.Net.Http.Json;
using CarrierHub.PublicApi.V1;

namespace CarrierHub.PublicApi;

// Calls the Notification service, found through Aspire's service discovery like Tracking.
public sealed class NotificationClient(HttpClient http)
{
    public Task<HttpResponseMessage> RegisterAsync(string merchantId, RegisterWebhookRequest request, CancellationToken ct) =>
        http.PutAsJsonAsync($"{MerchantPath(merchantId)}/webhook", request, ct);

    public Task<HttpResponseMessage> GetAsync(string merchantId, CancellationToken ct) =>
        http.GetAsync($"{MerchantPath(merchantId)}/webhook", ct);

    public Task<HttpResponseMessage> DeleteAsync(string merchantId, CancellationToken ct) =>
        http.DeleteAsync($"{MerchantPath(merchantId)}/webhook", ct);

    public Task<HttpResponseMessage> DeliveriesAsync(string merchantId, CancellationToken ct) =>
        http.GetAsync($"{MerchantPath(merchantId)}/deliveries", ct);

    private static string MerchantPath(string merchantId) => $"/merchants/{Uri.EscapeDataString(merchantId)}";
}