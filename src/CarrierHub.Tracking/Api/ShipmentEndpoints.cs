using CarrierHub.Tracking.Application;
using CarrierHub.Tracking.Data;
using CarrierHub.Tracking.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CarrierHub.Tracking.Api;

public sealed record CreateShipmentRequest(string MerchantReference, string Carrier, string TrackingNumber, DateTimeOffset PromisedBy);

public sealed record CarrierEventRequest(string Carrier, string TrackingNumber, string CarrierEventId,
    ShipmentStatus Status, DateTimeOffset OccurredAt, string? Location, string? Description);

public sealed record TimelineEntry(ShipmentStatus Status, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt,
    string? Location, string? Description);

public sealed record ShipmentResponse(Guid Id, string MerchantReference, string Carrier, string TrackingNumber,
    ShipmentStatus Status, DateTimeOffset PromisedBy, DateTimeOffset? DeliveredAt, bool IsLate,
    IReadOnlyList<TimelineEntry> Timeline)
{
    public static ShipmentResponse From(Shipment s, DateTimeOffset now) => new(
        s.Id, s.MerchantReference, s.Carrier, s.TrackingNumber, s.Status, s.PromisedBy, s.DeliveredAt, s.IsLate(now),
        s.Timeline.Select(e => new TimelineEntry(e.Status, e.OccurredAt, e.ReceivedAt, e.Location, e.Description)).ToList());
}

public sealed record CarrierEventResponse(Guid ShipmentId, EventResult Result, ShipmentStatus Before,
    ShipmentStatus After, bool StatusChanged)
{
    public static CarrierEventResponse From(Guid shipmentId, EventOutcome o) =>
        new(shipmentId, o.Result, o.Before, o.After, o.StatusChanged);
}

public sealed record ActiveShipmentResponse(string Carrier, string TrackingNumber);

public static class ShipmentEndpoints
{
    public static void MapShipmentEndpoints(this WebApplication app)
    {
        // Every merchant route carries the merchant. The Public API will fill it from the caller's JWT.
        var merchant = app.MapGroup("/merchants/{merchantId}/shipments");
        merchant.MapPost("/", CreateAsync);
        merchant.MapGet("/{id:guid}", GetAsync);
        merchant.MapGet("/", SearchAsync);

        // Carrier events, until Phase 2 moves them onto the bus.
        app.MapPost("/carrier-events", AddCarrierEventAsync);
        // For the Carrier Gateway: what to poll. Phase 3 replaces this with "shipment created" messages.
        app.MapGet("/carriers/{carrier}/active-shipments", GetActiveShipmentsAsync);
    }

    private static async Task<IResult> CreateAsync(string merchantId, CreateShipmentRequest request,
        TrackingDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var existing = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(
            s => s.MerchantId == merchantId && s.MerchantReference == request.MerchantReference, ct);

        if (existing is not null)
            return existing.Carrier == request.Carrier && existing.TrackingNumber == request.TrackingNumber
                ? Results.Ok(ShipmentResponse.From(existing, clock.GetUtcNow()))   // a retry of the same create
                : Results.Conflict(new { error = $"Merchant reference '{request.MerchantReference}' is already used by another shipment." });

        Shipment shipment;
        try
        {
            shipment = new Shipment(merchantId, request.MerchantReference, request.Carrier, request.TrackingNumber,
                request.PromisedBy, clock.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        db.Shipments.Add(shipment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "A shipment with this merchant reference or tracking number already exists." });
        }

        return Results.Created($"/merchants/{merchantId}/shipments/{shipment.Id}",
            ShipmentResponse.From(shipment, clock.GetUtcNow()));
    }

    private static async Task<IResult> GetAsync(string merchantId, Guid id,
        TrackingDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // The merchant filter is part of the lookup: another merchant's shipment is simply "not found".
        var shipment = await db.Shipments.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.MerchantId == merchantId, ct);

        return shipment is null ? Results.NotFound() : Results.Ok(ShipmentResponse.From(shipment, clock.GetUtcNow()));
    }

    // Search by what a person would ask: a status, "late", or their own order number.
    private static async Task<IResult> SearchAsync(string merchantId, ShipmentStatus? status, bool? late, string? reference,
        TrackingDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var query = db.Shipments.AsNoTracking().Where(s => s.MerchantId == merchantId);

        if (status is { } wanted)
            query = query.Where(s => s.Status == wanted);

        if (!string.IsNullOrWhiteSpace(reference))
            query = query.Where(s => s.MerchantReference == reference);

        if (late == true)   // the same rule as Shipment.IsLate, written so the database can run it
            query = query.Where(s => s.DeliveredAt > s.PromisedBy
                || (s.DeliveredAt == null
                    && s.Status != ShipmentStatus.Delivered
                    && s.Status != ShipmentStatus.Returned
                    && s.PromisedBy < now));

        var shipments = await query.OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(shipments.Select(s => ShipmentResponse.From(s, now)));
    }

    private static async Task<IResult> AddCarrierEventAsync(CarrierEventRequest request,
        TrackingDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var result = await CarrierEvents.ApplyAsync(db, request, clock.GetUtcNow(), ct);

        return result.Status switch
        {
            ApplyStatus.Invalid => Results.BadRequest(new { error = result.Error }),
            ApplyStatus.UnknownShipment => Results.NotFound(new { error = result.Error }),
            _ => Results.Ok(CarrierEventResponse.From(result.ShipmentId!.Value, result.Outcome!)),
        };
    }

    private static async Task<IResult> GetActiveShipmentsAsync(string carrier, TrackingDbContext db, CancellationToken ct)
    {
        // Merchants type the carrier name ("FastShip"); the Gateway uses keys ("fastship"). Compare case-insensitively,
        // and return the name as stored, so the events the Gateway sends back find their shipment.
        var key = carrier.ToLowerInvariant();

        var active = await db.Shipments.AsNoTracking()
            .Where(s => s.Carrier.ToLower() == key
                && s.Status != ShipmentStatus.Delivered
                && s.Status != ShipmentStatus.Returned)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new ActiveShipmentResponse(s.Carrier, s.TrackingNumber))   // a projection: the events aren't loaded
            .Take(1000)
            .ToListAsync(ct);

        return Results.Ok(active);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}