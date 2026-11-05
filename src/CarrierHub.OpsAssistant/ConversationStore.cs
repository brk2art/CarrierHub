using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CarrierHub.OpsAssistant;

// Conversations between requests: the agent's session, saved as JSON after every answer.
// In memory for now: one instance, lost on restart, forgotten after 30 idle minutes.
public sealed class ConversationStore(IMemoryCache cache)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public static string NewId() => Guid.NewGuid().ToString("N");

    public void Save(string id, string token, JsonElement session) =>
        cache.Set(Key(id), new Entry(Hash(token), session.Clone()),
            new MemoryCacheEntryOptions { SlidingExpiration = IdleTimeout });

    // A conversation holds a merchant's shipment data, so only the caller who started it may continue it: same id
    // AND same token. A wrong token gets the same answer as an unknown id, so it learns nothing.
    public JsonElement? Load(string id, string token) =>
        cache.TryGetValue(Key(id), out Entry? entry) && entry is not null &&
        CryptographicOperations.FixedTimeEquals(entry.TokenHash, Hash(token))
            ? entry.Session
            : null;

    private static string Key(string id) => $"conversation:{id}";

    // Never the token itself: only its SHA-256 hash is kept.
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private sealed record Entry(byte[] TokenHash, JsonElement Session);
}
