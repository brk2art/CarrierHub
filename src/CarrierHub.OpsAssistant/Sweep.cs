using CarrierHub.AssistantTools;
using CarrierHub.OpsAssistant.Data;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CarrierHub.OpsAssistant;

// The sweep: finds late shipments, checks each one, and PREPARES a claim where compensation is due.
// Code is the skeleton (which shipments, how many at once, what to do with each result); the model is only at the
// leaves (reading one shipment's timeline and its carrier's terms). It never opens anything: a person approves.
public sealed class SweepOptions
{
    public string? Token { get; set; }            // the sweep's own credential; read-only: it can propose, never approve
    public int MaxParallel { get; set; } = 3;     // shipments checked at the same time
    public int MaxShipments { get; set; } = 50;   // checked per sweep: a hard cap on cost
    public int ListLimit { get; set; } = 100;     // late shipments listed per sweep (cheap: no model)
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
    public bool Scheduled { get; set; }           // false: only POST /v1/assistant/sweeps runs it (Development)
}

// Outcome: Proposed, Skipped (not due), NotProposed (the proposal rules refused: say why), Unclear (a person must
// look) or Failed (tried again next time).
public sealed record SweepItem(string OrderNumber, string Carrier, string Outcome, string Detail, Guid? ActionId = null);

public sealed record SweepReport(Guid Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int Found,
    int AlreadyDecided, int Checked, int Proposed, int Skipped, int Unclear, int Failed, long InputTokens,
    IReadOnlyList<SweepItem> Items);

// One row per shipment the sweep has decided about. A delivered parcel's lateness can't change, so a decision is
// final: later sweeps skip it, and pay nothing for it. Failed checks are NOT stored, so the next sweep tries again.
public sealed class SweepDecision
{
    public required string MerchantId { get; init; }
    public required Guid ShipmentId { get; init; }
    public required string OrderNumber { get; init; }
    public required string Outcome { get; init; }
    public required string Detail { get; init; }
    public Guid? ActionId { get; init; }
    public required Guid SweepId { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

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

        // 1. Candidates, minus what is already decided: the Public API and the database, no model.
        string merchant;
        List<Shipment> found, todo;
        int alreadyDecided;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<PublicApiClient>();
            var me = await api.MeAsync(token, ct);
            if (me.StatusCode != 200 || me.Value is null)
                throw new InvalidOperationException($"The Public API refused the sweep's token: {me.StatusCode}.");
            merchant = me.Value.MerchantId;

            var late = await api.SearchAsync(token, "Delivered", true, null, options.ListLimit, ct);
            if (late.StatusCode != 200)
                throw new InvalidOperationException($"The Public API refused the sweep's token: {late.StatusCode}.");
            found = late.Value ?? [];

            var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
            var ids = found.Select(s => s.Id).ToList();
            var decided = (await db.SweepDecisions
                    .Where(d => d.MerchantId == merchant && ids.Contains(d.ShipmentId))
                    .Select(d => d.ShipmentId)
                    .ToListAsync(ct))
                .ToHashSet();

            alreadyDecided = decided.Count;
            todo = found.Where(s => !decided.Contains(s.Id)).Take(options.MaxShipments).ToList();
        }

        // 2. Fan-out, at most MaxParallel at once. Each shipment gets its OWN scope: a DbContext must never be
        //    shared between parallel branches. One shipment failing never stops the others.
        var items = new ConcurrentBag<SweepItem>();
        long tokens = 0;
        await Parallel.ForEachAsync(todo,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallel, CancellationToken = ct },
            async (shipment, branchCt) =>
            {
                var (item, used) = await CheckAsync(id, merchant, token, shipment, branchCt);
                items.Add(item);
                Interlocked.Add(ref tokens, used);
            });

        var list = items.OrderBy(i => i.OrderNumber).ToList();
        var report = new SweepReport(id, started, clock.GetUtcNow(),
            Found: found.Count,
            AlreadyDecided: alreadyDecided,
            Checked: todo.Count,
            Proposed: list.Count(i => i.Outcome == "Proposed"),
            Skipped: list.Count(i => i.Outcome is "Skipped" or "NotProposed"),
            Unclear: list.Count(i => i.Outcome == "Unclear"),
            Failed: list.Count(i => i.Outcome == "Failed"),
            tokens, list);

        logger.LogInformation(
            "Sweep {Id}: {Found} late, {AlreadyDecided} already decided, {Checked} checked: {Proposed} proposed, " +
            "{Skipped} skipped, {Unclear} unclear, {Failed} failed, {Tokens} tokens in",
            id, report.Found, report.AlreadyDecided, report.Checked, report.Proposed, report.Skipped, report.Unclear,
            report.Failed, report.InputTokens);
        return report;
    }

    // One branch: decide, then remember the decision at once. A sweep that crashes halfway loses only what was in
    // flight. If it crashes between proposing and remembering, the next sweep proposes again and gets the SAME
    // pending action back: the proposal path is idempotent.
    private async Task<(SweepItem Item, long Tokens)> CheckAsync(Guid sweepId, string merchant, string token,
        Shipment s, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        SweepItem item;
        long used;
        try
        {
            (item, used) = await DecideAsync(scope.ServiceProvider, sweepId, token, s, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sweep: checking {Order} failed; the next sweep tries again", s.MerchantReference);
            return (new(s.MerchantReference, s.Carrier, "Failed", ex.Message), 0);
        }

        await RememberAsync(scope.ServiceProvider, merchant, sweepId, s, item, ct);
        return (item, used);
    }

    private async Task<(SweepItem Item, long Tokens)> DecideAsync(IServiceProvider services, Guid sweepId,
        string token, Shipment s, CancellationToken ct)
    {
        var assistant = services.GetRequiredService<Assistant>();

        // 3. The model at the leaf: read the timeline and the terms, fill in the facts. C# does the arithmetic.
        var result = await assistant.CheckLatenessAsync($"Shipment {s.MerchantReference}.", token, ct);
        var used = result.InputTokens ?? 0;
        if (result.Verdict is not { } verdict)
            throw new InvalidOperationException($"The Public API refused: {result.RejectedWith}.");

        // 4. Code decides. Unknown is NOT "no": it goes to a person instead of being skipped.
        if (verdict.CompensationDue is null)
            return (new(s.MerchantReference, s.Carrier, "Unclear", Why(verdict)), used);
        if (verdict.CompensationDue == false)
            return (new(s.MerchantReference, s.Carrier, "Skipped", Why(verdict)), used);

        // 5. Propose through the SAME path as the chat: its rules, its duplicate check, a person approves.
        var api = services.GetRequiredService<PublicApiClient>();
        var store = services.GetRequiredService<IPendingActionStore>();
        var actions = new ActionTools(api, token, $"sweep-{sweepId:N}", store);
        var answer = await actions.ProposeOpenClaim(s.MerchantReference, "Late", null, ct);

        return actions.Proposed.FirstOrDefault() is { } action
            ? (new(s.MerchantReference, s.Carrier, "Proposed", Why(verdict), action.Id), used)
            : (new(s.MerchantReference, s.Carrier, "NotProposed", JsonSerializer.Serialize(answer)), used);
    }

    // 6. A final decision becomes a row. Failed is not final, so it isn't stored.
    private async Task RememberAsync(IServiceProvider services, string merchant, Guid sweepId, Shipment s,
        SweepItem item, CancellationToken ct)
    {
        if (item.Outcome == "Failed")
            return;

        var db = services.GetRequiredService<AssistantDbContext>();
        db.SweepDecisions.Add(new SweepDecision
        {
            MerchantId = merchant,
            ShipmentId = s.Id,
            OrderNumber = s.MerchantReference,
            Outcome = item.Outcome,
            Detail = item.Detail.Length > 1000 ? item.Detail[..1000] : item.Detail,
            ActionId = item.ActionId,
            SweepId = sweepId,
            DecidedAt = clock.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The primary key (merchant, shipment) refused it: another sweep decided this shipment at the same time.
            logger.LogInformation("Sweep: {Order} was already decided by another sweep", s.MerchantReference);
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