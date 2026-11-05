using System.ComponentModel;
using CarrierHub.AssistantTools;

namespace CarrierHub.OpsAssistant;

// The assistant's only action tool, and it never writes: it checks the request against the merchant's own data and
// records a proposal. The claim is opened only after a person approves it (Step 3c), with that person's write token.
// One instance per question, like ShipmentTools, because it carries the caller's token.
public sealed class ActionTools(PublicApiClient api, string token, string conversationId, PendingActionStore store)
{
    private static readonly string[] Reasons = ["Late", "Lost", "Damaged"];
    private const int MaxNoteLength = 500;

    private readonly List<PendingAction> _proposed = [];
    private string? _merchantId;

    // 401 or 403 from the Public API: the caller isn't allowed; the endpoint answers with that status.
    public int? RejectedWith { get; private set; }

    // What this question proposed, for the response (and, later, an Approve button).
    public IReadOnlyList<PendingAction> Proposed => _proposed;

    [Description("Prepares a claim with the carrier for one shipment, for a person to approve. It does NOT open the " +
                 "claim. Reasons: Late (delivered after the promised time, or not delivered and the promised time " +
                 "has passed), Lost (not delivered and the promised time has passed), Damaged (delivered damaged). " +
                 "Use it only when the user asks for a claim on a specific shipment.")]
    public async Task<object> ProposeOpenClaim(
        [Description("The merchant's order number, for example ORDER-1004.")] string orderNumber,
        [Description("Late, Lost or Damaged.")] string reason,
        [Description("Optional: one short sentence for the carrier, based only on what the user said.")] string? note = null,
        CancellationToken ct = default)
    {
        var canonical = Reasons.FirstOrDefault(r => r.Equals(reason?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return Error("reason must be Late, Lost or Damaged.");

        if (string.IsNullOrWhiteSpace(orderNumber))
            return Error("An order number is required.");
        orderNumber = orderNumber.Trim();

        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > MaxNoteLength)
            return Error($"The note can be at most {MaxNoteLength} characters.");

        // 1. Who is asking: from the Public API, which validated the token. Never from the model's arguments.
        var merchantId = await MerchantIdAsync(ct);
        if (merchantId is null)
            return Error("Could not confirm who is asking. Nothing was prepared.");

        // 2. The shipment, with the caller's own token: another merchant's shipment is simply not found.
        var search = await api.SearchAsync(token, status: null, late: null, reference: orderNumber, limit: 1, ct);
        if (Refused(search.StatusCode))
            return Error("Not allowed.");
        if (search.StatusCode != 200)
            return Error("Could not look up the shipment. Nothing was prepared.");
        if (search.Value is not [var shipment, ..])
            return Error($"No shipment {orderNumber} was found.");

        // 3. The same rules Tracking enforces, checked now so the user hears "not late" before anyone approves.
        //    Tracking checks them again when the claim is opened; that check is the one that counts.
        var status = shipment.Status.ToString();
        var refusal = canonical switch
        {
            "Late" when !shipment.IsLate =>
                $"{shipment.MerchantReference} is not late, so a Late claim isn't possible.",
            "Lost" when status is "Delivered" or "Returned" || !shipment.IsLate =>
                $"{shipment.MerchantReference} can't be claimed as lost: it is {status}" +
                (shipment.IsLate ? "." : " and its promised time hasn't passed."),
            "Damaged" when status != "Delivered" =>
                $"{shipment.MerchantReference} isn't delivered yet, so a Damaged claim isn't possible.",
            _ => null,
        };
        if (refusal is not null)
            return Error(refusal);

        // 4. Already claimed for this reason? (If the list can't be read, Tracking still refuses a duplicate later.)
        var claims = await api.ListClaimsAsync(token, shipment.Id, ct);
        if (Refused(claims.StatusCode))
            return Error("Not allowed.");
        if (claims.Value?.FirstOrDefault(c => c.Reason == canonical) is { } existing)
            return Error($"{shipment.MerchantReference} already has a {canonical} claim, opened " +
                         $"{existing.CreatedAt:yyyy-MM-dd HH:mm} UTC.");

        // 5. Record the proposal. Nothing is opened.
        var action = store.ProposeClaim(merchantId, conversationId, shipment.Id, shipment.MerchantReference,
            shipment.Carrier, canonical, note);
        if (_proposed.All(a => a.Id != action.Id))
            _proposed.Add(action);

        return new
        {
            actionId = action.Id,
            status = "PendingApproval",
            summary = action.Summary,
            expiresAt = action.ExpiresAt,
            message = "Nothing has been opened. A person must approve this proposal before the claim is opened.",
        };
    }

    private async Task<string?> MerchantIdAsync(CancellationToken ct)
    {
        if (_merchantId is not null)
            return _merchantId;

        var me = await api.MeAsync(token, ct);
        if (Refused(me.StatusCode) || me.StatusCode != 200)
            return null;

        return _merchantId = me.Value?.MerchantId;
    }

    private bool Refused(int statusCode)
    {
        if (statusCode is not (401 or 403))
            return false;

        RejectedWith = statusCode;
        return true;
    }

    private static object Error(string message) => new { error = message };
}