namespace CarrierHub.MockCarriers;

// FastShip: a REST API with JSON, short status codes, and times in Danish local time with an offset.
public static class FastShip
{
    public const string Name = "fastship";

    public sealed record Event(string Id, string Code, DateTimeOffset Timestamp, string Location);
    public sealed record Response(string TrackingNumber, IReadOnlyList<Event> Events);

    public static void MapFastShip(this WebApplication app) =>
        app.MapGet("/fastship/v2/shipments/{trackingNumber}/events",
            async (string trackingNumber, CarrierState state, HttpContext http, CancellationToken ct) =>
            {
                if (await FaultInjection.BeforeAnswerAsync(Name, state, http, ct) is { } failure)
                    return failure;

                // Like most polling APIs, it returns the whole history every time, not only what's new.
                var events = state.HappenedSoFar(Name, trackingNumber).Select(e => new Event(
                    Id: $"FS-{trackingNumber}-{e.Step.Index}",
                    Code: Code(e.Step.Step),
                    Timestamp: TimeZoneInfo.ConvertTime(e.At, Copenhagen.Zone),
                    Location: e.Step.Location));

                return Results.Ok(new Response(trackingNumber, FaultInjection.Mangle(events, state.FaultsFor(Name))));
            });

    private static string Code(Step step) => step switch
    {
        Step.PickedUp => "PU",
        Step.InTransit => "IT",
        Step.OutForDelivery => "OD",
        Step.DeliveryFailed => "NA",   // "not available": nobody home
        Step.Delivered => "DL",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };
}