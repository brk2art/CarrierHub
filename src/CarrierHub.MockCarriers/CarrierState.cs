using System.Collections.Concurrent;

namespace CarrierHub.MockCarriers;

// The faults a carrier shows, switchable at runtime through /admin.
public sealed record Faults
{
    public int LatencyMs { get; init; }           // wait this long before answering
    public double ErrorRate { get; init; }        // 0..1: answer 500 this often
    public double DuplicateRate { get; init; }    // 0..1: repeat each event this often
    public bool Shuffle { get; init; }            // return events in random order
    public int RequestsPerMinute { get; init; }   // above this, answer 429 (0 = no limit)
}

public sealed record HappenedEvent(JourneyStep Step, DateTimeOffset At);

public static class Carriers
{
    public static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "fastship", "nordpost", "parcelgo", "merchant" };
}

// Everything the mock carriers remember. In memory only: a restart starts every journey again.
public sealed class CarrierState(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _journeyStarts = new();
    private readonly ConcurrentDictionary<string, Faults> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _requests = new(StringComparer.OrdinalIgnoreCase);

    // A real carrier's clock starts when the parcel is booked, whether anyone asks about it or not.
    // Load tests call this at creation. Without it, the journey starts the first time a carrier is asked,
    // which hides any wait before the first poll from the Gateway's event age.
    public DateTimeOffset StartJourney(string carrier, string trackingNumber) =>
        _journeyStarts.GetOrAdd(Key(carrier, trackingNumber), _ => clock.GetUtcNow());

    public IReadOnlyList<HappenedEvent> HappenedSoFar(string carrier, string trackingNumber)
    {
        var start = StartJourney(carrier, trackingNumber);   // started already, or starts now
        var now = clock.GetUtcNow();

        return Journeys.For(trackingNumber)
            .Select(step => new HappenedEvent(step, start + step.After))
            .Where(e => e.At <= now)
            .ToList();
    }

    public Faults FaultsFor(string carrier) => _faults.GetOrAdd(carrier, _ => new Faults());

    public IReadOnlyDictionary<string, Faults> AllFaults() => _faults.ToDictionary(p => p.Key, p => p.Value);

    public void SetFaults(string carrier, Faults faults) => _faults[carrier] = faults;

    public void Reset()
    {
        _journeyStarts.Clear();
        _faults.Clear();
        _requests.Clear();
    }

    // A one-minute sliding window per carrier.
    public bool OverRateLimit(string carrier, int perMinute)
    {
        var now = clock.GetUtcNow();
        var window = _requests.GetOrAdd(carrier, _ => new Queue<DateTimeOffset>());

        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() > TimeSpan.FromMinutes(1))
                window.Dequeue();

            if (window.Count >= perMinute)
                return true;

            window.Enqueue(now);
            return false;
        }
    }

    // "FastShip" from a test and "fastship" from a carrier endpoint are the same journey.
    private static string Key(string carrier, string trackingNumber) => $"{carrier.ToLowerInvariant()}:{trackingNumber}";
}