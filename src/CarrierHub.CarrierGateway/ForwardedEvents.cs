using StackExchange.Redis;

namespace CarrierHub.CarrierGateway;

// Remembers, per shipment, which carrier events were already forwarded to Tracking.
// An optimisation only: Tracking rejects duplicates by itself (ADR 2), so if Redis is down
// we forward everything and are slower, never wrong.
public sealed class ForwardedEvents(IConnectionMultiplexer redis, ILogger<ForwardedEvents> logger)
{
    // Delivered shipments stop being polled; their keys clean themselves up after this.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private static RedisKey Key(string carrier, ActiveShipment shipment) =>
        $"forwarded:{carrier}:{shipment.TrackingNumber}";

    // The IDs among these events that were forwarded before. One round trip for the whole batch (SMISMEMBER).
    public async Task<IReadOnlySet<string>> AlreadyForwardedAsync(string carrier, ActiveShipment shipment,
        IReadOnlyList<CarrierEvent> events)
    {
        if (events.Count == 0)
            return new HashSet<string>();

        try
        {
            var ids = events.Select(e => (RedisValue)e.CarrierEventId).ToArray();
            var seen = await redis.GetDatabase().SetContainsAsync(Key(carrier, shipment), ids);

            return events.Where((_, i) => seen[i]).Select(e => e.CarrierEventId).ToHashSet();
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Redis unavailable: forwarding without dedupe");
            return new HashSet<string>();
        }
    }

    // Called only after Tracking confirmed the event, so a crash in between means "send again", never "lost".
    public async Task MarkForwardedAsync(string carrier, ActiveShipment shipment, string carrierEventId)
    {
        try
        {
            var db = redis.GetDatabase();
            var key = Key(carrier, shipment);
            await db.SetAddAsync(key, carrierEventId);
            await db.KeyExpireAsync(key, KeepFor);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Could not record a forwarded event in Redis");
        }
    }
}