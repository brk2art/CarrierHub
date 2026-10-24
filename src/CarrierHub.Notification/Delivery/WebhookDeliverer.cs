using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CarrierHub.Notification.Data;
using Microsoft.EntityFrameworkCore;

namespace CarrierHub.Notification.Webhooks;

public enum DeliveryResult { Delivered, AlreadyDelivered, NoWebhook, Skipped, Failed, Invalid }

// The same scheme the Gateway checks for ParcelGo, with CarrierHub as the sender:
// HMAC-SHA256 over "{timestamp}.{body}", sent as "CarrierHub-Signature: t=...,v1=...".
public static class WebhookSignature
{
    public const string Header = "CarrierHub-Signature";

    public static string Compute(string secret, long timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
}

public sealed class WebhookDeliverer(NotificationDbContext db, HttpClient http, TimeProvider clock, ILogger<WebhookDeliverer> logger)
{
    public async Task<DeliveryResult> DeliverAsync(string messageId, string type, string merchantId, string payload, CancellationToken ct)
    {
        if (await db.Deliveries.AnyAsync(d => d.MessageId == messageId && d.Succeeded, ct))
            return DeliveryResult.AlreadyDelivered;   // the outbox delivers at least once; the merchant gets it once

        var webhook = await db.Webhooks.FindAsync([merchantId], ct);
        if (webhook is null)
            return DeliveryResult.NoWebhook;          // this merchant doesn't want webhooks

        var body = new JsonObject
        {
            ["id"] = messageId,
            ["type"] = type,
            ["data"] = JsonNode.Parse(payload),
        }.ToJsonString();

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.Header, $"t={timestamp},v1={WebhookSignature.Compute(webhook.Secret, timestamp, body)}");
        request.Headers.Add("CarrierHub-Event-Id", messageId);   // lets the merchant drop repeats on their side too
        request.Headers.Add("CarrierHub-Event-Type", type);

        var delivery = new Delivery
        {
            MessageId = messageId,
            MerchantId = merchantId,
            Type = type,
            Url = webhook.Url,
            AttemptedAt = clock.GetUtcNow(),
        };

        try
        {
            using var response = await http.SendAsync(request, ct);
            delivery.StatusCode = (int)response.StatusCode;
            delivery.Succeeded = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            delivery.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }

        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(ct);

        if (!delivery.Succeeded)
            logger.LogWarning("Webhook to {Merchant} failed: {Status} {Error}", merchantId, delivery.StatusCode, delivery.Error);

        return delivery.Succeeded ? DeliveryResult.Delivered : DeliveryResult.Failed;
    }
}