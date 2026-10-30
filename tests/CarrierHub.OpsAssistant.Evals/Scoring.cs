using System.Text.Json;

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
    string? Notes);

public sealed record CaseResult(GoldenCase Case, AskResponse? Response, bool ToolsOk, bool ArgsOk, bool AnswerOk,
    IReadOnlyList<string> Problems)
{
    public bool Passed => ToolsOk && ArgsOk && AnswerOk;
}

// Three questions per case, in order of how cheap they are to answer: did it call the right tool, with the right
// arguments, and does the answer say what the data says (and nothing it doesn't)? No model judges a model here.
public static class Scoring
{
    public static CaseResult Score(GoldenCase c, AssistantResult? result, Exception? error)
    {
        if (error is not null)
            return new(c, null, false, false, false, [$"error: {error.GetType().Name}: {error.Message}"]);
        if (result?.Response is null)
            return new(c, null, false, false, false, [$"refused with {result?.RejectedWith}"]);

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

        var answer = result.Response.Answer.ToLowerInvariant();
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

        return new(c, result.Response, toolsOk, argsOk, answerOk, problems);
    }

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