using Microsoft.Extensions.Options;

namespace CarrierHub.CarrierGateway;

public sealed class PollingOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
}

// Polls every carrier for its active shipments and forwards what it finds to Tracking.
public sealed class CarrierPoller(IServiceScopeFactory scopes, IOptions<PollingOptions> options,
    TimeProvider clock, ILogger<CarrierPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval, clock);
        do
        {
            await PollOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        // A new scope every round: typed HttpClients are meant to be short-lived. Held by this long-lived
        // service, they would keep one connection pool forever and miss DNS changes.
        using var scope = scopes.CreateScope();
        var tracking = scope.ServiceProvider.GetRequiredService<TrackingClient>();

        foreach (var adapter in scope.ServiceProvider.GetServices<ICarrierAdapter>())
        {
            IReadOnlyList<ActiveShipment> active;
            try
            {
                active = await tracking.GetActiveShipmentsAsync(adapter.Carrier, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not get active {Carrier} shipments from Tracking", adapter.Carrier);
                continue;
            }

            foreach (var shipment in active)
                await PollShipmentAsync(adapter, tracking, shipment, ct);
        }
    }

    private async Task PollShipmentAsync(ICarrierAdapter adapter, TrackingClient tracking, ActiveShipment shipment, CancellationToken ct)
    {
        try
        {
            var events = await adapter.GetEventsAsync(shipment.TrackingNumber, ct);

            // A polling API repeats its whole history every time, sometimes the same event twice in one answer.
            // Tracking ignores duplicates anyway (ADR 2); dropping the obvious ones here only saves calls.
            foreach (var e in events.DistinctBy(e => e.CarrierEventId))
            {
                var result = await tracking.SendEventAsync(shipment, e, ct);
                if (result.StatusChanged)
                    logger.LogInformation("{Carrier} {TrackingNumber} is now {Status}",
                        adapter.Carrier, shipment.TrackingNumber, e.Status);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One failing shipment or carrier must not stop the others. Next round tries again.
            logger.LogWarning(ex, "Polling {Carrier} {TrackingNumber} failed", adapter.Carrier, shipment.TrackingNumber);
        }
    }
}