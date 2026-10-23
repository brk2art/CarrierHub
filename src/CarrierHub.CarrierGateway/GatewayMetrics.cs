using System.Diagnostics.Metrics;

namespace CarrierHub.CarrierGateway;

// What happened to each carrier event the Gateway saw. Exported through OpenTelemetry,
// and kept as totals for the round summary in the logs.
public sealed class GatewayMetrics
{
    public const string MeterName = "CarrierHub.Gateway";

    public const string Added = "added";            // sent, and new to Tracking
    public const string Duplicate = "duplicate";    // sent, but Tracking already had it: wasted work
    public const string Skipped = "skipped";        // not sent: Redis says it was forwarded before

    private readonly Counter<long> _events;
    private long _added, _duplicates, _skipped;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _events = meter.CreateCounter<long>("carrierhub.gateway.events", unit: "{event}",
            description: "Carrier events seen by the Gateway, by outcome: added, duplicate or skipped");
    }

    public void Record(string carrier, string outcome)
    {
        _events.Add(1,
            new KeyValuePair<string, object?>("carrier", carrier),
            new KeyValuePair<string, object?>("outcome", outcome));

        switch (outcome)
        {
            case Added: Interlocked.Increment(ref _added); break;
            case Duplicate: Interlocked.Increment(ref _duplicates); break;
            case Skipped: Interlocked.Increment(ref _skipped); break;
        }
    }

    // Since this process started.
    public (long Added, long Duplicates, long Skipped) Totals =>
        (Interlocked.Read(ref _added), Interlocked.Read(ref _duplicates), Interlocked.Read(ref _skipped));
}