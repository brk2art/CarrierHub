using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using System.Diagnostics;

namespace CarrierHub.CarrierGateway;

public sealed class PollingOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class DedupeOptions
{
    // Off only to measure the baseline.
    public bool Enabled { get; set; } = true;
}

// Polls every carrier for its active shipments and forwards what's new to Tracking.
public sealed class CarrierPoller(IServiceScopeFactory scopes, IOptions<PollingOptions> polling, IOptions<DedupeOptions> dedupe,
    ForwardedEvents forwarded, CarrierEventPublisher publisher, GatewayMetrics metrics, TimeProvider clock, 
    ILogger<CarrierPoller> logger) : BackgroundService
{
    // A background loop has no incoming request to hang a trace on, so each shipment poll starts one.
    // The carrier call, the bus send, and everything the message causes later join that trace.
    private static readonly ActivitySource Source = new("CarrierHub.Gateway");

    private sealed class Round
    {
        public int Published, Skipped;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(polling.Value.Interval, clock);
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
        var adapters = scope.ServiceProvider.GetServices<ICarrierAdapter>();

        // Carriers side by side: a slow or broken NordPost must not hold up FastShip.
        await Task.WhenAll(adapters.Select(adapter => PollCarrierAsync(adapter, tracking, ct)));
    }

    private async Task PollCarrierAsync(ICarrierAdapter adapter, TrackingClient tracking, CancellationToken ct)
    {
        IReadOnlyList<ActiveShipment> active;
        try
        {
            active = await tracking.GetActiveShipmentsAsync(adapter.Carrier, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not get active {Carrier} shipments from Tracking", adapter.Carrier);
            return;
        }

        if (active.Count == 0)
            return;

        var round = new Round();
        try
        {
            foreach (var shipment in active)
                await PollShipmentAsync(adapter, tracking, shipment, round, ct);
        }
        catch (CarrierRateLimitedException)
        {
            // Our own limit for this carrier is used up. The rest waits for the next round.
            logger.LogInformation("{Carrier}: request limit reached, remaining shipments wait for the next round", adapter.Carrier);
        }
        catch (BrokenCircuitException)
        {
            // The carrier failed too often and its circuit is open. Don't hammer it; try again next round.
            logger.LogWarning("{Carrier}: circuit open, skipping the rest of this round", adapter.Carrier);
        }

        var (published, skipped) = metrics.Totals;
        logger.LogInformation(
            "{Carrier} round: {Shipments} active, {Published} published, {Skipped} skipped. " +
               "Since start: {TotalPublished} published, {TotalSkipped} skipped",
            adapter.Carrier, active.Count, round.Published, round.Skipped, published, skipped);
    }

    private async Task PollShipmentAsync(ICarrierAdapter adapter, TrackingClient tracking, ActiveShipment shipment,
        Round round, CancellationToken ct)
    {
        using var activity = Source.StartActivity($"poll {adapter.Carrier}");
        activity?.SetTag("carrierhub.carrier", adapter.Carrier);
        activity?.SetTag("carrierhub.tracking_number", shipment.TrackingNumber);
        var publishedBefore = round.Published;

        try
        {
            // A polling API repeats its whole history every time, sometimes the same event twice in one answer.
            var events = (await adapter.GetEventsAsync(shipment.TrackingNumber, ct))
                .DistinctBy(e => e.CarrierEventId)
                .ToList();

            IReadOnlySet<string> alreadyForwarded = dedupe.Value.Enabled
                ? await forwarded.AlreadyForwardedAsync(adapter.Carrier, shipment, events)
                : new HashSet<string>();

            foreach (var e in events)
            {
                if (alreadyForwarded.Contains(e.CarrierEventId))
                {
                    round.Skipped++;
                    metrics.Record(adapter.Carrier, GatewayMetrics.Skipped);
                    continue;
                }

                await publisher.PublishAsync(shipment, e, ct);
                round.Published++;
                metrics.Record(adapter.Carrier, GatewayMetrics.Published);

                // Recorded only after the bus accepted it, so a crash in between means "publish again", never "lost".
                if (dedupe.Value.Enabled)
                    await forwarded.MarkForwardedAsync(adapter.Carrier, shipment, e.CarrierEventId);
            }
        }
        catch (Exception ex) when (ex is CarrierRateLimitedException or BrokenCircuitException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;   // about the whole carrier, not this shipment: let the carrier's round stop
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One failing shipment must not stop the others. Next round tries again.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Polling {Carrier} {TrackingNumber} failed", adapter.Carrier, shipment.TrackingNumber);
        }
        finally
        {
            activity?.SetTag("carrierhub.published", round.Published - publishedBefore);
        }
    }
}