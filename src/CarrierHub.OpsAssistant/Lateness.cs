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
        if (f.MovedPromiseAt is not { } promise || f.DeliveredAt is not { } delivered)
            return new(f, null, null, null);

        var hours = Math.Round((delivered - promise).TotalHours, 1);
        bool? due = f.CompensationAfterHours is { } after ? hours > after : null;
        return new(f, hours, hours > 0, due);
    }
}

// One lateness check's outcome: the verdict, or the status the Public API refused the caller with.
public sealed record LatenessResult(LatenessVerdict? Verdict, int? RejectedWith);