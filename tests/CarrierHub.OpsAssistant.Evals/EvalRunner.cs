using System.Globalization;
using System.Text;
using System.Text.Json;
using CarrierHub.AssistantTools;

namespace CarrierHub.OpsAssistant.Evals;

// dotnet run --project tests/CarrierHub.OpsAssistant.Evals -- --model qwen2.5:7b --label tools-v3 [--repeat 3]
//   [--case lookup-1] [--min 0.8]
// Needs Ollama running; doesn't need the AppHost: the Public API is the fake one in World.cs.
internal static class EvalRunner
{
    private static async Task<int> Main(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var options = new AssistantOptions
        {
            Endpoint = Arg("--endpoint") ?? "http://localhost:11434",
            Model = Arg("--model") ?? "qwen2.5:7b",
        };
        var only = Arg("--case");
        var label = Arg("--label") ?? "run";
        var repeat = int.Parse(Arg("--repeat") ?? "1", CultureInfo.InvariantCulture);
        var minPass = double.Parse(Arg("--min") ?? "0", CultureInfo.InvariantCulture);

        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden.json"));
        var cases = JsonSerializer.Deserialize<List<GoldenCase>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (only is not null)
            cases = cases.Where(c => only.Split(',', StringSplitOptions.TrimEntries).Contains(c.Id)).ToList();

        var chat = Assistant.CreateChatClient(options, enableSensitiveData: false);
        var api = new PublicApiClient(new HttpClient(new FakePublicApi()) { BaseAddress = new Uri("https://publicapi.test") });
        var assistant = new Assistant(chat, api, new FixedTime(World.Now), options);

        // The first call loads the model into memory; it isn't part of any case's time.
        Console.WriteLine($"Model {options.Model} ({label}), {cases.Count} cases x {repeat}. Warming up...");
        await assistant.AskAsync("How many shipments do I have?", World.Merchant1Token, CancellationToken.None);

        var results = new List<CaseRuns>();
        foreach (var c in cases)
        {
            var runs = new List<CaseResult>();
            for (var attempt = 0; attempt < repeat; attempt++)
            {
                AssistantResult? result = null;
                Exception? error = null;
                try
                {
                    result = await assistant.AskAsync(c.Question, World.Merchant1Token, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                runs.Add(Scoring.Score(c, result, error));
            }

            var caseRuns = new CaseRuns(c, runs);
            results.Add(caseRuns);

            var problems = string.Join("; ", runs.Where(r => !r.Passed).SelectMany(r => r.Problems).Distinct());
            Console.WriteLine($"{caseRuns.Verdict,-11} {caseRuns.Passes}/{runs.Count}  {c.Id,-14} {caseRuns.MedianMs,7} ms  {problems}");
        }

        var report = Report($"{options.Model} ({label})", repeat, results);
        Console.WriteLine();
        Console.WriteLine(report.Summary);

        var folder = Path.Combine(Directory.GetCurrentDirectory(), "eval-results");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{options.Model.Replace(':', '-')}-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(file, report.Markdown);
        Console.WriteLine($"Report: {file}");

        var allRuns = results.SelectMany(r => r.Runs).ToList();
        var passRate = allRuns.Count == 0 ? 0 : allRuns.Count(r => r.Passed) / (double)allRuns.Count;
        return passRate >= minPass ? 0 : 1;
    }

    private static (string Summary, string Markdown) Report(string title, int repeat, List<CaseRuns> results)
    {
        static string Rate(int ok, int all) => all == 0 ? "-" : $"{ok}/{all} ({100.0 * ok / all:0}%)";

        var allRuns = results.SelectMany(r => r.Runs).ToList();
        var answered = allRuns.Where(r => r.Response is not null).ToList();

        var durations = answered.Select(r => r.Response!.DurationMs).Order().ToList();
        int Percentile(double p) => durations.Count == 0 ? 0 : durations[(int)Math.Ceiling(p * durations.Count) - 1];

        var inputTokens = answered.Count == 0 ? 0 : answered.Average(r => r.Response!.InputTokens ?? 0);
        var outputTokens = answered.Count == 0 ? 0 : answered.Average(r => r.Response!.OutputTokens ?? 0);

        var summary = new StringBuilder();
        summary.AppendLine($"Model: {title}, each case {repeat}x");
        summary.AppendLine($"Passed: {Rate(allRuns.Count(r => r.Passed), allRuns.Count)} of all runs");
        summary.AppendLine($"Cases: {results.Count(r => r.Verdict == "STABLE")} stable pass, " +
                           $"{results.Count(r => r.Verdict == "FLAKY")} flaky, " +
                           $"{results.Count(r => r.Verdict == "FAIL")} stable fail");
        summary.AppendLine($"Right tool: {Rate(allRuns.Count(r => r.ToolsOk), allRuns.Count)}   " +
                           $"Right arguments: {Rate(allRuns.Count(r => r.ArgsOk), allRuns.Count)}   " +
                           $"Right answer: {Rate(allRuns.Count(r => r.AnswerOk), allRuns.Count)}");
        summary.AppendLine($"Time: P50 {Percentile(0.5)} ms, P95 {Percentile(0.95)} ms   " +
                           $"Tokens per question: {inputTokens:0} in, {outputTokens:0} out");
        summary.AppendLine("By type:");
        foreach (var group in results.GroupBy(r => r.Case.Type))
        {
            var runs = group.SelectMany(r => r.Runs).ToList();
            summary.AppendLine($"  {group.Key,-10} {Rate(runs.Count(r => r.Passed), runs.Count)}");
        }

        var md = new StringBuilder();
        md.AppendLine($"# Ops Assistant evaluation: {title}");
        md.AppendLine();
        md.AppendLine($"Run {DateTime.Now:yyyy-MM-dd HH:mm}, {results.Count} cases x {repeat}, fake Public API (World.cs), temperature 0.");
        md.AppendLine();
        md.AppendLine("```");
        md.Append(summary);
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("| Case | Type | Verdict | Passed | Median ms | Tools called (last run) | Answer (last run) | Problems (all failing runs) |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var last = r.Runs[^1];
            var calls = last.Response is null ? "" : string.Join("<br>", last.Response.ToolCalls.Select(c =>
                $"{c.Name}({string.Join(", ", (c.Arguments ?? new Dictionary<string, object?>()).Select(a => $"{a.Key}={a.Value}"))})"));
            var answer = (last.Response?.Answer ?? "").Replace("|", "\\|").Replace("\n", " ");
            var problems = string.Join("; ", r.Runs.Where(x => !x.Passed).SelectMany(x => x.Problems).Distinct()).Replace("|", "\\|");
            md.AppendLine($"| {r.Case.Id} | {r.Case.Type} | {r.Verdict} | {r.Passes}/{r.Runs.Count} | {r.MedianMs} | " +
                          $"{calls} | {answer} | {problems} |");
        }

        // Every failing answer, in full: the only way to tell a wrong answer from a check that's too strict.
        md.AppendLine();
        md.AppendLine("## Failing answers");
        foreach (var r in results)
        {
            foreach (var (run, i) in r.Runs.Select((run, i) => (run, i + 1)).Where(x => !x.run.Passed))
            {
                md.AppendLine();
                md.AppendLine($"**{r.Case.Id}, run {i}:** {string.Join("; ", run.Problems)}");
                md.AppendLine();
                md.AppendLine($"> {(run.Response?.Answer ?? "(no answer)").Replace("\n", " ")}");
            }
        }

        return (summary.ToString(), md.ToString());
    }
}

// All runs of one case. STABLE: passed every time; FAIL: never; FLAKY: sometimes.
internal sealed record CaseRuns(GoldenCase Case, List<CaseResult> Runs)
{
    public int Passes => Runs.Count(r => r.Passed);

    public string Verdict => Passes == Runs.Count ? "STABLE" : Passes == 0 ? "FAIL" : "FLAKY";

    public int MedianMs
    {
        get
        {
            var ms = Runs.Where(r => r.Response is not null).Select(r => r.Response!.DurationMs).Order().ToList();
            return ms.Count == 0 ? 0 : ms[ms.Count / 2];
        }
    }
}