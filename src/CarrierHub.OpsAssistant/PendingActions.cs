using Microsoft.Extensions.Logging.Abstractions;

namespace CarrierHub.OpsAssistant;

public enum ActionStatus { Pending, Approved, Rejected, Executed, Failed }

// Something the assistant prepared and a person must approve. Everything needed to carry it out is fixed here, at
// proposal time: approving runs exactly what the person saw, never a fresh interpretation by the model.
public sealed record PendingAction(
    Guid Id,
    string Kind,
    string MerchantId,
    string ConversationId,
    Guid ShipmentId,
    string MerchantReference,
    string Carrier,
    string Reason,
    string? Note,
    string IdempotencyKey,
    ActionStatus Status,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    Guid? ClaimId = null,
    string? Error = null)
{
    public const string OpenClaim = "open_claim";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    // What the person reads before approving.
    public string Summary =>
        $"Open a {Reason} claim with {Carrier} for {MerchantReference}" + (Note is null ? "." : $": \"{Note}\"");

    public bool IsOpen(DateTimeOffset now) => Status == ActionStatus.Pending && now < ExpiresAt;

    // Waiting for a decision, or approved and not finished: either way, the same claim shouldn't be proposed again.
    public bool IsActive(DateTimeOffset now) => IsOpen(now) || Status == ActionStatus.Approved;

    // A new proposal. Both stores create it here, so they can't differ.
    public static PendingAction NewClaim(string merchantId, string conversationId, Guid shipmentId,
        string merchantReference, string carrier, string reason, string? note, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7(now);
        return new PendingAction(id, OpenClaim, merchantId, conversationId, shipmentId, merchantReference, carrier,
            reason, note,
            IdempotencyKey: $"claim-{id:N}",   // fixed now, sent unchanged on every attempt to carry it out
            ActionStatus.Pending, now, now + Lifetime);
    }
}

// One line of the audit trail: what happened to an action, when, and who did it.
public sealed record AuditEntry(DateTimeOffset At, Guid ActionId, string MerchantId, string Event, string Actor,
    string? Detail);

// Where pending actions and their audit trail live. The service uses PostgreSQL; the evaluations use memory, so
// they need no database. Every change is saved together with its audit line.
public interface IPendingActionStore
{
    // Proposing the same claim again (same merchant, shipment and reason) while one is still active returns that one.
    Task<PendingAction> ProposeClaimAsync(string merchantId, string conversationId, Guid shipmentId,
        string merchantReference, string carrier, string reason, string? note, CancellationToken ct);

    // Another merchant's action is simply "not found", as everywhere else.
    Task<PendingAction?> FindAsync(Guid id, string merchantId, CancellationToken ct);

    Task<IReadOnlyList<PendingAction>> ListAsync(string merchantId, ActionStatus? status, CancellationToken ct);

    Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(Guid actionId, CancellationToken ct);

    // Changes an action only if it is still in one of the expected states, and audits the change with it.
    // Null: the action doesn't exist for this merchant, or someone changed it a moment ago.
    Task<PendingAction?> TryChangeAsync(Guid id, string merchantId, ActionStatus[] from,
        Func<PendingAction, PendingAction> change, string auditEvent, string actor, string? detail, CancellationToken ct);

    // An event that changes no state: a refused approval, an execution that got no answer.
    Task RecordAsync(PendingAction action, string auditEvent, string actor, string? detail, CancellationToken ct);
}

// The same audit line in the logs (and so in Aspire and, later, Application Insights), searchable by action id.
internal static class AuditLog
{
    public static void Write(ILogger logger, AuditEntry e) =>
        logger.LogInformation("Audit: action {ActionId} {AuditEvent} by {Actor} for {MerchantId}: {Detail}",
            e.ActionId, e.Event, e.Actor, e.MerchantId, e.Detail);
}

// For the evaluations (and tests): no database, lost on restart. A lock makes each change and its audit line one step.
public sealed class InMemoryPendingActionStore(TimeProvider clock, ILogger<InMemoryPendingActionStore>? logger = null)
    : IPendingActionStore
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Dictionary<Guid, PendingAction> _actions = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly Lock _gate = new();

    public Task<PendingAction> ProposeClaimAsync(string merchantId, string conversationId, Guid shipmentId,
        string merchantReference, string carrier, string reason, string? note, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            var active = _actions.Values.FirstOrDefault(a => a.Kind == PendingAction.OpenClaim
                && a.MerchantId == merchantId && a.ShipmentId == shipmentId && a.Reason == reason && a.IsActive(now));
            if (active is not null)
                return Task.FromResult(active);

            var action = PendingAction.NewClaim(merchantId, conversationId, shipmentId, merchantReference, carrier,
                reason, note, now);
            _actions[action.Id] = action;
            Audit(action, "Proposed", "assistant", $"conversation {conversationId}");
            return Task.FromResult(action);
        }
    }

    public Task<PendingAction?> FindAsync(Guid id, string merchantId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_actions.TryGetValue(id, out var a) && a.MerchantId == merchantId ? a : null);
    }

    public Task<IReadOnlyList<PendingAction>> ListAsync(string merchantId, ActionStatus? status, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PendingAction>>(_actions.Values
                .Where(a => a.MerchantId == merchantId && (status is null || a.Status == status))
                .OrderByDescending(a => a.ProposedAt)
                .Take(50)
                .ToList());
    }

    public Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(Guid actionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<AuditEntry>>(_audit.Where(e => e.ActionId == actionId).ToList());
    }

    public Task<PendingAction?> TryChangeAsync(Guid id, string merchantId, ActionStatus[] from,
        Func<PendingAction, PendingAction> change, string auditEvent, string actor, string? detail, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_actions.TryGetValue(id, out var current) || current.MerchantId != merchantId
                || !from.Contains(current.Status))
                return Task.FromResult<PendingAction?>(null);

            var changed = change(current);
            _actions[id] = changed;
            Audit(changed, auditEvent, actor, detail);
            return Task.FromResult<PendingAction?>(changed);
        }
    }

    public Task RecordAsync(PendingAction action, string auditEvent, string actor, string? detail, CancellationToken ct)
    {
        lock (_gate)
            Audit(action, auditEvent, actor, detail);
        return Task.CompletedTask;
    }

    private void Audit(PendingAction action, string auditEvent, string actor, string? detail)
    {
        var entry = new AuditEntry(clock.GetUtcNow(), action.Id, action.MerchantId, auditEvent, actor, detail);
        _audit.Add(entry);
        AuditLog.Write(_logger, entry);
    }
}