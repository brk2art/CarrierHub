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

    // What the person reads before approving.
    public string Summary =>
        $"Open a {Reason} claim with {Carrier} for {MerchantReference}" + (Note is null ? "." : $": \"{Note}\"");

    public bool IsOpen(DateTimeOffset now) => Status == ActionStatus.Pending && now < ExpiresAt;

    // Waiting for a decision, or approved and not finished: either way, the same claim shouldn't be proposed again.
    public bool IsActive(DateTimeOffset now) => IsOpen(now) || Status == ActionStatus.Approved;
}

// One line of the audit trail: what happened to an action, when, and who did it.
public sealed record AuditEntry(DateTimeOffset At, Guid ActionId, string MerchantId, string Event, string Actor,
    string? Detail);

// In memory for now, like conversations: a restart loses actions and their audit trail. Step 4 moves both to
// PostgreSQL. Every change and its audit line happen in one locked step, so the trail can't miss a change.
public sealed class PendingActionStore(TimeProvider clock, ILogger<PendingActionStore>? logger = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Dictionary<Guid, PendingAction> _actions = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly Lock _gate = new();

    // Proposing the same claim again (same merchant, shipment and reason) while one is still active returns that
    // one: asking twice, or in two conversations, gives one thing to approve, not two.
    public PendingAction ProposeClaim(string merchantId, string conversationId, Guid shipmentId,
        string merchantReference, string carrier, string reason, string? note)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            var active = _actions.Values.FirstOrDefault(a => a.Kind == PendingAction.OpenClaim
                && a.MerchantId == merchantId && a.ShipmentId == shipmentId && a.Reason == reason && a.IsActive(now));
            if (active is not null)
                return active;

            var id = Guid.CreateVersion7(now);
            var action = new PendingAction(id, PendingAction.OpenClaim, merchantId, conversationId, shipmentId,
                merchantReference, carrier, reason, note,
                IdempotencyKey: $"claim-{id:N}",   // fixed now, sent unchanged on every attempt to carry it out
                ActionStatus.Pending, now, now + Lifetime);

            _actions[id] = action;
            Audit(action, "Proposed", "assistant", $"conversation {conversationId}");
            return action;
        }
    }

    // Another merchant's action is simply "not found", as everywhere else.
    public PendingAction? Find(Guid id, string merchantId)
    {
        lock (_gate)
            return _actions.TryGetValue(id, out var action) && action.MerchantId == merchantId ? action : null;
    }

    public IReadOnlyList<PendingAction> List(string merchantId, ActionStatus? status)
    {
        lock (_gate)
            return _actions.Values
                .Where(a => a.MerchantId == merchantId && (status is null || a.Status == status))
                .OrderByDescending(a => a.ProposedAt)
                .Take(50)
                .ToList();
    }

    public IReadOnlyList<AuditEntry> AuditTrail(Guid actionId)
    {
        lock (_gate)
            return _audit.Where(e => e.ActionId == actionId).ToList();
    }

    // Changes an action only if it is still in one of the expected states, and audits the change in the same step.
    // Null: the action doesn't exist for this merchant, or someone changed it a moment ago.
    public PendingAction? TryChange(Guid id, string merchantId, ActionStatus[] from,
        Func<PendingAction, PendingAction> change, string auditEvent, string actor, string? detail = null)
    {
        lock (_gate)
        {
            if (!_actions.TryGetValue(id, out var current) || current.MerchantId != merchantId
                || !from.Contains(current.Status))
                return null;

            var changed = change(current);
            _actions[id] = changed;
            Audit(changed, auditEvent, actor, detail);
            return changed;
        }
    }

    // An event that changes no state: a refused approval, an execution that got no answer.
    public void Record(PendingAction action, string auditEvent, string actor, string? detail = null)
    {
        lock (_gate)
            Audit(action, auditEvent, actor, detail);
    }

    private void Audit(PendingAction action, string auditEvent, string actor, string? detail)
    {
        _audit.Add(new AuditEntry(clock.GetUtcNow(), action.Id, action.MerchantId, auditEvent, actor, detail));

        // The same line in the logs (and so in Aspire and, later, Application Insights), searchable by action id.
        _logger.LogInformation("Audit: action {ActionId} {AuditEvent} by {Actor} for {MerchantId}: {Detail}",
            action.Id, auditEvent, actor, action.MerchantId, detail);
    }
}