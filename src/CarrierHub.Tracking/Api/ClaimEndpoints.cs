using CarrierHub.Tracking.Data;
using CarrierHub.Tracking.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CarrierHub.Tracking.Api;

// The Public API takes the key from the Idempotency-Key header and sends it in the body.
public sealed record OpenClaimRequest(Guid ShipmentId, string Reason, string? Note, string IdempotencyKey);

// Reason and status as strings: callers read "Late", whatever the JSON settings of either service are.
public sealed record ClaimResponse(Guid Id, Guid ShipmentId, string MerchantReference, string Carrier,
    string Reason, string Status, string? Note, DateTimeOffset CreatedAt)
{
    public static ClaimResponse From(ShipmentClaim c) => new(c.Id, c.ShipmentId, c.MerchantReference, c.Carrier,
        c.Reason.ToString(), c.Status.ToString(), c.Note, c.CreatedAt);
}

public static class ClaimEndpoints
{
    public const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    public static void MapClaimEndpoints(this WebApplication app)
    {
        var claims = app.MapGroup("/merchants/{merchantId}/claims");
        claims.MapPost("/", OpenAsync);
        claims.MapGet("/{id:guid}", GetAsync);
        claims.MapGet("/", ListAsync);
    }

    private static async Task<IResult> OpenAsync(string merchantId, OpenClaimRequest request,
        TrackingDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!Enum.TryParse<ClaimReason>(request.Reason, ignoreCase: true, out var reason) || !Enum.IsDefined(reason))
            return Results.BadRequest(new { error = $"reason must be one of: {string.Join(", ", Enum.GetNames<ClaimReason>())}." });

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Results.BadRequest(new { error = "An idempotency key is required." });

        // 1. A retry: this key was used before. Answer with the first result; never create a second claim.
        var earlier = await FindByKeyAsync(db, merchantId, request.IdempotencyKey, ct);
        if (earlier is not null)
            return Replay(earlier, request, reason);

        // 2. The shipment, through the merchant filter: another merchant's shipment is simply "not found".
        var shipment = await db.Shipments.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.ShipmentId && s.MerchantId == merchantId, ct);
        if (shipment is null)
            return Results.NotFound(new { error = "Shipment not found." });

        // 3. One claim per shipment and reason.
        var sameReason = await FindByReasonAsync(db, shipment.Id, reason, ct);
        if (sameReason is not null)
            return AlreadyClaimed(sameReason);

        // 4. The domain rules: is the claim allowed for this shipment, right now?
        ShipmentClaim claim;
        try
        {
            claim = ShipmentClaim.Open(shipment, reason, request.Note, request.IdempotencyKey, clock.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ClaimNotAllowedException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }

        db.Claims.Add(claim);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two requests raced past the checks above and the database let only one through.
            // Answer the loser exactly as the checks would have, had it arrived a moment later.
            db.ChangeTracker.Clear();

            var winner = await FindByKeyAsync(db, merchantId, request.IdempotencyKey, ct);
            if (winner is not null)
                return Replay(winner, request, reason);

            var other = await FindByReasonAsync(db, shipment.Id, reason, ct);
            return other is not null
                ? AlreadyClaimed(other)
                : Results.Conflict(new { error = "The claim conflicts with another claim. Try again." });
        }

        return Results.Created($"/merchants/{merchantId}/claims/{claim.Id}", ClaimResponse.From(claim));
    }

    private static async Task<IResult> GetAsync(string merchantId, Guid id, TrackingDbContext db, CancellationToken ct)
    {
        var claim = await db.Claims.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.MerchantId == merchantId, ct);

        return claim is null ? Results.NotFound() : Results.Ok(ClaimResponse.From(claim));
    }

    // This merchant's claims, newest first; optionally only one shipment's.
    private static async Task<IResult> ListAsync(string merchantId, Guid? shipmentId, int? limit,
        TrackingDbContext db, CancellationToken ct)
    {
        if (limit is < 1 or > MaxPageSize)
            return Results.BadRequest(new { error = $"limit must be between 1 and {MaxPageSize}." });

        var query = db.Claims.AsNoTracking().Where(c => c.MerchantId == merchantId);
        if (shipmentId is { } wanted)
            query = query.Where(c => c.ShipmentId == wanted);

        var claims = await query
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit ?? DefaultPageSize)
            .ToListAsync(ct);

        return Results.Ok(claims.Select(ClaimResponse.From));
    }

    private static Task<ShipmentClaim?> FindByKeyAsync(TrackingDbContext db, string merchantId, string key,
        CancellationToken ct) =>
        db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.MerchantId == merchantId && c.IdempotencyKey == key, ct);

    private static Task<ShipmentClaim?> FindByReasonAsync(TrackingDbContext db, Guid shipmentId, ClaimReason reason,
        CancellationToken ct) =>
        db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.ShipmentId == shipmentId && c.Reason == reason, ct);

    // The same key: the same request gets the first answer (200, not 201); a different request is refused.
    private static IResult Replay(ShipmentClaim earlier, OpenClaimRequest request, ClaimReason reason) =>
        earlier.IsSameRequest(request.ShipmentId, reason, request.Note)
            ? Results.Ok(ClaimResponse.From(earlier))
            : Results.UnprocessableEntity(new
            {
                error = "This idempotency key was already used for a different claim. Use a new key for a new claim.",
            });

    private static IResult AlreadyClaimed(ShipmentClaim existing) =>
        Results.Conflict(new
        {
            error = $"Shipment {existing.MerchantReference} already has a {existing.Reason} claim.",
            claimId = existing.Id,
        });

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}