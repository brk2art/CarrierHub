namespace CarrierHub.PublicApi;

public static class RateLimits
{
    public const string PerMerchant = "per-merchant";
}

// One token bucket per merchant: a burst of TokenLimit requests, then TokensPerPeriod per period.
// Read from "RateLimits:PerMerchant", so tests and environments can set their own values.
public sealed class MerchantRateLimit
{
    public int TokenLimit { get; set; } = 20;
    public int TokensPerPeriod { get; set; } = 10;
    public int PeriodSeconds { get; set; } = 1;
}