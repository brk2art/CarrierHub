using System.Security.Cryptography;
using Azure.Messaging.ServiceBus;
using CarrierHub.Notification.Data;
using CarrierHub.Notification.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace CarrierHub.Notification.Api;

public sealed record RegisterWebhookRequest(string Url);

// Internal endpoints. The Public API will call these with the merchant from the caller's token (step 3b).
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        var merchant = app.MapGroup("/merchants/{merchantId}");
        merchant.MapPut("/webhook", RegisterAsync);
        merchant.MapGet("/webhook", GetAsync);
        merchant.MapDelete("/webhook", DeleteAsync);
        merchant.MapGet("/deliveries", DeliveriesAsync);
    }

    public static void MapDeadLetterReplay(this WebApplication app) =>
        app.MapPost("/admin/dead-letters/replay", ReplayAsync);

    private static async Task<IResult> RegisterAsync(string merchantId, RegisterWebhookRequest request,
        NotificationDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return Results.BadRequest(new { error = "An absolute https URL is required." });

        var existing = await db.Webhooks.FindAsync([merchantId], ct);
        if (existing is not null)
        {
            existing.Url = url.AbsoluteUri;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { existing.MerchantId, existing.Url });   // the secret stays the same and isn't shown again
        }

        var webhook = new WebhookSubscription
        {
            MerchantId = merchantId,
            Url = url.AbsoluteUri,
            Secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync(ct);

        // Shown once, now: the merchant keeps it to check our signatures.
        return Results.Created($"/merchants/{merchantId}/webhook", new { webhook.MerchantId, webhook.Url, webhook.Secret });
    }

    private static async Task<IResult> GetAsync(string merchantId, NotificationDbContext db, CancellationToken ct) =>
        await db.Webhooks.FindAsync([merchantId], ct) is { } w
            ? Results.Ok(new { w.MerchantId, w.Url, w.CreatedAt })
            : Results.NotFound();

    private static async Task<IResult> DeleteAsync(string merchantId, NotificationDbContext db, CancellationToken ct)
    {
        if (await db.Webhooks.FindAsync([merchantId], ct) is { } w)
        {
            db.Webhooks.Remove(w);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    // The latest attempts, so a merchant can see why a webhook didn't arrive.
    private static async Task<IResult> DeliveriesAsync(string merchantId, NotificationDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Deliveries
            .Where(d => d.MerchantId == merchantId)
            .OrderByDescending(d => d.AttemptedAt)
            .Take(20)
            .Select(d => new { d.MessageId, d.Type, d.StatusCode, d.Succeeded, d.Error, d.AttemptedAt })
            .ToListAsync(ct));

    // Takes messages from the dead-letter queue and tries them again, in the order they were parked.
    // Stops at the first failure, so a shipment's messages never overtake each other.
    private static async Task<IResult> ReplayAsync(ServiceBusClient client, IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var receiver = client.CreateReceiver(NotificationConsumer.TopicName, NotificationConsumer.SubscriptionName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var messages = await receiver.ReceiveMessagesAsync(maxMessages: 50, maxWaitTime: TimeSpan.FromSeconds(2), ct);

        var replayed = 0;
        var stoppedAt = (string?)null;

        foreach (var message in messages)
        {
            if (stoppedAt is not null)
            {
                await receiver.AbandonMessageAsync(message, cancellationToken: ct);   // left in the dead-letter queue
                continue;
            }

            using var scope = scopes.CreateScope();
            var result = await NotificationConsumer.HandleOneAsync(message, scope.ServiceProvider, ct);

            if (result is DeliveryResult.Failed or DeliveryResult.Invalid)
            {
                await receiver.AbandonMessageAsync(message, cancellationToken: ct);
                stoppedAt = message.MessageId;
                continue;
            }

            await receiver.CompleteMessageAsync(message, ct);
            replayed++;
        }

        return Results.Ok(new { found = messages.Count, replayed, stoppedAt });
    }
}