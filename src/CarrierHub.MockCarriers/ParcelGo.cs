using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CarrierHub.MockCarriers;

// ParcelGo: pushes events by webhook instead of being polled. Signs every webhook with HMAC-SHA256.
public static class ParcelGo
{
    public const string Name = "parcelgo";

    public sealed record SubscribeRequest(string TrackingNumber, string CallbackUrl);

    public static void MapParcelGo(this WebApplication app) =>
        app.MapPost("/parcelgo/v1/subscriptions", (SubscribeRequest request, ParcelGoSubscriptions subscriptions, CarrierState state) =>
        {
            if (string.IsNullOrWhiteSpace(request.TrackingNumber) || !Uri.TryCreate(request.CallbackUrl, UriKind.Absolute, out _))
                return Results.BadRequest(new { error = "trackingNumber and an absolute callbackUrl are required" });

            subscriptions.Add(request.TrackingNumber, request.CallbackUrl);
            state.HappenedSoFar(Name, request.TrackingNumber);   // starts the journey, unless it was booked earlier
            return Results.Ok(new { request.TrackingNumber, request.CallbackUrl });
        });

    // ParcelGo's own signing: HMAC-SHA256 over "{timestamp}.{body}", sent as "t=...,v1=...".
    public static string Sign(string secret, long timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    public static string Status(Step step) => step switch
    {
        Step.PickedUp => "picked_up",
        Step.InTransit => "in_transit",
        Step.OutForDelivery => "out_for_delivery",
        Step.DeliveryFailed => "delivery_attempt_failed",
        Step.Delivered => "delivered",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };
}

public sealed class ParcelGoSubscription(string trackingNumber, string callbackUrl)
{
    public string TrackingNumber { get; } = trackingNumber;
    public string CallbackUrl { get; set; } = callbackUrl;
    public ConcurrentDictionary<string, byte> Delivered { get; } = new();             // event IDs the receiver accepted
    public ConcurrentDictionary<string, DateTimeOffset> HeldUntil { get; } = new();   // for the shuffle fault

    // Every event of the journey was accepted: nothing left to send, so the sender skips it.
    public bool Finished { get; set; }
}

public sealed class ParcelGoSubscriptions
{
    private readonly ConcurrentDictionary<string, ParcelGoSubscription> _subscriptions = new();

    // Subscribing again is harmless: same subscription, possibly a new URL, nothing sent twice because of it.
    public void Add(string trackingNumber, string callbackUrl) =>
        _subscriptions.AddOrUpdate(trackingNumber,
            _ => new ParcelGoSubscription(trackingNumber, callbackUrl),
            (_, existing) => { existing.CallbackUrl = callbackUrl; return existing; });

    public IReadOnlyCollection<ParcelGoSubscription> All => _subscriptions.Values.ToList();

    public void Clear() => _subscriptions.Clear();
}

// Sends each event when it "happens", signed. Like real webhook senders: at least once, retried until a 2xx.
public sealed class ParcelGoWebhookSender(ParcelGoSubscriptions subscriptions, CarrierState state, IHttpClientFactory httpClients,
    IConfiguration config, TimeProvider clock, ILogger<ParcelGoWebhookSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var secret = config["ParcelGo:WebhookSecret"];
            if (string.IsNullOrEmpty(secret))
            {
                logger.LogWarning("ParcelGo:WebhookSecret is not set; no webhooks are sent");
                continue;
            }

            // A real carrier's webhook service sends for many shipments at once. Sent one at a time, a single slow
            // answer held up every other shipment, and this sender, not CarrierHub, set the load test's ceiling
            // (about 6 webhooks a second; ADR 13). Within one shipment, events still go one at a time, in order.
            await Parallel.ForEachAsync(
                subscriptions.All.Where(s => !s.Finished),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = config.GetValue("ParcelGo:SenderParallelism", 32),
                    CancellationToken = stoppingToken,
                },
                async (subscription, ct) => await SendDueEventsAsync(subscription, secret, ct));
        }
    }

    private async Task SendDueEventsAsync(ParcelGoSubscription subscription, string secret, CancellationToken ct)
    {
        var faults = state.FaultsFor(ParcelGo.Name);
        var now = clock.GetUtcNow();

        foreach (var e in state.HappenedSoFar(ParcelGo.Name, subscription.TrackingNumber))
        {
            var id = $"pg_{subscription.TrackingNumber}_{e.Step.Index}";
            if (subscription.Delivered.ContainsKey(id))
                continue;

            // Shuffle: hold each event back for a random while, so later events can overtake earlier ones.
            if (faults.Shuffle && now < subscription.HeldUntil.GetOrAdd(id, _ => now.AddSeconds(Random.Shared.Next(0, 30))))
                continue;

            var body = JsonSerializer.Serialize(new
            {
                event_id = id,
                tracking_number = subscription.TrackingNumber,
                status = ParcelGo.Status(e.Step.Step),
                occurred_at = e.At.ToUnixTimeSeconds(),
                location = e.Step.Location,
            });

            if (await SendAsync(subscription.CallbackUrl, body, secret, ct))
            {
                subscription.Delivered[id] = 0;

                // At least once: sometimes the same webhook arrives twice.
                if (Random.Shared.NextDouble() < faults.DuplicateRate)
                    await SendAsync(subscription.CallbackUrl, body, secret, ct);
            }
            // Not accepted: it's sent again on the next tick.
        }

        if (subscription.Delivered.Count >= Journeys.For(subscription.TrackingNumber).Count)
            subscription.Finished = true;
    }

    private async Task<bool> SendAsync(string url, string body, string secret, CancellationToken ct)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("ParcelGo-Signature", $"t={timestamp},v1={ParcelGo.Sign(secret, timestamp, body)}");

        try
        {
            using var response = await httpClients.CreateClient("webhooks").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Webhook to {Url} answered {Status}", url, (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Webhook to {Url} failed", url);
            return false;
        }
    }
}