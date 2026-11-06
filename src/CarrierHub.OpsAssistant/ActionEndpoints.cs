using CarrierHub.AssistantTools;

namespace CarrierHub.OpsAssistant;

public sealed record AuditView(DateTimeOffset At, string Event, string Actor, string? Detail);

public sealed record ActionView(Guid Id, string Kind, string Summary, string Status, DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt, string? DecidedBy, DateTimeOffset? DecidedAt, Guid? ClaimId, string? Error,
    IReadOnlyList<AuditView> Audit);

// Where a person sees, approves or rejects what the assistant prepared. These are HTTP endpoints, not tools:
// the model has no way to call them, whatever it writes.
public static class ActionEndpoints
{
    // The scope the Public API requires for POST /v1/claims. Checked here too, so an approval without it is refused
    // before anything changes, instead of being recorded as approved and then failing.
    public const string WriteScope = "shipments:write";

    public static void MapActions(this WebApplication app)
    {
        var actions = app.MapGroup("/v1/assistant/actions");
        actions.MapGet("/", ListAsync);
        actions.MapGet("/{id:guid}", GetAsync);
        actions.MapPost("/{id:guid}/approve", ApproveAsync);
        actions.MapPost("/{id:guid}/reject", RejectAsync);
    }

    private sealed record Caller(string Token, string MerchantId, string Actor, IReadOnlyList<string> Scopes);

    private static async Task<IResult> ListAsync(string? status, HttpRequest http, PublicApiClient api,
        PendingActionStore store, CancellationToken ct)
    {
        var (caller, failure) = await IdentifyAsync(http, api, ct);
        if (caller is null)
            return failure!;

        ActionStatus? wanted = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ActionStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.BadRequest(new { error = $"status must be one of: {string.Join(", ", Enum.GetNames<ActionStatus>())}." });
            wanted = parsed;
        }

        return Results.Ok(store.List(caller.MerchantId, wanted).Select(a => View(a, store)));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpRequest http, PublicApiClient api,
        PendingActionStore store, CancellationToken ct)
    {
        var (caller, failure) = await IdentifyAsync(http, api, ct);
        if (caller is null)
            return failure!;

        return store.Find(id, caller.MerchantId) is { } action
            ? Results.Ok(View(action, store))
            : Results.NotFound(new { error = "Unknown action." });
    }

    private static async Task<IResult> ApproveAsync(Guid id, HttpRequest http, PublicApiClient api,
        PendingActionStore store, ClaimExecutor executor, TimeProvider clock, CancellationToken ct)
    {
        var (caller, failure) = await IdentifyAsync(http, api, ct);
        if (caller is null)
            return failure!;

        var action = store.Find(id, caller.MerchantId);
        if (action is null)
            return Results.NotFound(new { error = "Unknown action." });

        switch (action.Status)
        {
            case ActionStatus.Executed:
                return Results.Ok(View(action, store));   // approving again is harmless: the same result, nothing new
            case ActionStatus.Rejected:
                return Refuse(action, store, "This action was rejected.");
            case ActionStatus.Failed:
                return Refuse(action, store, $"This action failed: {action.Error}");
        }

        if (!caller.Scopes.Contains(WriteScope))
        {
            store.Record(action, "ApprovalRefused", caller.Actor, $"the token has no {WriteScope} scope");
            return Results.Json(new { error = $"Approving needs a token with the {WriteScope} scope." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var now = clock.GetUtcNow();
        if (action.Status == ActionStatus.Pending && now >= action.ExpiresAt)
        {
            store.Record(action, "ApprovalRefused", caller.Actor, "expired");
            return Refuse(action, store, "This action expired. Ask the assistant to prepare it again.");
        }

        // 1. Record the approval BEFORE executing. If the process dies during the call below, the action is visibly
        //    "Approved, not executed", and approving again finishes it with the same key.
        var again = action.Status == ActionStatus.Approved;
        var approved = store.TryChange(id, caller.MerchantId, [ActionStatus.Pending, ActionStatus.Approved],
            a => a with { Status = ActionStatus.Approved, DecidedBy = a.DecidedBy ?? caller.Actor, DecidedAt = a.DecidedAt ?? now },
            again ? "ApprovedAgain" : "Approved", caller.Actor);
        if (approved is null)
            return Current(id, caller, store, okIf: ActionStatus.Executed);   // someone decided a moment ago

        // 2. Execute: the approver's token, the stored request, the stored key. Not the request's token: once a person
        //    has approved, a caller who hangs up (a closed tab, a client timeout) must not cancel the write halfway.
        //    The HttpClient's own timeouts (from ServiceDefaults) still bound how long this can take.
        var outcome = await executor.OpenClaimAsync(approved, caller.Token, CancellationToken.None);

        switch (outcome.Kind)
        {
            case ExecutionKind.Opened:
                var executed = store.TryChange(id, caller.MerchantId, [ActionStatus.Approved],
                    a => a with { Status = ActionStatus.Executed, ClaimId = outcome.ClaimId, Error = null },
                    "Executed", "carrierhub",
                    outcome.Replayed ? $"claim {outcome.ClaimId} (already opened with this key)" : $"claim {outcome.ClaimId} opened");
                return executed is null
                    ? Current(id, caller, store, okIf: ActionStatus.Executed)
                    : Results.Ok(View(executed, store));

            case ExecutionKind.Refused:
                var failed = store.TryChange(id, caller.MerchantId, [ActionStatus.Approved],
                    a => a with { Status = ActionStatus.Failed, Error = outcome.Error },
                    "Failed", "carrierhub", outcome.Error);
                return failed is null
                    ? Current(id, caller, store, okIf: ActionStatus.Executed)
                    : Refuse(failed, store, $"CarrierHub refused the claim: {outcome.Error}");

            case ExecutionKind.NotAllowed:
                store.Record(approved, "ExecutionNotAllowed", caller.Actor, outcome.Error);
                return Results.Json(new { error = outcome.Error, action = View(approved, store) },
                    statusCode: outcome.StatusCode ?? StatusCodes.Status403Forbidden);

            default:   // Unknown: the action stays Approved
                store.Record(approved, "ExecutionUnknown", "carrierhub", outcome.Error);
                return Results.Json(new
                {
                    error = "CarrierHub didn't confirm the claim. Approve again: the same idempotency key makes sure " +
                            "at most one claim is opened.",
                    action = View(approved, store),
                }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // Rejecting needs no write scope: saying no is always safe. Least privilege is about doing, not refusing.
    private static async Task<IResult> RejectAsync(Guid id, HttpRequest http, PublicApiClient api,
        PendingActionStore store, TimeProvider clock, CancellationToken ct)
    {
        var (caller, failure) = await IdentifyAsync(http, api, ct);
        if (caller is null)
            return failure!;

        var rejected = store.TryChange(id, caller.MerchantId, [ActionStatus.Pending],
            a => a with { Status = ActionStatus.Rejected, DecidedBy = caller.Actor, DecidedAt = clock.GetUtcNow() },
            "Rejected", caller.Actor);

        return rejected is not null
            ? Results.Ok(View(rejected, store))
            : Current(id, caller, store, okIf: ActionStatus.Rejected);
    }

    // Who is calling, as the Public API validated the token: never decoded here, never taken from the request body.
    private static async Task<(Caller? Caller, IResult? Failure)> IdentifyAsync(HttpRequest http, PublicApiClient api,
        CancellationToken ct)
    {
        var authorization = http.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Unauthorized());
        var token = authorization["Bearer ".Length..].Trim();

        var me = await api.MeAsync(token, ct);
        if (me.StatusCode is 401 or 403)
            return (null, Results.StatusCode(me.StatusCode));
        if (me.StatusCode != 200 || me.Value is null)
            return (null, Results.Problem("Could not confirm who is calling. Try again shortly.",
                statusCode: StatusCodes.Status502BadGateway));

        var actor = me.Value.Subject ?? $"{me.Value.MerchantId} token";
        return (new Caller(token, me.Value.MerchantId, actor, me.Value.Scopes), null);
    }

    // The state after someone else's change: fine if it's the state this call wanted, a conflict otherwise.
    private static IResult Current(Guid id, Caller caller, PendingActionStore store, ActionStatus okIf)
    {
        var action = store.Find(id, caller.MerchantId);
        if (action is null)
            return Results.NotFound(new { error = "Unknown action." });

        return action.Status == okIf
            ? Results.Ok(View(action, store))
            : Refuse(action, store, $"The action is already {action.Status}.");
    }

    private static IResult Refuse(PendingAction action, PendingActionStore store, string error) =>
        Results.Conflict(new { error, action = View(action, store) });

    private static ActionView View(PendingAction a, PendingActionStore store) => new(
        a.Id, a.Kind, a.Summary, a.Status.ToString(), a.ProposedAt, a.ExpiresAt, a.DecidedBy, a.DecidedAt,
        a.ClaimId, a.Error,
        store.AuditTrail(a.Id).Select(e => new AuditView(e.At, e.Event, e.Actor, e.Detail)).ToList());
}