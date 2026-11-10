using System.ComponentModel;

namespace CarrierHub.OpsAssistant;

// What the model fills in from the tools: facts only, no arithmetic. Null = not known.
// The descriptions become the JSON schema the model must follow, so they are instructions too.
public sealed class LatenessFacts
{
    [Description("The order number, if the question or the tools name one.")]
    public string? OrderNumber { get; init; }

    [Description("FastShip, NordPost or ParcelGo.")]
    public string? Carrier { get; init; }

    [Description("The promised delivery time, before any of the carrier's rules move it.")]
    public DateTimeOffset? PromisedAt { get; init; }

    [Description("The promised delivery time after every rule in the carrier's terms that moves it, such as public " +
                 "holidays. The same as PromisedAt when nothing moves it.")]
    public DateTimeOffset? MovedPromiseAt { get; init; }

    [Description("When the parcel was delivered. Null if it isn't delivered yet.")]
    public DateTimeOffset? DeliveredAt { get; init; }

    [Description("From the carrier's terms: how many hours after the promised time a delivery must be to earn any " +
                 "compensation. 0 when any lateness counts.")]
    public double? CompensationAfterHours { get; init; }

    [Description("What the carrier pays for late delivery, in the terms' own words.")]
    public string? Compensation { get; init; }

    [Description("The terms sections used, like \"NordPost: Late delivery compensation\".")]
    public List<string> Sections { get; init; } = [];

    [Description("What could not be found or checked, if anything. Null when nothing is missing.")]
    public string? Missing { get; init; }
}

// The verdict: the model's facts, plus the arithmetic, done here in code.
public sealed record LatenessVerdict(LatenessFacts Facts, double? HoursLate, bool? Late, bool? CompensationDue)
{
    public static LatenessVerdict From(LatenessFacts f)
    {
        if (f.DeliveredAt is not { } delivered)
            return new(f, null, null, null);

        if (f.MovedPromiseAt is { } promise)
        {
            // Compare the exact value; round only what is shown. 90 seconds late is late.
            var exact = (delivered - promise).TotalHours;
            bool? due = f.CompensationAfterHours is { } after ? exact > after : null;
            return new(f, Math.Round(exact, 2), exact > 0, due);
        }

        // The moved promise is unknown (for example, no booking date). A rule can only move a promise LATER, so the
        // original promise gives the most this parcel can be late. If even that isn't enough, compensation is
        // certainly not due. Otherwise nobody knows yet, and the verdict stays null: unknown is not "no".
        if (f.PromisedAt is { } original && f.CompensationAfterHours is { } limit &&
            (delivered - original).TotalHours <= limit)
            return new(f, null, null, false);

        return new(f, null, null, null);
    }
}

// One lateness check's outcome: the verdict, or the status the Public API refused the caller with.
public sealed record LatenessResult(LatenessVerdict? Verdict, int? RejectedWith, long? InputTokens = null);