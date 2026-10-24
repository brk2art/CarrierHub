using System.Diagnostics.Metrics;
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
    // Static: one meter for the process, while a deliverer is created per message. Keeping it out of the
    // constructor also leaves the tests' way of creating a deliverer unchanged.
    private static readonly Meter Meter = new("CarrierHub.Notification");

    // What the merchant actually experiences: from the status change in Tracking to their 2xx answer.
    // It includes every hop: outbox wait, both bus hops, retries and redeliveries.
    private static readonly Histogram<double> DeliveryTime = Meter.CreateHistogram<double>(
        "carrierhub.webhook.delivery_time", "s",
        "Time from the status change to the merchant accepting its webhook",
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2.5, 5, 10, 30, 60, 300, 900],
        });

    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>(
        "carrierhub.webhook.deliveries", "{delivery}",
        "Webhook deliveries by outcome: delivered, failed, already_delivered, no_webhook");

    public async Task<DeliveryResult> DeliverAsync(string messageId, string type, string merchantId, string payload, CancellationToken ct)
    {
        if (await db.Deliveries.AnyAsync(d => d.MessageId == messageId && d.Succeeded, ct))
        {
            Count("already_delivered", type);
            return DeliveryResult.AlreadyDelivered;   // the outbox delivers at least once; the merchant gets it once
        }

        var webhook = await db.Webhooks.FindAsync([merchantId], ct);
        if (webhook is null)
        {
            Count("no_webhook", type);
            return DeliveryResult.NoWebhook;          // this merchant doesn't want webhooks
        }

        var data = JsonNode.Parse(payload);
        var changedAt = ChangedAt(data);

        var body = new JsonObject
        {
            ["id"] = messageId,
            ["type"] = type,
            ["data"] = data,
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

        if (delivery.Succeeded && changedAt is { } at)
        {
            DeliveryTime.Record((clock.GetUtcNow() - at).TotalSeconds,
                new KeyValuePair<string, object?>("type", type));
        }
        Count(delivery.Succeeded ? "delivered" : "failed", type);

        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(ct);

        if (!delivery.Succeeded)
            logger.LogWarning("Webhook to {Merchant} failed: {Status} {Error}", merchantId, delivery.StatusCode, delivery.Error);

        return delivery.Succeeded ? DeliveryResult.Delivered : DeliveryResult.Failed;
    }

    private static void Count(string outcome, string type) =>
        Outcomes.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("type", type));

    // When the change happened in Tracking: "changedAt" for status changes, "createdAt" for a new shipment.
    private static DateTimeOffset? ChangedAt(JsonNode? data)
    {
        if (data is not JsonObject obj)
            return null;
        if (obj["changedAt"] is JsonValue changed && changed.TryGetValue<DateTimeOffset>(out var changedAt))
            return changedAt;
        if (obj["createdAt"] is JsonValue created && created.TryGetValue<DateTimeOffset>(out var createdAt))
            return createdAt;
        return null;
    }
}