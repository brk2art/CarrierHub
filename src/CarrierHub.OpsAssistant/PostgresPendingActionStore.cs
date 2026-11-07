using CarrierHub.OpsAssistant.Data;
using Microsoft.EntityFrameworkCore;

namespace CarrierHub.OpsAssistant;

// The service's store: proposals and their audit trail survive restarts and crashes. Each change and its audit line
// go into one SaveChanges, which is one transaction: there's never a change without its line, or a line without it.
public sealed class PostgresPendingActionStore(AssistantDbContext db, TimeProvider clock,
    ILogger<PostgresPendingActionStore> logger) : IPendingActionStore
{
    public async Task<PendingAction> ProposeClaimAsync(string merchantId, string conversationId, Guid shipmentId,
        string merchantReference, string carrier, string reason, string? note, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // The same rule as PendingAction.IsActive, written so the database can run it.
        var active = await db.PendingActions.AsNoTracking()
            .Where(a => a.Kind == PendingAction.OpenClaim && a.MerchantId == merchantId
                && a.ShipmentId == shipmentId && a.Reason == reason
                && (a.Status == ActionStatus.Approved || (a.Status == ActionStatus.Pending && a.ExpiresAt > now)))
            .OrderByDescending(a => a.ProposedAt)
            .FirstOrDefaultAsync(ct);
        if (active is not null)
            return active;

        // Two proposals at the very same moment could both be stored. Harmless: Tracking's unique (shipment, reason)
        // index lets only one of them become a claim; the other fails visibly on approval.
        var action = PendingAction.NewClaim(merchantId, conversationId, shipmentId, merchantReference, carrier,
            reason, note, now);
        var entry = new AuditEntry(now, action.Id, action.MerchantId, "Proposed", "assistant",
            $"conversation {conversationId}");

        db.PendingActions.Add(action);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        AuditLog.Write(logger, entry);
        return action;
    }

    public Task<PendingAction?> FindAsync(Guid id, string merchantId, CancellationToken ct) =>
        db.PendingActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.MerchantId == merchantId, ct);

    public async Task<IReadOnlyList<PendingAction>> ListAsync(string merchantId, ActionStatus? status,
        CancellationToken ct)
    {
        var query = db.PendingActions.AsNoTracking().Where(a => a.MerchantId == merchantId);
        if (status is { } wanted)
            query = query.Where(a => a.Status == wanted);

        return await query.OrderByDescending(a => a.ProposedAt).Take(50).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(Guid actionId, CancellationToken ct) =>
        await db.AuditEntries.AsNoTracking()
            .Where(e => e.ActionId == actionId)
            .OrderBy(e => EF.Property<long>(e, "Id"))   // the order the lines were written in
            .ToListAsync(ct);

    public async Task<PendingAction?> TryChangeAsync(Guid id, string merchantId, ActionStatus[] from,
        Func<PendingAction, PendingAction> change, string auditEvent, string actor, string? detail, CancellationToken ct)
    {
        var current = await db.PendingActions.FirstOrDefaultAsync(a => a.Id == id && a.MerchantId == merchantId, ct);
        if (current is null || !from.Contains(current.Status))
        {
            db.ChangeTracker.Clear();
            return null;
        }

        var changed = change(current);
        var entry = new AuditEntry(clock.GetUtcNow(), id, merchantId, auditEvent, actor, detail);

        db.Entry(current).CurrentValues.SetValues(changed);
        db.AuditEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);   // fails if xmin changed since the read above: someone else got there first
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        AuditLog.Write(logger, entry);
        return changed;
    }

    public async Task RecordAsync(PendingAction action, string auditEvent, string actor, string? detail,
        CancellationToken ct)
    {
        var entry = new AuditEntry(clock.GetUtcNow(), action.Id, action.MerchantId, auditEvent, actor, detail);
        db.AuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        AuditLog.Write(logger, entry);
    }
}