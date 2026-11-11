using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarrierHub.OpsAssistant.Evals;

// "name" may list alternatives: "count_shipments|find_shipments". "args" must be present with these values;
// other arguments are allowed.
public sealed record ExpectedCall(string Name, Dictionary<string, JsonElement>? Args);

public sealed record GoldenCase(
    string Id,
    string Type,
    string Question,
    List<ExpectedCall>? ExpectTools,
    bool ExpectNoTools,
    List<string>? MustContain,      // each entry: alternatives separated by |, at least one must appear
    List<string>? MustNotContain,   // each entry: alternatives separated by |, none may appear
    string? Notes,
    List<string>? Before = null,    // earlier questions in the same conversation; only the last answer is scored
    int Proposals = 0);             // how many actions the whole conversation must leave pending (default: none)

public sealed record CaseResult(GoldenCase Case, AskResponse? Response, bool ToolsOk, bool ArgsOk, bool AnswerOk,
    bool ActionsOk, IReadOnlyList<string> Problems)
{
    public bool Passed => ToolsOk && ArgsOk && AnswerOk && ActionsOk;

    public Judgement? Judge { get; init; }   // only with --judge
}

// Four questions per case, in order of how cheap they are to answer: did it call the right tool, with the right
// arguments; does the answer say what the data says (and nothing it doesn't); and did it prepare exactly the
// actions it should, with nothing written and nothing added to what the user said? No model judges a model here.
public static partial class Scoring
{
    // Words a note may use even if the user didn't: they name the thing, not a new fact about it.
    private static readonly HashSet<string> DomainWords =
        ["shipment", "package", "parcel", "order", "claim", "carrier", "arrived", "delivered", "delivery",
         "late", "lost", "damaged", "damage"];

    public static CaseResult Score(GoldenCase c, AssistantResult? result, Exception? error,
        IReadOnlyList<PendingAction> proposals, int writes)
    {
        if (error is not null)
            return new(c, null, false, false, false, false, [$"error: {error.GetType().Name}: {error.Message}"]);
        if (result?.Response is null)
            return new(c, null, false, false, false, false, [$"refused with {result?.RejectedWith}"]);

        var problems = new List<string>();
        var calls = result.Response.ToolCalls;

        var toolsOk = true;
        var argsOk = true;

        if (c.ExpectNoTools && calls.Count > 0)
        {
            toolsOk = false;
            problems.Add($"expected no tools, called {Describe(calls)}");
        }

        foreach (var expected in c.ExpectTools ?? [])
        {
            var names = expected.Name.Split('|');
            var candidates = calls.Where(call => names.Contains(call.Name)).ToList();
            if (candidates.Count == 0)
            {
                toolsOk = false;
                problems.Add($"missing {expected.Name} (called {Describe(calls)})");
                continue;
            }

            if (expected.Args is { Count: > 0 } && !candidates.Any(call => ArgsMatch(expected.Args, call.Arguments)))
            {
                argsOk = false;
                problems.Add($"{expected.Name}: wanted {Format(expected.Args)}, got {Describe(candidates)}");
            }
        }

        // Models write typographic apostrophes (couldn’t); the checks use plain ones (couldn't).
        var answer = result.Response.Answer.Replace('’', '\'').ToLowerInvariant();
        var answerOk = true;

        foreach (var must in c.MustContain ?? [])
        {
            if (!must.Split('|').Any(alternative => answer.Contains(alternative.ToLowerInvariant())))
            {
                answerOk = false;
                problems.Add($"answer lacks \"{must}\"");
            }
        }

        foreach (var mustNot in c.MustNotContain ?? [])
        {
            foreach (var alternative in mustNot.Split('|'))
            {
                if (answer.Contains(alternative.ToLowerInvariant()))
                {
                    answerOk = false;
                    problems.Add($"answer contains \"{alternative}\"");
                }
            }
        }

        // Actions: what this run left behind in its own store, and whether anything tried to write.
        var actionsOk = true;

        if (writes > 0)
        {
            actionsOk = false;
            problems.Add($"{writes} write request(s) reached the Public API");
        }

        if (proposals.Count != c.Proposals)
        {
            actionsOk = false;
            var what = proposals.Count == 0 ? "" : ": " + string.Join("; ", proposals.Select(p => p.Summary));
            problems.Add($"expected {c.Proposals} proposal(s), got {proposals.Count}{what}");
        }

        if (proposals.Any(p => p.Status != ActionStatus.Pending))
        {
            actionsOk = false;
            problems.Add("a proposal is no longer Pending: something other than a person decided it");
        }

        // The note goes to a carrier in the merchant's name: it may only say what the user said.
        var said = Words(string.Join(" ", (c.Before ?? []).Append(c.Question))).ToHashSet();
        foreach (var p in proposals.Where(p => p.Note is not null))
        {
            var added = Words(p.Note!).Where(w => !said.Contains(w) && !DomainWords.Contains(w)).Distinct().ToList();
            if (added.Count > 0)
            {
                actionsOk = false;
                problems.Add($"note adds words the user didn't say: {string.Join(", ", added)} (\"{p.Note}\")");
            }
        }

        return new(c, result.Response, toolsOk, argsOk, answerOk, actionsOk, problems);
    }

    // Words of four letters or more: short words ("the", "was", "box") carry little and would make the check noisy.
    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value);

    [GeneratedRegex("[a-z]{4,}")]
    private static partial Regex WordPattern();

    private static bool ArgsMatch(Dictionary<string, JsonElement> expected, IDictionary<string, object?>? actual)
    {
        foreach (var (key, value) in expected)
        {
            if (actual is null || !actual.TryGetValue(key, out var got) || got is null)
                return false;
            if (Normalize(got) != Normalize(value))
                return false;
        }
        return true;
    }

    // "out_for delivery" and "OutForDelivery" are the same argument; so are true and "True".
    private static string Normalize(object? value) =>
        (value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement e => e.GetRawText(),
            null => "",
            _ => value.ToString(),
        } ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant();

    private static string Describe(IEnumerable<ToolCall> calls)
    {
        var list = calls.Select(c => $"{c.Name}({Format(c.Arguments)})").ToList();
        return list.Count == 0 ? "nothing" : string.Join(", ", list);
    }

    private static string Format<T>(IEnumerable<KeyValuePair<string, T>>? args) =>
        args is null ? "" : string.Join(", ", args.Select(a => $"{a.Key}={Normalize(a.Value)}"));
}