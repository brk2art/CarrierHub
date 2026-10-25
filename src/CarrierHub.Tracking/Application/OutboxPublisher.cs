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
            "Messages whose send to the bus failed; they wait for the next check");
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
        // and the dashboard dropped the real ones to make room. Only the messages and the send are traced.
        using var quiet = SuppressInstrumentationScope.Begin();

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackingDbContext>();

        // Aspire turns on retries for database calls; a transaction we open ourselves must run inside them.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Lock this batch. Rows another Tracking instance is publishing right now are skipped, not waited for.
            var rows = await db.Outbox
                .FromSql($"""
                    SELECT * FROM "OutboxMessages"
                    WHERE "PublishedAt" IS NULL
                    ORDER BY "CreatedAt"
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            if (rows.Count == 0)
                return 0;

            _batchSize.Record(rows.Count);

            var published = await SendAsync(sender, rows, ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return published;
        });
    }

    // One round trip for the whole batch. Sent one at a time, each row waited for the bus to answer the one before
    // it, and under load the outbox fell minutes behind at about 5 messages a second (ADR 13). Same order, same
    // MessageIds (so the topic's duplicate detection still works); the batch succeeds or fails as a whole.
    private async Task<int> SendAsync(ServiceBusSender sender, List<OutboxMessage> rows, CancellationToken ct)
    {
        using var traced = SuppressInstrumentationScope.Begin(false);
        using var batch = await sender.CreateMessageBatchAsync(ct);

        var added = new List<OutboxMessage>(rows.Count);
        foreach (var row in rows)
        {
            // Each message continues the trace of the change that wrote it: the SDK stamps the current span's
            // context on the message as it's added, so Notification's processing joins this trace.
            ActivityContext.TryParse(row.TraceParent, null, out var parent);
            using var activity = Source.StartActivity("outbox publish", ActivityKind.Internal, parent);
            activity?.SetTag("messaging.destination.name", TopicName);
            activity?.SetTag("carrierhub.outbox.type", row.Type);
            activity?.SetTag("carrierhub.outbox.wait_ms", (_clock.GetUtcNow() - row.CreatedAt).TotalMilliseconds);

            if (!batch.TryAddMessage(ToMessage(row)))
                break;   // the batch is full (size limit): the rest go in the next one, still in order

            added.Add(row);
        }

        if (added.Count == 0)
        {
            // Even the first row doesn't fit in a batch on its own. Retrying won't help; a person has to look.
            rows[0].Attempts++;
            rows[0].LastError = "Message too large for a batch.";
            _logger.LogError("Outbox message {Id} is too large to publish", rows[0].Id);
            return 0;
        }

        try
        {
            await sender.SendMessagesAsync(batch, ct);
        }
        catch (Exception ex) when (ex is ServiceBusException or TimeoutException)
        {
            var first = added[0];
            first.Attempts++;
            first.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            _failures.Add(added.Count);
            _logger.LogWarning(ex, "Could not publish {Count} outbox messages from {Id}; they wait for the next try",
                added.Count, first.Id);
            return 0;   // nothing marked: the whole batch is tried again, in the same order
        }

        var now = _clock.GetUtcNow();
        foreach (var row in added)
        {
            row.PublishedAt = now;
            _wait.Record((now - row.CreatedAt).TotalSeconds, new KeyValuePair<string, object?>("carrierhub.outbox.type", row.Type));
        }

        return added.Count;
    }

    private static ServiceBusMessage ToMessage(OutboxMessage row) => new(BinaryData.FromString(row.Payload))
    {
        MessageId = row.Id.ToString(),   // the topic drops a re-publish of the same row within its window
        SessionId = row.SessionId,       // one session per shipment: subscribers get its messages in order
        Subject = row.Type,
        ContentType = "application/json",
    };
}