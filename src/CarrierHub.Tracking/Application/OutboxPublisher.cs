using Azure.Messaging.ServiceBus;
using CarrierHub.Tracking.Data;
using Microsoft.EntityFrameworkCore;

namespace CarrierHub.Tracking.Application;

// Moves outbox rows onto the "shipment-events" topic. Every row is published at least once.
public sealed class OutboxPublisher(ServiceBusClient client, IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    public const string TopicName = "shipment-events";
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var sender = client.CreateSender(TopicName);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Keep going while batches come back full, so a backlog drains quickly after an outage.
                while (await PublishBatchAsync(sender, stoppingToken) == BatchSize) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox publishing failed; trying again in a second");
            }
        }
    }

    private async Task<int> PublishBatchAsync(ServiceBusSender sender, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
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

            var published = 0;
            foreach (var message in batch)
            {
                try
                {
                    await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(message.Payload))
                    {
                        MessageId = message.Id.ToString(),   // the topic drops a re-publish of the same row within its window
                        SessionId = message.SessionId,       // one session per shipment: subscribers get its messages in order
                        Subject = message.Type,
                        ContentType = "application/json",
                    }, ct);

                    message.PublishedAt = clock.GetUtcNow();
                    published++;
                }
                catch (Exception ex) when (ex is ServiceBusException or TimeoutException)
                {
                    message.Attempts++;
                    message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    logger.LogWarning(ex, "Could not publish outbox message {Id}; it waits for the next try", message.Id);
                    break;   // stop here, so later messages can't overtake this one
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return published;
        });
    }
}