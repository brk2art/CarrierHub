namespace CarrierHub.PublicApi;

public static class Claims
{
    // The merchant the caller acts for. The only source of the merchant ID in this API.
    public const string MerchantId = "merchant_id";
}

public static class Scopes
{
    public const string Read = "shipments:read";
    public const string Write = "shipments:write";
}

public static class Policies
{
    public const string Read = "shipments.read";
    public const string Write = "shipments.write";
}