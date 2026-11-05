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
    DateTimeOffset ExpiresAt)
{
    public const string OpenClaim = "open_claim";

    // What the person reads before approving.
    public string Summary =>
        $"Open a {Reason} claim with {Carrier} for {MerchantReference}" + (Note is null ? "." : $": \"{Note}\"");

    public bool IsOpen(DateTimeOffset now) => Status == ActionStatus.Pending && now < ExpiresAt;
}

// In memory for now, like conversations: a restart loses pending actions. Step 4 moves them to PostgreSQL.
public sealed class PendingActionStore(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly Dictionary<Guid, PendingAction> _actions = [];
    private readonly Lock _gate = new();

    // Proposing the same claim again (same merchant, shipment and reason) while one is still pending returns that
    // one: asking twice, or in two conversations, gives one thing to approve, not two.
    public PendingAction ProposeClaim(string merchantId, string conversationId, Guid shipmentId,
        string merchantReference, string carrier, string reason, string? note)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            var pending = _actions.Values.FirstOrDefault(a => a.Kind == PendingAction.OpenClaim
                && a.MerchantId == merchantId && a.ShipmentId == shipmentId && a.Reason == reason && a.IsOpen(now));
            if (pending is not null)
                return pending;

            var id = Guid.CreateVersion7(now);
            var action = new PendingAction(id, PendingAction.OpenClaim, merchantId, conversationId, shipmentId,
                merchantReference, carrier, reason, note,
                IdempotencyKey: $"claim-{id:N}",   // fixed now, sent unchanged on every attempt to carry it out
                ActionStatus.Pending, now, now + Lifetime);

            _actions[id] = action;
            return action;
        }
    }

    // Another merchant's action is simply "not found", as everywhere else.
    public PendingAction? Find(Guid id, string merchantId)
    {
        lock (_gate)
            return _actions.TryGetValue(id, out var action) && action.MerchantId == merchantId ? action : null;
    }
}