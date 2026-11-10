using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace CarrierHub.OpsAssistant;

// Conversations between requests: the agent's session, saved as JSON after every answer.
// A conversation holds a merchant's shipment data, so only the caller who started it may continue it: same id
// AND same token. A wrong token gets the same answer as an unknown id, so it learns nothing.
public interface IConversationStore
{
    static string NewId() => Guid.NewGuid().ToString("N");

    // Never the token itself: only its SHA-256 hash is kept.
    static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    Task<JsonElement?> LoadAsync(string id, string token, CancellationToken ct);
    Task SaveAsync(string id, string token, JsonElement session, CancellationToken ct);
}

// In memory: one instance, lost on restart, forgotten after 30 idle minutes. The evaluations use it.
public sealed class InMemoryConversationStore(IMemoryCache cache) : IConversationStore
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public Task SaveAsync(string id, string token, JsonElement session, CancellationToken ct)
    {
        cache.Set(Key(id), new Entry(IConversationStore.Hash(token), session.Clone()),
            new MemoryCacheEntryOptions { SlidingExpiration = IdleTimeout });
        return Task.CompletedTask;
    }

    public Task<JsonElement?> LoadAsync(string id, string token, CancellationToken ct) =>
        Task.FromResult<JsonElement?>(
            cache.TryGetValue(Key(id), out Entry? entry) && entry is not null &&
            CryptographicOperations.FixedTimeEquals(entry.TokenHash, IConversationStore.Hash(token))
                ? entry.Session
                : null);

    private static string Key(string id) => $"conversation:{id}";

    private sealed record Entry(byte[] TokenHash, JsonElement Session);
}