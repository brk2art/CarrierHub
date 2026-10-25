using System.Diagnostics.Metrics;
using CarrierHub.Tracking.Api;
using CarrierHub.Tracking.Data;
using CarrierHub.Tracking.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CarrierHub.Tracking.Application;

public enum ApplyStatus { Applied, UnknownShipment, Invalid }

public sealed record ApplyResult(ApplyStatus Status, Guid? ShipmentId = null, EventOutcome? Outcome = null, string? Error = null);

// Applies one carrier event to its shipment. The HTTP endpoint and the Service Bus consumer both call this,
// so the rules live in one place.
public static class CarrierEvents
{
    private static readonly Meter Meter = new("CarrierHub.Tracking");

    // What the merchant cares about in the end: from creating the shipment to us knowing it was delivered.
    // The mock journeys take 120 s (210 s with a failed attempt), so anything above that is our delay.
    private static readonly Histogram<double> TimeToDelivered = Meter.CreateHistogram<double>(
        "carrierhub.shipment.time_to_delivered", "s",
        "Time from the shipment being created to Tracking seeing it delivered",
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [60, 120, 135, 150, 180, 210, 240, 300, 360, 480, 600, 900, 1200, 1800],
        });

    public static async Task<ApplyResult> ApplyAsync(TrackingDbContext db, CarrierEventRequest request,
        DateTimeOffset receivedAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Carrier) || string.IsNullOrWhiteSpace(request.TrackingNumber))
            return new ApplyResult(ApplyStatus.Invalid, Error: "Carrier and tracking number are required.");

        TrackingEvent NewEvent() => new(request.CarrierEventId, request.Status, request.OccurredAt, receivedAt,
            request.Location, request.Description);

        try
        {
            NewEvent();   // validate once before touching the database
        }
        catch (ArgumentException ex)
        {
            return new ApplyResult(ApplyStatus.Invalid, Error: ex.Message);
        }

        // Merchants type "ParcelGo", the Gateway sends "parcelgo": compare case-insensitively.
        var carrier = request.Carrier.ToLowerInvariant();

        for (var attempt = 1; ; attempt++)
        {
            var shipment = await db.Shipments.FirstOrDefaultAsync(
                s => s.Carrier.ToLower() == carrier && s.TrackingNumber == request.TrackingNumber, ct);

            if (shipment is null)
                return new ApplyResult(ApplyStatus.UnknownShipment, Error: $"No shipment for {request.Carrier} {request.TrackingNumber}.");

            var outcome = shipment.AddEvent(NewEvent());
            if (outcome.Result == EventResult.Duplicate)
                return new ApplyResult(ApplyStatus.Applied, shipment.Id, outcome);

            try
            {
                await db.SaveChangesAsync(ct);

                // Delivered is final, so this is recorded once per shipment.
                if (outcome.StatusChanged && shipment.Status == ShipmentStatus.Delivered)
                {
                    TimeToDelivered.Record((receivedAt - shipment.CreatedAt).TotalSeconds,
                        new KeyValuePair<string, object?>("carrier", carrier));
                }

                return new ApplyResult(ApplyStatus.Applied, shipment.Id, outcome);
            }
            catch (DbUpdateException ex) when (attempt < 5 && (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex)))
            {
                // Another writer saved this shipment first. Through the bus, sessions make this rare; over HTTP it can still happen.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}