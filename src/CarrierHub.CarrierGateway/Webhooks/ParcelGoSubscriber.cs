namespace CarrierHub.CarrierGateway.Webhooks;

public sealed class ParcelGoClient(HttpClient http)
{
    public async Task SubscribeAsync(string trackingNumber, string callbackUrl, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/parcelgo/v1/subscriptions", new { trackingNumber, callbackUrl }, ct);
        response.EnsureSuccessStatusCode();
    }
}

// Subscribes every active ParcelGo shipment to webhooks. Subscribing again is harmless, so this simply
// repeats each round. Phase 3 does it once, when the "shipment created" message arrives.
public sealed class ParcelGoSubscriber(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock,
    ILogger<ParcelGoSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), clock);
        do
        {
            await SubscribeActiveShipmentsAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SubscribeActiveShipmentsAsync(CancellationToken ct)
    {
        var callbackBase = config["ParcelGo:CallbackBaseUrl"];
        if (string.IsNullOrEmpty(callbackBase))
        {
            logger.LogWarning("ParcelGo:CallbackBaseUrl is not set; ParcelGo shipments are not subscribed");
            return;
        }

        var callbackUrl = $"{callbackBase.TrimEnd('/')}/webhooks/parcelgo";

        using var scope = scopes.CreateScope();
        var tracking = scope.ServiceProvider.GetRequiredService<TrackingClient>();
        var parcelGo = scope.ServiceProvider.GetRequiredService<ParcelGoClient>();

        try
        {
            foreach (var shipment in await tracking.GetActiveShipmentsAsync(ParcelGoWebhook.Carrier, ct))
                await parcelGo.SubscribeAsync(shipment.TrackingNumber, callbackUrl, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Subscribing ParcelGo shipments failed; trying again next round");
        }
    }
}