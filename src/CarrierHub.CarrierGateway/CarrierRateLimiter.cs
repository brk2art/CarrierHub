using StackExchange.Redis;

namespace CarrierHub.CarrierGateway;

public sealed class CarrierRateLimitedException(string carrier)
    : Exception($"Our own request limit for {carrier} is used up for this minute.")
{
    public string Carrier { get; } = carrier;
}

// A fixed one-minute window per carrier, counted in Redis, so every Gateway instance shares the same limit.
public sealed class CarrierRateLimiter(IConnectionMultiplexer redis, TimeProvider clock, ILogger<CarrierRateLimiter> logger)
{
    public async Task<bool> TryAcquireAsync(string carrier, int perMinute)
    {
        if (perMinute <= 0)
            return true;   // no limit configured

        var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var key = $"ratelimit:{carrier}:{minute}";

        try
        {
            var db = redis.GetDatabase();
            var count = await db.StringIncrementAsync(key);
            if (count == 1)
                await db.KeyExpireAsync(key, TimeSpan.FromMinutes(2));   // old windows clean themselves up

            return count <= perMinute;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            // Fail open: the carrier's own 429s and our retries still protect it.
            logger.LogWarning(ex, "Redis unavailable: {Carrier} requests are not rate limited", carrier);
            return true;
        }
    }
}

// Sits inside the resilience pipeline, so every attempt (retries included) counts against the limit.
public sealed class CarrierRateLimitHandler(string carrier, int perMinute, CarrierRateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!await limiter.TryAcquireAsync(carrier, perMinute))
            throw new CarrierRateLimitedException(carrier);   // not an HTTP failure, so Polly doesn't retry it

        return await base.SendAsync(request, cancellationToken);
    }
}