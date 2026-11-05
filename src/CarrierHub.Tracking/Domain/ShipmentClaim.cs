namespace CarrierHub.Tracking.Domain;

// Why a merchant asks the carrier for compensation.
public enum ClaimReason { Late, Lost, Damaged }

// Open until the carrier answers. Accepted / Rejected come when carriers do.
public enum ClaimStatus { Open }

// The shipment's state doesn't allow this claim: a business rule, not bad input.
public sealed class ClaimNotAllowedException(string message) : Exception(message);

public sealed class ShipmentClaim
{
    public const int MaxNoteLength = 1000;
    public const int MaxIdempotencyKeyLength = 100;

    private ShipmentClaim() { }   // for EF Core, and for Open below

    public Guid Id { get; private set; }

    // Every query filters on this, as for shipments.
    public string MerchantId { get; private set; } = "";
    public Guid ShipmentId { get; private set; }

    // Copied from the shipment, so a list of claims is readable without loading shipments.
    public string MerchantReference { get; private set; } = "";
    public string Carrier { get; private set; } = "";

    public ClaimReason Reason { get; private set; }
    public ClaimStatus Status { get; private set; }
    public string? Note { get; private set; }

    // Chosen by the caller, unique per merchant: the same key twice means "the same request, retried".
    public string IdempotencyKey { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static ShipmentClaim Open(Shipment shipment, ClaimReason reason, string? note, string idempotencyKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            throw new ArgumentException($"The idempotency key can be at most {MaxIdempotencyKeyLength} characters.",
                nameof(idempotencyKey));

        note = NormalizeNote(note);
        if (note?.Length > MaxNoteLength)
            throw new ArgumentException($"The note can be at most {MaxNoteLength} characters.", nameof(note));

        // The rules live here, next to the data, so no caller (a person, a script, an AI agent) can skip them.
        var refusal = reason switch
        {
            ClaimReason.Late when !shipment.IsLate(now) =>
                "The shipment is not late: it was delivered on time, or its promised time hasn't passed yet.",
            ClaimReason.Lost when shipment.Status.IsFinal() || !shipment.IsLate(now) =>
                "A shipment can be claimed as lost only after its promised time has passed and before it is delivered or returned.",
            ClaimReason.Damaged when shipment.Status != ShipmentStatus.Delivered =>
                "A damage claim needs a delivered shipment.",
            _ => null,
        };
        if (refusal is not null)
            throw new ClaimNotAllowedException(refusal);

        return new ShipmentClaim
        {
            Id = Guid.CreateVersion7(now),
            MerchantId = shipment.MerchantId,
            ShipmentId = shipment.Id,
            MerchantReference = shipment.MerchantReference,
            Carrier = shipment.Carrier,
            Reason = reason,
            Status = ClaimStatus.Open,
            Note = note,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now.ToUniversalTime().AddTicks(-(now.Ticks % 10)),   // microseconds, as PostgreSQL stores it
        };
    }

    // A retry carries the same key AND the same request. The same key with a different request is a caller bug.
    public bool IsSameRequest(Guid shipmentId, ClaimReason reason, string? note) =>
        ShipmentId == shipmentId && Reason == reason && Note == NormalizeNote(note);

    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

}
