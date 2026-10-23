namespace CarrierHub.MockCarriers;

public static class FaultInjection
{
    // Runs before a carrier answers. Returns the failure to send instead, or null to answer normally.
    public static async Task<IResult?> BeforeAnswerAsync(string carrier, CarrierState state, HttpContext http, CancellationToken ct)
    {
        var faults = state.FaultsFor(carrier);

        if (faults.LatencyMs > 0)
            await Task.Delay(faults.LatencyMs, ct);

        if (faults.RequestsPerMinute > 0 && state.OverRateLimit(carrier, faults.RequestsPerMinute))
        {
            http.Response.Headers.RetryAfter = "60";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (Random.Shared.NextDouble() < faults.ErrorRate)
            return Results.Problem($"Simulated {carrier} failure", statusCode: StatusCodes.Status500InternalServerError);

        return null;
    }

    // Duplicates and shuffles events, the way unreliable carriers do.
    public static IReadOnlyList<T> Mangle<T>(IEnumerable<T> events, Faults faults)
    {
        var list = new List<T>();
        foreach (var e in events)
        {
            list.Add(e);
            if (Random.Shared.NextDouble() < faults.DuplicateRate)
                list.Add(e);
        }

        var result = list.ToArray();
        if (faults.Shuffle)
            Random.Shared.Shuffle(result);
        return result;
    }
}