using System.Security.Cryptography;
using System.Text.Json;
using CarrierHub.OpsAssistant.Data;
using Microsoft.EntityFrameworkCore;

namespace CarrierHub.OpsAssistant;

// One row per conversation: who owns it (a token hash), the session JSON, and when it was last used.
public sealed class ConversationRow
{
    public required string Id { get; init; }
    public required byte[] OwnerHash { get; init; }
    public required string Session { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Conversations in Postgres: they survive restarts and are shared by every instance of the assistant.
public sealed class PostgresConversationStore(AssistantDbContext db, TimeProvider clock) : IConversationStore
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(24);

    public async Task<JsonElement?> LoadAsync(string id, string token, CancellationToken ct)
    {
        var since = clock.GetUtcNow() - IdleTimeout;
        var row = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.UpdatedAt > since, ct);

        if (row is null || !CryptographicOperations.FixedTimeEquals(row.OwnerHash, IConversationStore.Hash(token)))
            return null;

        return JsonSerializer.Deserialize<JsonElement>(row.Session);
    }

    public async Task SaveAsync(string id, string token, JsonElement session, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var owner = IConversationStore.Hash(token);
        var row = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id, ct);

        if (row is null)
        {
            db.Conversations.Add(new ConversationRow
            {
                Id = id,
                OwnerHash = owner,
                Session = session.GetRawText(),
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            // Only reached after LoadAsync accepted this caller; checked again so a bug can't hand a chat to someone else.
            if (!CryptographicOperations.FixedTimeEquals(row.OwnerHash, owner))
                throw new InvalidOperationException("Conversation belongs to another caller.");
            row.Session = session.GetRawText();
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}