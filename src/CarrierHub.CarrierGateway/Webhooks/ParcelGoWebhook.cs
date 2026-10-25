using System.Net;
using System.Text.Json;
using Polly;

namespace CarrierHub.CarrierGateway.Webhooks;

public static class ParcelGoWebhook
{
    public const string Carrier = "parcelgo";

    // In gateway.events next to published and skipped: we told ParcelGo to retry because the bus was too slow.
    private const string Refused = "refused";

    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Payload(string EventId, string TrackingNumber, string Status, long OccurredAt, string? Location);

    public static void MapParcelGoWebhook(this WebApplication app) =>
        app.MapPost("/webhooks/parcelgo", HandleAsync);   // anonymous on purpose: the signature is the authentication

    // Pure translation, no network: what the contract tests check.
    public static (string TrackingNumber, ParseResult Result) Parse(string json)
    {
        var payload = JsonSerializer.Deserialize<Payload>(json, Json)
            ?? throw new JsonException("Empty ParcelGo webhook.");

        return Map(payload.Status) is { } status
            ? (payload.TrackingNumber, new ParseResult(
                [new CarrierEvent(payload.EventId, status, DateTimeOffset.FromUnixTimeSeconds(payload.OccurredAt), payload.Location, null)], []))
            : (payload.TrackingNumber, new ParseResult([], [payload.Status]));
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, IConfiguration config, CarrierEventPublisher publisher,
        ForwardedEvents forwarded, GatewayMetrics metrics, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger("CarrierHub.CarrierGateway.ParcelGoWebhook");

        var secret = config["ParcelGo:WebhookSecret"];
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogError("ParcelGo:WebhookSecret is not configured; webhooks can't be verified");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (request.ContentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Verify the raw text exactly as it arrived. Parsing first and serialising again would change the bytes.
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);

        if (!ParcelGoSignature.IsValid(request.Headers[ParcelGoSignature.Header], body, secret, clock.GetUtcNow(), Tolerance))
        {
            logger.LogWarning("Rejected a ParcelGo webhook: missing, wrong or expired signature");
            return Results.Unauthorized();
        }

        string trackingNumber;
        ParseResult parsed;
        try
        {
            (trackingNumber, parsed) = Parse(body);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ParcelGo sent a signed webhook we can't read");
            return Results.BadRequest();
        }

        if (parsed.Unknown.Count > 0)
        {
            // Signed, but not understood. Accept it so ParcelGo doesn't retry it forever; the warning is for a person.
            logger.LogWarning("ParcelGo sent a status we don't know for {TrackingNumber}: {Status}",
                trackingNumber, string.Join(", ", parsed.Unknown));
            return Results.Ok();
        }

        var e = parsed.Events[0];
        var shipment = new ActiveShipment(Carrier, trackingNumber);

        var alreadyForwarded = await forwarded.AlreadyForwardedAsync(Carrier, shipment, parsed.Events);
        if (alreadyForwarded.Contains(e.CarrierEventId))
        {
            metrics.Record(Carrier, GatewayMetrics.Skipped);
            return Results.Ok();
        }

        // Fail fast. ParcelGo waits for our answer with its own timeout (the mock's is 10 s). A quick 503 it can retry
        // beats holding the request until ParcelGo gives up and sends the event again anyway (ADR 13).
        var budget = config.GetValue("ParcelGo:PublishTimeout", TimeSpan.FromSeconds(4));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);

        try
        {
            await publisher.PublishAsync(shipment, e, timeout.Token);
            metrics.Record(Carrier, GatewayMetrics.Published);
            await forwarded.MarkForwardedAsync(Carrier, shipment, e.CarrierEventId);
            return Results.Ok();   // only now: the event is on the bus, which is durable
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our budget ran out, not ParcelGo's patience: the event isn't on the bus. Nothing is lost; ParcelGo retries.
            metrics.Record(Carrier, Refused);
            logger.LogWarning("The bus didn't take a ParcelGo event for {TrackingNumber} within {Budget}; asking ParcelGo to retry",
                trackingNumber, budget);
            return RetryLater(request);
        }
        catch (Exception ex) when (ex is Azure.Messaging.ServiceBus.ServiceBusException or TimeoutException)
        {
            // Not on the bus. Say so: ParcelGo retries, and nothing is lost.
            metrics.Record(Carrier, Refused);
            logger.LogWarning(ex, "Could not publish a ParcelGo event; asking ParcelGo to retry");
            return RetryLater(request);
        }
    }

    private static IResult RetryLater(HttpRequest request)
    {
        request.HttpContext.Response.Headers.RetryAfter = "5";   // seconds
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static ShipmentStatus? Map(string status) => status switch
    {
        "picked_up" => ShipmentStatus.PickedUp,
        "in_transit" => ShipmentStatus.InTransit,
        "out_for_delivery" => ShipmentStatus.OutForDelivery,
        "delivery_attempt_failed" => ShipmentStatus.DeliveryFailed,
        "delivered" => ShipmentStatus.Delivered,
        _ => null,
    };
}