using System.Diagnostics.Metrics;

namespace CarrierHub.CarrierGateway;

// What happened to each carrier event the Gateway saw, and how well polling keeps up. Exported through
// OpenTelemetry, and the event counts are also kept as totals for /stats and the round summary.
public sealed class GatewayMetrics
{
    public const string MeterName = "CarrierHub.Gateway";

    public const string Published = "published";   // put on the bus for Tracking
    public const string Skipped = "skipped";       // not sent: Redis says it was published before

    private readonly Counter<long> _events;
    private readonly Counter<long> _polls;
    private readonly Histogram<double> _roundDuration;
    private readonly Histogram<double> _eventAge;
    private readonly Histogram<double> _pollGap;
    private long _published, _skipped;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _events = meter.CreateCounter<long>("carrierhub.gateway.events", unit: "{event}",
            description: "Carrier events seen by the Gateway, by outcome: published or skipped");

        // Every round, every active shipment is either polled or deferred because the carrier's request
        // budget ran out. Deferred above zero means the carrier has more active shipments than we can afford.
        _polls = meter.CreateCounter<long>("carrierhub.gateway.polls", unit: "{shipment}",
            description: "Active shipments per round, by outcome: polled or deferred");

        // Shipments of one carrier are polled one after another. When a round takes longer than the
        // polling interval, the next one starts late and every event waits longer to be noticed.
        _roundDuration = meter.CreateHistogram<double>("carrierhub.gateway.round.duration", "s",
            "Time to poll all active shipments of one carrier",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30, 60],
            });

        // From the scan at the carrier to our publish. delivery_time in Notification starts only when
        // Tracking saves the change, so a Gateway that falls behind is visible here and nowhere else.
        _eventAge = meter.CreateHistogram<double>("carrierhub.gateway.event.age", "s",
            "Time from the event happening at the carrier to the Gateway publishing it",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [1, 2.5, 5, 7.5, 10, 15, 20, 30, 60, 120, 180, 300, 600, 1200],
            });

        // Time between two polls of the same shipment: the fairness measure. With a shared budget it should
        // be about the same for every shipment; a long tail means some shipments are being left behind.
        _pollGap = meter.CreateHistogram<double>("carrierhub.gateway.poll.gap", "s",
            "Time between two polls of the same shipment",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600, 1200],
            });
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

    public void RecordRound(string carrier, TimeSpan duration) =>
        _roundDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("carrier", carrier));

    public void RecordEventAge(string carrier, TimeSpan age) =>
        _eventAge.Record(age.TotalSeconds, new KeyValuePair<string, object?>("carrier", carrier));

    public void RecordPollGap(string carrier, TimeSpan gap) =>
        _pollGap.Record(gap.TotalSeconds, new KeyValuePair<string, object?>("carrier", carrier));

    public void RecordPolls(string carrier, int polled, int deferred)
    {
        _polls.Add(polled,
            new KeyValuePair<string, object?>("carrier", carrier),
            new KeyValuePair<string, object?>("outcome", "polled"));
        _polls.Add(deferred,
            new KeyValuePair<string, object?>("carrier", carrier),
            new KeyValuePair<string, object?>("outcome", "deferred"));
    }

    // Since this process started.
    public (long Published, long Skipped) Totals => (Interlocked.Read(ref _published), Interlocked.Read(ref _skipped));
}