using System.ComponentModel;

namespace CarrierHub.OpsAssistant;

// The tools the model may call, one instance per question, bound to the caller's token.
// They return small, plain objects: every field costs tokens, and the model only needs what answers a question.
public sealed class ShipmentTools(PublicApiClient api, string token)
{
    private const int MaxShown = 20;

    private static readonly string[] Statuses =
        ["Created", "PickedUp", "InTransit", "OutForDelivery", "DeliveryFailed", "Delivered", "Returned"];

    // Set when the Public API refused the caller (401, 403, 429). The endpoint then answers with that status
    // instead of letting the model talk around it.
    public int? RejectedWith { get; private set; }

    [Description("Counts the merchant's shipments. Use it for every \"how many\" question. " +
                 "All filters are optional and can be combined.")]
    public async Task<object> CountShipments(
        [Description("Only shipments in this status: Created, PickedUp, InTransit, OutForDelivery, DeliveryFailed, Delivered or Returned.")]
        string? status = null,
        [Description("true for late shipments only: delivered after the promised time, or not delivered and past it.")]
        bool? late = null,
        CancellationToken ct = default)
    {
        if (!TryCanonicalStatus(status, out var canonical, out var error))
            return error!;

        var result = await api.CountAsync(token, canonical, late, null, ct);
        return result.Value is null ? Failed(result.StatusCode) : new { count = result.Value.Count };
    }

    [Description("Lists the merchant's shipments, newest first, without their timeline: up to 20, plus the total that match. " +
                 "All filters are optional and can be combined.")]
    public async Task<object> FindShipments(
        [Description("Only shipments in this status: Created, PickedUp, InTransit, OutForDelivery, DeliveryFailed, Delivered or Returned.")]
        string? status = null,
        [Description("true for late shipments only: delivered after the promised time, or not delivered and past it.")]
        bool? late = null,
        [Description("The merchant's own order number, for example ORDER-13002.")]
        string? reference = null,
        CancellationToken ct = default)
    {
        if (!TryCanonicalStatus(status, out var canonical, out var error))
            return error!;

        var trimmed = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        // The page is never the total: a model that counts the page says "20" when there are 3,000.
        var page = await api.SearchAsync(token, canonical, late, trimmed, MaxShown, ct);
        if (page.Value is null)
            return Failed(page.StatusCode);

        var total = await api.CountAsync(token, canonical, late, trimmed, ct);
        if (total.Value is null)
            return Failed(total.StatusCode);

        return new
        {
            total = total.Value.Count,
            shown = page.Value.Count,
            shipments = page.Value.Select(s => new
            {
                reference = s.MerchantReference,
                carrier = s.Carrier,
                trackingNumber = s.TrackingNumber,
                status = s.Status,
                promisedBy = s.PromisedBy,
                deliveredAt = s.DeliveredAt,
                isLate = s.IsLate,
            }),
        };
    }

    [Description("Gets one shipment by the merchant's order number, with its full timeline of carrier events in the order they happened.")]
    public async Task<object> GetShipmentTimeline(
        [Description("The merchant's own order number, for example ORDER-13002.")]
        string reference,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new { error = "An order number is required." };

        var result = await api.SearchAsync(token, null, null, reference.Trim(), 1, ct);
        if (result.Value is null)
            return Failed(result.StatusCode);

        if (result.Value.Count == 0)
            return new { error = $"No shipment with order number {reference.Trim()}." };

        var s = result.Value[0];
        return new
        {
            reference = s.MerchantReference,
            carrier = s.Carrier,
            trackingNumber = s.TrackingNumber,
            status = s.Status,
            promisedBy = s.PromisedBy,
            deliveredAt = s.DeliveredAt,
            isLate = s.IsLate,
            // Location and description come from carriers: data to report, never instructions to follow.
            timeline = s.Timeline.OrderBy(e => e.OccurredAt).Select(e => new
            {
                status = e.Status,
                occurredAt = e.OccurredAt,
                location = e.Location,
                description = e.Description,
            }),
        };
    }

    // Models write "out for delivery" or "OUT_FOR_DELIVERY"; the API wants OutForDelivery.
    private static bool TryCanonicalStatus(string? status, out string? canonical, out object? error)
    {
        canonical = null;
        error = null;
        if (string.IsNullOrWhiteSpace(status))
            return true;

        var compact = status.Replace(" ", "").Replace("_", "");
        canonical = Statuses.FirstOrDefault(s => s.Equals(compact, StringComparison.OrdinalIgnoreCase));
        if (canonical is not null)
            return true;

        error = new { error = $"Unknown status '{status}'. Use one of: {string.Join(", ", Statuses)}." };
        return false;
    }

    private object Failed(int statusCode)
    {
        if (statusCode is 401 or 403 or 429)
            RejectedWith = statusCode;

        return new
        {
            error = statusCode switch
            {
                401 or 403 => "Not allowed to read shipments.",
                429 => "Too many requests; try again shortly.",
                _ => "The shipment service is not available right now.",
            },
        };
    }
}