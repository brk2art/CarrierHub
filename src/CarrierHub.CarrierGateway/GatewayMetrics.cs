using System.Diagnostics.Metrics;

namespace CarrierHub.CarrierGateway;

// What happened to each carrier event the Gateway saw. Exported through OpenTelemetry,
// and kept as totals for /stats and the round summary.
public sealed class GatewayMetrics
{
    public const string MeterName = "CarrierHub.Gateway";

    public const string Published = "published";   // put on the bus for Tracking
    public const string Skipped = "skipped";       // not sent: Redis says it was published before

    private readonly Counter<long> _events;
    private long _published, _skipped;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _events = meter.CreateCounter<long>("carrierhub.gateway.events", unit: "{event}",
            description: "Carrier events seen by the Gateway, by outcome: published or skipped");
    }

    public void Record(string carrier, string outcome)
    {
        _events.Add(1,
            new KeyValuePair<string, object?>("carrier", carrier),
            new KeyValuePair<string, object?>("outcome", outcome));

        if (outcome == Published)
            Interlocked.Increment(ref _published);
        else if (outcome == Skipped)
            Interlocked.Increment(ref _skipped);
    }

    // Since this process started.
    public (long Published, long Skipped) Totals => (Interlocked.Read(ref _published), Interlocked.Read(ref _skipped));
}