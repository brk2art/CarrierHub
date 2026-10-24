using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CarrierHub.MockCarriers;

public sealed record ReceivedWebhook(DateTimeOffset ReceivedAt, string? EventId, string? EventType, string? Signature, JsonNode? Body);

public sealed class MerchantInbox
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ReceivedWebhook>> _inboxes = new();

    public void Add(string merchantId, ReceivedWebhook webhook) => _inboxes.GetOrAdd(merchantId, _ => new()).Enqueue(webhook);

    public IReadOnlyList<ReceivedWebhook> For(string merchantId) =>
        _inboxes.TryGetValue(merchantId, out var inbox) ? inbox.ToList() : [];

    public void Clear() => _inboxes.Clear();
}

// A merchant's server receiving CarrierHub's webhooks. Its "errorRate" fault makes it answer 500.
public static class MockMerchant
{
    public const string Name = "merchant";

    public static void MapMockMerchant(this WebApplication app)
    {
        app.MapPost("/merchant-hooks/{merchantId}",
            async (string merchantId, HttpRequest request, MerchantInbox inbox, CarrierState state, TimeProvider clock) =>
            {
                if (Random.Shared.NextDouble() < state.FaultsFor(Name).ErrorRate)
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);   // the merchant's server is having a bad day

                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();

                inbox.Add(merchantId, new ReceivedWebhook(
                    clock.GetUtcNow(),
                    request.Headers["CarrierHub-Event-Id"],
                    request.Headers["CarrierHub-Event-Type"],
                    request.Headers["CarrierHub-Signature"],
                    JsonNode.Parse(body)));

                return Results.Ok();
            });

        // What the merchant received, in arrival order.
        app.MapGet("/merchant-hooks/{merchantId}", (string merchantId, MerchantInbox inbox) => inbox.For(merchantId));
    }
}