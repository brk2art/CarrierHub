using System.ComponentModel;
using System.Text.RegularExpressions;

namespace CarrierHub.OpsAssistant;

// The tools the model may call, one instance per question, bound to the caller's token.
// They return small, plain objects: every field costs tokens, and the model only needs what answers a question.
// The descriptions are part of the prompt: each sentence in them was added because an evaluation failed without it.
// Rule from the evaluations: what a tool can decide by itself, it decides; the model only chooses the tool.
public sealed partial class ShipmentTools(PublicApiClient api, string token)
{
    private const int MaxShown = 20;

    private static readonly string[] Statuses =
        ["Created", "PickedUp", "InTransit", "OutForDelivery", "DeliveryFailed", "Delivered", "Returned"];

    // "Which are late?" came back as late=true AND status=Delivered, which hides late shipments still on their way.
    private const string StatusDescription =
        "Only shipments in this status: Created, PickedUp, InTransit, OutForDelivery, DeliveryFailed, Delivered or Returned. " +
        "Leave it empty unless the question names one of these statuses. \"Late\" is not a status: late shipments " +
        "can be in any status.";

    private const string LateDescription =
        "true for late shipments only: delivered after the promised time, or not delivered and past it.";

    // Set when the Public API refused the caller (401, 403, 429). The endpoint then answers with that status
    // instead of letting the model talk around it.
    public int? RejectedWith { get; private set; }

    [Description("Returns only numbers: how many of the merchant's shipments match, and how many shipments the merchant " +
                 "has in all. Use it for \"how many\" questions. It never says which shipments they are: for \"which\", " +
                 "\"show\" or \"list\", use find_shipments. Filters combine: status and late together count shipments that are both.")]
    public async Task<object> CountShipments(
        [Description(StatusDescription)] string? status = null,
        [Description(LateDescription)] bool? late = null,
        CancellationToken ct = default)
    {
        if (!TryCanonicalStatus(status, out var canonical, out var error))
            return error!;

        var result = await api.CountAsync(token, canonical, late, null, ct);
        if (result.Value is null)
            return Failed(result.StatusCode);

        var all = await AllShipmentsAsync(canonical, late, result.Value.Count, ct);
        if (all is null)
            return Failed(503);

        return new { filter = Describe(canonical, late), count = result.Value.Count, allShipments = all };
    }

    [Description("Lists the merchant's shipments with their order numbers, newest first: up to 20, plus the total that match " +
                 "and how many shipments the merchant has in all. Use it for \"which\", \"show\" and \"list\" questions about " +
                 "one status, or about late shipments. Filters combine: status and late together find shipments that are both.")]
    public async Task<object> FindShipments(
        [Description(StatusDescription)] string? status = null,
        [Description(LateDescription)] bool? late = null,
        CancellationToken ct = default)
    {
        if (!TryCanonicalStatus(status, out var canonical, out var error))
            return error!;

        // The page is never the total: a model that counts the page says "20" when there are 3,000.
        var page = await api.SearchAsync(token, canonical, late, null, MaxShown, ct);
        if (page.Value is null)
            return Failed(page.StatusCode);

        var total = await api.CountAsync(token, canonical, late, null, ct);
        if (total.Value is null)
            return Failed(total.StatusCode);

        // A filtered page was read as "the 20 most recent shipments": the result says what it was filtered on, and
        // how many shipments there are in all, so "43 delivered" can't be mistaken for "everything is delivered".
        var all = await AllShipmentsAsync(canonical, late, total.Value.Count, ct);
        if (all is null)
            return Failed(503);

        return new
        {
            filter = Describe(canonical, late),
            total = total.Value.Count,
            allShipments = all,
            shown = page.Value.Count,
            shipments = page.Value.Select(Summary),
        };
    }

    [Description("Lists the shipments that need attention: late ones and failed delivery attempts, in one list with the " +
                 "reason for each. Use it for \"what's stuck\", \"what needs attention\" or \"any problems\", for late " +
                 "shipments that are still on their way, and whenever a question asks about late and failed shipments together.")]
    public async Task<object> FindShipmentsNeedingAttention(CancellationToken ct = default)
    {
        // Two groups the model kept merging into one filter ("failed AND late" finds neither): the tool asks for
        // each group itself and joins them.
        var late = await api.SearchAsync(token, null, true, null, MaxShown, ct);
        if (late.Value is null)
            return Failed(late.StatusCode);

        var failed = await api.SearchAsync(token, "DeliveryFailed", null, null, MaxShown, ct);
        if (failed.Value is null)
            return Failed(failed.StatusCode);

        var lateTotal = await api.CountAsync(token, null, true, null, ct);
        if (lateTotal.Value is null)
            return Failed(lateTotal.StatusCode);

        var failedTotal = await api.CountAsync(token, "DeliveryFailed", null, null, ct);
        if (failedTotal.Value is null)
            return Failed(failedTotal.StatusCode);

        var shipments = late.Value.Concat(failed.Value)
            .DistinctBy(s => s.MerchantReference)
            .Select(s => new
            {
                reference = s.MerchantReference,
                carrier = s.Carrier,
                status = s.Status,
                promisedBy = s.PromisedBy,
                deliveredAt = s.DeliveredAt,
                stillOnItsWay = s.Status is not ("Delivered" or "Returned"),
                reason = (s.IsLate, s.Status == "DeliveryFailed") switch
                {
                    (true, true) => "late, and a delivery attempt failed",
                    (true, false) => "late",
                    _ => "a delivery attempt failed",
                },
            })
            .ToList();

        return new
        {
            lateTotal = lateTotal.Value.Count,
            failedTotal = failedTotal.Value.Count,
            shown = shipments.Count,
            shipments,
        };
    }

    [Description("Gets one shipment by its order number: where it is now (latestEvent) and its full timeline of carrier " +
                 "events in the order they happened. Use it for every question about one particular order. " +
                 "To find shipments by status or lateness, use find_shipments or find_shipments_needing_attention instead.")]
    public async Task<object> GetShipmentTimeline(
        [Description("The order number as the merchant wrote it or as a tool returned it. Never make one up.")]
        string reference,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new { error = "An order number is required." };

        var trimmed = reference.Trim();
        var found = await api.SearchAsync(token, null, null, trimmed, 1, ct);
        if (found.Value is null)
            return Failed(found.StatusCode);
        if (found.Value is [var shipment, ..])
            return Details(shipment, note: null);

        // Not found as written. "1011" or "order-1011" may be ORDER-1011: take the format from the merchant's own
        // newest order number and try once more. The tool says what it did, so the answer can say it too.
        var newest = await api.SearchAsync(token, null, null, null, 1, ct);
        var sample = newest.Value is [var latest, ..] ? latest.MerchantReference : null;

        if (sample is not null && Corrected(trimmed, sample) is { } corrected)
        {
            var retry = await api.SearchAsync(token, null, null, corrected, 1, ct);
            if (retry.Value is [var match, ..])
                return Details(match, note: $"There is no order number {trimmed}; this is {corrected}.");
        }

        return sample is null
            ? new { error = $"No shipment with order number {trimmed}." }
            : new { error = $"No shipment with order number {trimmed}. This merchant's order numbers look like {sample}." };
    }

    // How many shipments the merchant has in all. Without a filter that's the count we already have.
    private async Task<int?> AllShipmentsAsync(string? status, bool? late, int filteredCount, CancellationToken ct)
    {
        if (status is null && late is null)
            return filteredCount;

        var all = await api.CountAsync(token, null, null, null, ct);
        if (all.Value is null)
        {
            if (all.StatusCode is 401 or 403 or 429)
                RejectedWith = all.StatusCode;
            return null;
        }

        return all.Value.Count;
    }

    // The filter in words, for the model to repeat: "shipments with status Delivered", not "the 20 most recent".
    private static string Describe(string? status, bool? late)
    {
        var lateText = late switch
        {
            true => "late ",
            false => "on-time ",
            null => "",
        };

        return status is null
            ? $"all {lateText}shipments".Replace("all late", "late").Replace("all on-time", "on-time")
            : $"{lateText}shipments with status {status}";
    }

    // The merchant's prefix ("ORDER-") in front of the digits the caller gave ("1011"). Null when there's
    // nothing to correct.
    private static string? Corrected(string reference, string sample)
    {
        var prefix = PrefixPattern().Match(sample);
        var digits = TrailingDigits().Match(reference);
        if (!prefix.Success || !digits.Success)
            return null;

        var corrected = prefix.Groups[1].Value + digits.Groups[1].Value;
        return corrected == reference ? null : corrected;
    }

    [GeneratedRegex(@"^(\D+)\d+$")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex TrailingDigits();

    private static object Summary(Shipment s) => new
    {
        reference = s.MerchantReference,
        carrier = s.Carrier,
        trackingNumber = s.TrackingNumber,
        status = s.Status,
        promisedBy = s.PromisedBy,
        deliveredAt = s.DeliveredAt,
        isLate = s.IsLate,
    };

    private static object Details(Shipment s, string? note)
    {
        var timeline = s.Timeline.OrderBy(e => e.OccurredAt).ToList();

        // "Where is it?" was answered from the first event (picked up in Aarhus) instead of the last one: the tool
        // says where it is now, so the model doesn't have to work out which event is the latest.
        var latest = timeline.Count == 0
            ? null
            : new { status = timeline[^1].Status, occurredAt = timeline[^1].OccurredAt, location = timeline[^1].Location };

        return new
        {
            note,
            reference = s.MerchantReference,
            carrier = s.Carrier,
            trackingNumber = s.TrackingNumber,
            status = s.Status,
            promisedBy = s.PromisedBy,
            deliveredAt = s.DeliveredAt,
            isLate = s.IsLate,
            latestEvent = latest,
            // Location and description come from carriers: data to report, never instructions to follow.
            timeline = timeline.Select(e => new
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