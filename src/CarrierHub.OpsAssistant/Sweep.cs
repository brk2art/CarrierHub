using System.Collections.Concurrent;
using System.Text.Json;
using CarrierHub.AssistantTools;

namespace CarrierHub.OpsAssistant;

// The sweep: finds late shipments, checks each one, and PREPARES a claim where compensation is due.
// Code is the skeleton (which shipments, how many at once, what to do with each result); the model is only at the
// leaves (reading one shipment's timeline and its carrier's terms). It never opens anything: a person approves.
public sealed class SweepOptions
{
    public string? Token { get; set; }            // the sweep's own credential; read-only: it can propose, never approve
    public int MaxParallel { get; set; } = 3;     // shipments checked at the same time
    public int MaxShipments { get; set; } = 50;   // per sweep: a hard cap on cost
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
    public bool Scheduled { get; set; }           // false: only POST /v1/assistant/sweeps runs it (Development)
}

// Outcome: Proposed, Skipped (not due), NotProposed (the proposal rules refused: say why) or Failed.
public sealed record SweepItem(string OrderNumber, string Carrier, string Outcome, string Detail, Guid? ActionId = null);

public sealed record SweepReport(Guid Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int Found,
    int Proposed, int Skipped, int Unclear, int Failed, long InputTokens, IReadOnlyList<SweepItem> Items);

public sealed class SweepRunner(IServiceScopeFactory scopes, SweepOptions options, TimeProvider clock,
    ILogger<SweepRunner> logger)
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public SweepReport? Last { get; private set; }

    // Null: a sweep is already running. Two at once would check (and pay for) everything twice.
    public async Task<SweepReport?> RunAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct))
            return null;
        try
        {
            return Last = await SweepAsync(ct);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<SweepReport> SweepAsync(CancellationToken ct)
    {
        var token = options.Token ?? throw new InvalidOperationException("Set Assistant:Sweep:Token.");
        var id = Guid.CreateVersion7();
        var started = clock.GetUtcNow();

        // 1. Which shipments: code decides, not the model. Delivered and late, at most MaxShipments.
        List<Shipment> late;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PublicApiClient>();
            var found = await api.SearchAsync(token, "Delivered", true, null, options.MaxShipments, ct);
            if (found.StatusCode != 200)
                throw new InvalidOperationException($"The Public API refused the sweep's token: {found.StatusCode}.");
            late = found.Value ?? [];
        }

        // 2. Fan-out, at most MaxParallel at once. Each shipment gets its OWN scope: a DbContext must never be
        //    shared between parallel branches. One shipment failing never stops the others.
        var items = new ConcurrentBag<SweepItem>();
        long tokens = 0;
        await Parallel.ForEachAsync(late,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = ct },
            async (shipment, token2) =>
            {
                var (item, used) = await CheckAsync(id, token, shipment, token2);
                items.Add(item);
                Interlocked.Add(ref tokens, used);
            });

        var list = items.OrderBy(i => i.OrderNumber).ToList();
        var report = new SweepReport(id, started, clock.GetUtcNow(), late.Count,
            Proposed: list.Count(i => i.Outcome == "Proposed"),
            Skipped: list.Count(i => i.Outcome is "Skipped" or "NotProposed"),
            Unclear: list.Count(i => i.Outcome == "Unclear"),
            Failed: list.Count(i => i.Outcome == "Failed"),
            tokens, list);

        logger.LogInformation("Sweep {Id}: {Found} late, {Proposed} proposed, {Skipped} skipped, {Failed} failed, {Tokens} tokens in",
            id, report.Found, report.Proposed, report.Skipped, report.Failed, report.InputTokens);
        return report;
    }

    private async Task<(SweepItem Item, long Tokens)> CheckAsync(Guid sweepId, string token, Shipment s, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var assistant = scope.ServiceProvider.GetRequiredService<Assistant>();

            // 3. The model at the leaf: read the timeline and the terms, fill in the facts. C# does the arithmetic.
            var result = await assistant.CheckLatenessAsync($"Shipment {s.MerchantReference}.", token, ct);
            var used = result.InputTokens ?? 0;
            if (result.Verdict is not { } verdict)
                return (new(s.MerchantReference, s.Carrier, "Failed", $"The Public API refused: {result.RejectedWith}."), used);

            // 4. Code decides. Unknown is NOT "no": it goes to a person instead of being skipped.
            if (verdict.CompensationDue is null)
                return (new(s.MerchantReference, s.Carrier, "Unclear", Why(verdict)), used);
            if (verdict.CompensationDue == false)
                return (new(s.MerchantReference, s.Carrier, "Skipped", Why(verdict)), used);

            // 5. Propose through the SAME path as the chat: its rules, its duplicate check, a person approves.
            var api = scope.ServiceProvider.GetRequiredService<PublicApiClient>();
            var store = scope.ServiceProvider.GetRequiredService<IPendingActionStore>();
            var actions = new ActionTools(api, token, $"sweep-{sweepId:N}", store);
            var answer = await actions.ProposeOpenClaim(s.MerchantReference, "Late", null, ct);

            return actions.Proposed.FirstOrDefault() is { } action
                ? (new(s.MerchantReference, s.Carrier, "Proposed", Why(verdict), action.Id), used)
                : (new(s.MerchantReference, s.Carrier, "NotProposed", JsonSerializer.Serialize(answer)), used);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sweep: checking {Order} failed", s.MerchantReference);
            return (new(s.MerchantReference, s.Carrier, "Failed", ex.Message), 0);
        }
    }

    private static string Why(LatenessVerdict v) => v switch
    {
        { HoursLate: { } hours } =>
            $"{hours} h late; {v.Facts.Carrier} pays after {v.Facts.CompensationAfterHours} h.",
        { CompensationDue: false } =>
            $"Not due even against the original promise; {v.Facts.Carrier} pays after {v.Facts.CompensationAfterHours} h.",
        _ => $"Needs a person: {v.Facts.Missing ?? "the promised or delivery time is unknown"}",
    };
}

// The schedule. Off unless Assistant:Sweep:Scheduled is true and a token is set.
public sealed class SweepService(SweepRunner runner, SweepOptions options, ILogger<SweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Scheduled || options.Token is null)
            return;

        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await runner.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The sweep failed; trying again at the next tick");
            }
        }
    }
}

public static class SweepEndpoints
{
    // Development only: run a sweep now, with the sweep's own token. In production only the schedule runs it.
    public static void MapSweeps(this WebApplication app)
    {
        app.MapPost("/v1/assistant/sweeps", async (SweepRunner runner, CancellationToken ct) =>
            await runner.RunAsync(ct) is { } report
                ? Results.Ok(report)
                : Results.Conflict(new { error = "A sweep is already running." }));

        app.MapGet("/v1/assistant/sweeps/last", (SweepRunner runner) =>
            runner.Last is { } report ? Results.Ok(report) : Results.NotFound());
    }
}