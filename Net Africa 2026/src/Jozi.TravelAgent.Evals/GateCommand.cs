using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentCore.Evaluations.Otel;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Jozi.Shared;
using Spectre.Console;

namespace Jozi.TravelAgent.Evals;

public sealed record GoldenCase(
    string Id,
    List<string> Turns,
    string? ExpectedResponse,
    List<string>? Assertions,
    List<string>? ExpectedTools);

public sealed record GoldenDataset(
    List<string> Evaluators,
    Dictionary<string, double> Thresholds,
    List<GoldenCase> Cases);

internal sealed record CaseResult(GoldenCase Case, string SessionId, AgentTurn? LastTurn, IReadOnlyList<string> ToolsCalled, List<EvaluationScore> Scores);

internal sealed record EvaluatorSummary(string EvaluatorId, double? Mean, int Scored, int Errors, double Threshold)
{
    /// <summary>Nothing scored and nothing failed = not applicable (e.g. tool evaluators when no case calls tools).</summary>
    public bool Passed => Scored == 0 ? Errors == 0 : Mean >= Threshold;
}

/// <summary>
/// DEMO 2 — the pull-request quality gate.
/// Replays a small golden dataset against the agent config in the PR, captures spans in memory, scores each session with
/// the AgentCore Evaluate API (with ground truth), and fails the build if an evaluator's mean drops below its threshold.
/// </summary>
internal static class GateCommand
{
    private static readonly string[] ToolCallEvaluators = ["Builtin.ToolSelectionAccuracy", "Builtin.ToolParameterAccuracy"];

    public static async Task<int> RunAsync(CliOptions options)
    {
        var configPath = options.Get("config", "config/agent.json");
        var datasetPath = options.Get("dataset", "evals/golden-dataset.json");
        var reportPath = options.Get("report");
        var parallel = options.GetInt("parallel", 3);

        var config = AgentConfig.Load(configPath);
        var dataset = JsonSerializer.Deserialize<GoldenDataset>(File.ReadAllText(datasetPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })!;

        AnsiConsole.Write(new Rule("[bold]Agent quality gate · AgentCore Evaluations[/]").LeftJustified());
        AnsiConsole.MarkupLine($"config  [bold]{Markup.Escape(configPath)}[/]  v{Markup.Escape(config.Version)}  model {Markup.Escape(config.ModelId)}");
        AnsiConsole.MarkupLine($"dataset [bold]{Markup.Escape(datasetPath)}[/]  {dataset.Cases.Count} cases × {dataset.Evaluators.Count} evaluators\n");

        using var telemetry = AgentTelemetry.Start(new AgentTelemetryOptions { Export = SpanExport.None, Environment = "ci" });
        var bedrock = new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { RetryMode = RequestRetryMode.Adaptive, MaxErrorRetry = 8 });
        var evaluator = AgentCoreEvaluator.Create();
        var results = new ConcurrentBag<CaseResult>();

        await Parallel.ForEachAsync(dataset.Cases, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (goldenCase, ct) =>
        {
            // Run the conversation. A throttled or failed run is retried in a fresh session; if it still fails,
            // the case is recorded as an evaluation error (the gate fails loudly) instead of crashing the build.
            TravelAgent agent = null!;
            AgentTurn? lastTurn = null;
            for (var attempt = 1; ; attempt++)
            {
                agent = new TravelAgent(bedrock, config);
                try
                {
                    foreach (var userTurn in goldenCase.Turns) lastTurn = await agent.AskAsync(userTurn, ct);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is not OperationCanceledException)
                {
                    telemetry.Recorder!.Forget(agent.SessionId);
                    AnsiConsole.MarkupLine($"  [yellow]↻[/] {Markup.Escape(goldenCase.Id),-22} {Markup.Escape(ex.Message)} (retry {attempt})");
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
                }
                catch (Exception ex)
                {
                    var errors = dataset.Evaluators.Select(id => new EvaluationScore(id, null, null, null, agent.SessionId, null, null, "AgentRunFailed", ex.Message)).ToList();
                    results.Add(new CaseResult(goldenCase, agent.SessionId, lastTurn, [], errors));
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(goldenCase.Id),-22} agent run failed: {Markup.Escape(ex.Message)}");
                    return;
                }
            }

            var spans = telemetry.Recorder!.GetSessionSpans(agent.SessionId);
            var toolsCalled = SpanQueries.ToolsCalled(spans);
            var reference = new ReferenceData(goldenCase.ExpectedResponse, goldenCase.Assertions, goldenCase.ExpectedTools);

            var scores = new List<EvaluationScore>();
            foreach (var evaluatorId in dataset.Evaluators)
            {
                // Tool-call evaluators score each execute_tool span; with no tool calls there is nothing to score.
                // (Skipping a tool is caught by the trajectory evaluator instead.)
                if (toolsCalled.Count == 0 && ToolCallEvaluators.Contains(evaluatorId)) continue;
                try
                {
                    scores.AddRange(await evaluator.EvaluateAsync(evaluatorId, spans, reference, ct));
                }
                catch (Exception ex)
                {
                    scores.Add(new EvaluationScore(evaluatorId, null, null, null, agent.SessionId, null, null, ex.GetType().Name, ex.Message));
                }
            }

            results.Add(new CaseResult(goldenCase, agent.SessionId, lastTurn, toolsCalled, scores));
            var tools = toolsCalled.Count == 0 ? "[red]no tools[/]" : Markup.Escape(string.Join(" → ", toolsCalled));
            AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(goldenCase.Id),-22} {tools}");
        });

        var ordered = dataset.Cases.Select(c => results.Single(r => r.Case.Id == c.Id)).ToList();
        var summaries = Summarise(dataset, ordered);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(CaseTable(dataset, ordered));
        AnsiConsole.Write(SummaryTable(summaries));

        var passed = summaries.All(s => s.Passed);
        AnsiConsole.MarkupLine(passed
            ? "\n[bold green]PASS[/] agent quality is within thresholds."
            : "\n[bold red]FAIL[/] agent quality dropped below threshold. Blocking merge.");

        var markdown = Markdown(configPath, config, summaries, ordered, passed);
        if (reportPath is not null) await File.WriteAllTextAsync(reportPath, markdown);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
            await File.AppendAllTextAsync(stepSummary, markdown);

        return passed ? 0 : 1;
    }

    private static List<EvaluatorSummary> Summarise(GoldenDataset dataset, List<CaseResult> results) =>
        dataset.Evaluators.Select(id =>
        {
            var scores = results.SelectMany(r => r.Scores).Where(s => s.EvaluatorId == id).ToList();
            var values = scores.Where(s => !s.Failed && s.Value is not null).Select(s => s.Value!.Value).ToList();
            return new EvaluatorSummary(
                id,
                values.Count > 0 ? values.Average() : null,
                values.Count,
                scores.Count(s => s.Failed),
                dataset.Thresholds.GetValueOrDefault(id, 0));
        }).ToList();

    private static double? CaseMean(CaseResult r, string evaluatorId)
    {
        var values = r.Scores.Where(s => s.EvaluatorId == evaluatorId && !s.Failed && s.Value is not null).Select(s => s.Value!.Value).ToList();
        return values.Count > 0 ? values.Average() : null;
    }

    private static Table CaseTable(GoldenDataset dataset, List<CaseResult> results)
    {
        var table = new Table().RoundedBorder().Title("[bold]Per case[/]").AddColumn("Case").AddColumn("Tools called");
        foreach (var e in dataset.Evaluators) table.AddColumn(new TableColumn(Short(e)).Centered());
        foreach (var r in results)
        {
            var cells = new List<string>
            {
                Markup.Escape(r.Case.Id),
                r.ToolsCalled.Count == 0 ? "[red]none[/]" : Markup.Escape(string.Join(" → ", r.ToolsCalled)),
            };
            foreach (var e in dataset.Evaluators)
            {
                var mean = CaseMean(r, e);
                cells.Add(r.Scores.Any(s => s.EvaluatorId == e && s.Failed) ? "[red]err[/]" : $"[{ScoreRendering.Colour(mean)}]{ScoreRendering.Format(mean)}[/]");
            }
            table.AddRow(cells.ToArray());
        }
        return table;
    }

    private static Table SummaryTable(List<EvaluatorSummary> summaries)
    {
        var table = new Table().RoundedBorder().Title("[bold]Gate[/]")
            .AddColumn("Evaluator").AddColumn(new TableColumn("Mean").Centered()).AddColumn(new TableColumn("Threshold").Centered())
            .AddColumn(new TableColumn("Scored").Centered()).AddColumn(new TableColumn("Errors").Centered()).AddColumn("Result");
        foreach (var s in summaries)
        {
            table.AddRow(
                Short(s.EvaluatorId),
                $"[bold {ScoreRendering.Colour(s.Mean)}]{ScoreRendering.Format(s.Mean)}[/]",
                s.Threshold.ToString("0.00"),
                s.Scored.ToString(),
                s.Errors == 0 ? "0" : $"[red]{s.Errors}[/]",
                s.Passed ? "[green]pass[/]" : "[bold red]FAIL[/]");
        }
        return table;
    }

    private static string Markdown(string configPath, AgentConfig config, List<EvaluatorSummary> summaries, List<CaseResult> results, bool passed)
    {
        var sb = new StringBuilder();
        sb.AppendLine(passed ? "## ✅ Agent quality gate passed" : "## ❌ Agent quality gate failed");
        sb.AppendLine();
        sb.AppendLine($"Config `{configPath}` · version `{config.Version}` · model `{config.ModelId}` · scored by Amazon Bedrock AgentCore Evaluations");
        sb.AppendLine();
        sb.AppendLine("| Evaluator | Mean | Threshold | Scored | Errors | Result |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var s in summaries)
            sb.AppendLine($"| {Short(s.EvaluatorId)} | {ScoreRendering.Format(s.Mean)} | {s.Threshold:0.00} | {s.Scored} | {s.Errors} | {(s.Passed ? "✅" : "❌")} |");

        sb.AppendLine();
        sb.AppendLine("<details><summary>Per-case results</summary>");
        sb.AppendLine();
        sb.AppendLine("| Case | Tools called | " + string.Join(" | ", summaries.Select(s => Short(s.EvaluatorId))) + " |");
        sb.AppendLine("|---|---|" + string.Concat(summaries.Select(_ => "---:|")));
        foreach (var r in results)
        {
            var tools = r.ToolsCalled.Count == 0 ? "_none_" : string.Join(" → ", r.ToolsCalled);
            sb.AppendLine($"| {r.Case.Id} | {tools} | " + string.Join(" | ", summaries.Select(s => ScoreRendering.Format(CaseMean(r, s.EvaluatorId)))) + " |");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");

        var worst = results.SelectMany(r => r.Scores.Select(s => (r.Case.Id, Score: s)))
            .Where(x => !x.Score.Failed && x.Score.Value < 0.7)
            .OrderBy(x => x.Score.Value).Take(5).ToList();
        if (worst.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Lowest scores: what the judge said");
            foreach (var (caseId, s) in worst)
                sb.AppendLine($"- **{caseId}** · {Short(s.EvaluatorId)} = {ScoreRendering.Format(s.Value)}: {ScoreRendering.Truncate(s.Explanation ?? "", 400).ReplaceLineEndings(" ")}");
        }
        return sb.ToString();
    }

    private static string Short(string evaluatorId) => evaluatorId.Replace("Builtin.", "");
}
