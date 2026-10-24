using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Messaging.ServiceBus;
using CarrierHub.Tracking.Data;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;

namespace CarrierHub.Tracking.Application;

// Moves outbox rows onto the "shipment-events" topic. Every row is published at least once.
public sealed class OutboxPublisher : BackgroundService
{
    public const string TopicName = "shipment-events";
    private const int BatchSize = 50;

    private static readonly ActivitySource Source = new("CarrierHub.Tracking.Outbox");

    private readonly ServiceBusClient _client;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxPublisher> _logger;
    private readonly Histogram<double> _wait;
    private readonly Histogram<int> _batchSize;
    private readonly Counter<long> _failures;

    public OutboxPublisher(ServiceBusClient client, IServiceScopeFactory scopes, TimeProvider clock,
        IMeterFactory meters, ILogger<OutboxPublisher> logger)
    {
        _client = client;
        _scopes = scopes;
        _clock = clock;
        _logger = logger;

        var meter = meters.Create("CarrierHub.Tracking.Outbox");

        // How long a row waited between the save and the bus. The default buckets are made for
        // milliseconds; ours are seconds, so they're set to where the outbox actually lives (0–1 s, more in outages).
        _wait = meter.CreateHistogram<double>("carrierhub.outbox.wait", "s",
            "Time from the change being saved to its message being on the bus",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2.5, 5, 10, 30, 60, 300],
            });

        // Rows found per check. Always 50 means the publisher is falling behind.
        _batchSize = meter.CreateHistogram<int>("carrierhub.outbox.batch_size", "{message}",
            "Rows found per publisher check (empty checks not counted)",
            tags: null,
            advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = [1, 2, 5, 10, 25, 50] });

        _failures = meter.CreateCounter<long>("carrierhub.outbox.publish_failures", "{message}",
            "Sends to the bus that failed; the row waits for the next check");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var sender = _client.CreateSender(TopicName);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Keep going while batches come back full, so a backlog drains quickly after an outage.
                while (await PublishBatchAsync(sender, stoppingToken) == BatchSize) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Outbox publishing failed; trying again in a second");
            }
        }
    }

    private async Task<int> PublishBatchAsync(ServiceBusSender sender, CancellationToken ct)
    {
        // This runs every second and almost always finds nothing. Traced, it was 3,600 empty traces an hour,
        // and the dashboard dropped the real ones to make room. Only actual publishes are traced (PublishOneAsync).
        using var quiet = SuppressInstrumentationScope.Begin();

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackingDbContext>();

        // Aspire turns on retries for database calls; a transaction we open ourselves must run inside them.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Lock this batch. Rows another Tracking instance is publishing right now are skipped, not waited for.
            var batch = await db.Outbox
                .FromSql($"""
                    SELECT * FROM "OutboxMessages"
                    WHERE "PublishedAt" IS NULL
                    ORDER BY "CreatedAt"
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            if (batch.Count > 0)
                _batchSize.Record(batch.Count);

            var published = 0;
            foreach (var message in batch)
            {
                if (!await PublishOneAsync(sender, message, ct))
                    break;   // stop here, so later messages can't overtake this one
                published++;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return published;
        });
    }

    private async Task<bool> PublishOneAsync(ServiceBusSender sender, OutboxMessage message, CancellationToken ct)
    {
        // Tracing back on for this one message, continuing the trace of the change that wrote the row.
        using var traced = SuppressInstrumentationScope.Begin(false);
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        using var activity = Source.StartActivity("outbox publish", ActivityKind.Internal, parent);
        activity?.SetTag("messaging.destination.name", TopicName);
        activity?.SetTag("carrierhub.outbox.type", message.Type);

        var waited = _clock.GetUtcNow() - message.CreatedAt;
        // How long the row waited: the gap you see in the trace between the change and its publish.
        activity?.SetTag("carrierhub.outbox.wait_ms", waited.TotalMilliseconds);

        try
        {
            await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(message.Payload))
            {
                MessageId = message.Id.ToString(),   // the topic drops a re-publish of the same row within its window
                SessionId = message.SessionId,       // one session per shipment: subscribers get its messages in order
                Subject = message.Type,
                ContentType = "application/json",
            }, ct);

            message.PublishedAt = _clock.GetUtcNow();
            _wait.Record(waited.TotalSeconds, new KeyValuePair<string, object?>("carrierhub.outbox.type", message.Type));
            return true;
        }
        catch (Exception ex) when (ex is ServiceBusException or TimeoutException)
        {
            message.Attempts++;
            message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _failures.Add(1);
            _logger.LogWarning(ex, "Could not publish outbox message {Id}; it waits for the next try", message.Id);
            return false;
        }
    }
}