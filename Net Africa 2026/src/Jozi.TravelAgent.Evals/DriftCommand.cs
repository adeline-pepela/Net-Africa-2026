using System.Globalization;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Jozi.Shared;
using Spectre.Console;

namespace Jozi.TravelAgent.Evals;

/// <summary>
/// DEMO 3 — drift check over online-evaluation results.
/// Online evaluation writes one "gen_ai.evaluation.result" record per score into its results log group.
/// This compares the mean score per evaluator in a recent window against a baseline window, shows the
/// lowest-scoring recent sessions with the judge's explanation, and exits 2 when any evaluator drops more than --max-drop.
/// Run it on a schedule (see .github/workflows/agent-drift-check.yml) or alarm on the metrics directly.
/// </summary>
internal static class DriftCommand
{
    private const string StatsQuery = """
        filter name = "gen_ai.evaluation.result" and ispresent(attributes.gen_ai.evaluation.score.value){SERVICE}
        | stats avg(attributes.gen_ai.evaluation.score.value) as avg_score, count(*) as n by attributes.gen_ai.evaluation.name
        """;

    private const string WorstQuery = """
        filter name = "gen_ai.evaluation.result" and ispresent(attributes.gen_ai.evaluation.score.value){SERVICE}
        | fields @timestamp, attributes.gen_ai.evaluation.name, attributes.gen_ai.evaluation.score.value, attributes.session.id, attributes.gen_ai.evaluation.explanation
        | sort attributes.gen_ai.evaluation.score.value asc
        | limit 5
        """;

    public static async Task<int> RunAsync(CliOptions options)
    {
        var logGroup = options.Get("results-log-group")
            ?? throw new ArgumentException("--results-log-group is required (printed by infra/setup-online-eval.sh).");
        var serviceName = options.Get("service-name");
        var recentMinutes = options.GetInt("recent-minutes", 30);
        var baselineHours = options.GetInt("baseline-hours", 24);
        var maxDrop = options.GetDouble("max-drop", 0.15);

        var serviceFilter = serviceName is null ? "" : $" and resource.attributes.service.name = \"{serviceName}\"";
        var statsQuery = StatsQuery.Replace("{SERVICE}", serviceFilter);
        var worstQuery = WorstQuery.Replace("{SERVICE}", serviceFilter);
        var now = DateTimeOffset.UtcNow;
        var recentStart = now.AddMinutes(-recentMinutes);
        var baselineStart = now.AddHours(-baselineHours);

        var logs = new AmazonCloudWatchLogsClient();

        AnsiConsole.Write(new Rule("[bold]Production drift check · online evaluation results[/]").LeftJustified());
        AnsiConsole.MarkupLine($"log group [bold]{Markup.Escape(logGroup)}[/]");
        AnsiConsole.MarkupLine($"baseline  {baselineStart:HH:mm dd MMM} → {recentStart:HH:mm dd MMM} UTC");
        AnsiConsole.MarkupLine($"recent    {recentStart:HH:mm dd MMM} → {now:HH:mm dd MMM} UTC\n");

        var (baseline, recent, worst) = await AnsiConsole.Status().StartAsync("Querying CloudWatch Logs Insights…", async _ =>
        {
            var b = QueryAsync(logs, logGroup, statsQuery, baselineStart, recentStart);
            var r = QueryAsync(logs, logGroup, statsQuery, recentStart, now);
            var w = QueryAsync(logs, logGroup, worstQuery, recentStart, now);
            return (await b, await r, await w);
        });

        var baselineByEvaluator = ToStats(baseline);
        var recentByEvaluator = ToStats(recent);
        var evaluators = baselineByEvaluator.Keys.Union(recentByEvaluator.Keys).Order().ToList();

        if (evaluators.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No evaluation results in these windows yet. Generate traffic and wait for the session timeout.[/]");
            return 0;
        }

        var drifted = false;
        var table = new Table().RoundedBorder()
            .AddColumn("Evaluator")
            .AddColumn(new TableColumn("Baseline").Centered())
            .AddColumn(new TableColumn("Recent").Centered())
            .AddColumn(new TableColumn("Δ").Centered())
            .AddColumn(new TableColumn("Scores (base/recent)").Centered())
            .AddColumn("Status");

        foreach (var e in evaluators)
        {
            var hasBaseline = baselineByEvaluator.TryGetValue(e, out var bs);
            var hasRecent = recentByEvaluator.TryGetValue(e, out var rs);
            double? b = hasBaseline ? bs.Mean : null;
            double? r = hasRecent ? rs.Mean : null;
            double? delta = b is not null && r is not null ? r - b : null;
            var isDrift = delta < -maxDrop;
            drifted |= isDrift;

            table.AddRow(
                e.Replace("Builtin.", ""),
                $"[{ScoreRendering.Colour(b)}]{ScoreRendering.Format(b)}[/]",
                $"[bold {ScoreRendering.Colour(r)}]{ScoreRendering.Format(r)}[/]",
                delta is { } d ? (d < 0 ? $"[red]{d:+0.00;-0.00}[/]" : $"[green]{d:+0.00;-0.00}[/]") : "-",
                $"{(hasBaseline ? bs.Count : 0)} / {(hasRecent ? rs.Count : 0)}",
                isDrift ? "[bold red]DRIFT[/]" : "[green]ok[/]");
        }
        AnsiConsole.Write(table);

        if (worst.Count > 0)
        {
            var worstTable = new Table().RoundedBorder().Title("[bold]Lowest recent scores[/]").Expand()
                .AddColumn("Evaluator").AddColumn("Score").AddColumn("Session").AddColumn("Why");
            foreach (var row in worst)
            {
                var score = double.TryParse(Field(row, "attributes.gen_ai.evaluation.score.value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : (double?)null;
                worstTable.AddRow(
                    Markup.Escape(Field(row, "attributes.gen_ai.evaluation.name").Replace("Builtin.", "")),
                    $"[{ScoreRendering.Colour(score)}]{ScoreRendering.Format(score)}[/]",
                    Markup.Escape(Field(row, "attributes.session.id")),
                    Markup.Escape(ScoreRendering.Truncate(Field(row, "attributes.gen_ai.evaluation.explanation"), 120)));
            }
            AnsiConsole.Write(worstTable);
        }

        AnsiConsole.MarkupLine(drifted
            ? $"\n[bold red]Quality drift detected[/] (an evaluator dropped by more than {maxDrop:0.00})."
            : "\n[bold green]No drift.[/]");
        return drifted ? 2 : 0;
    }

    private static Dictionary<string, (double Mean, int Count)> ToStats(List<List<ResultField>> rows) =>
        rows.ToDictionary(
            row => Field(row, "attributes.gen_ai.evaluation.name"),
            row => (double.Parse(Field(row, "avg_score"), CultureInfo.InvariantCulture), int.Parse(Field(row, "n"), CultureInfo.InvariantCulture)));

    private static string Field(List<ResultField> row, string name) => row.FirstOrDefault(f => f.Field == name)?.Value ?? "";

    private static async Task<List<List<ResultField>>> QueryAsync(IAmazonCloudWatchLogs logs, string logGroup, string query, DateTimeOffset start, DateTimeOffset end)
    {
        var started = await logs.StartQueryAsync(new StartQueryRequest
        {
            LogGroupNames = [logGroup],
            QueryString = query,
            StartTime = start.ToUnixTimeSeconds(),
            EndTime = end.ToUnixTimeSeconds(),
        });

        while (true)
        {
            await Task.Delay(1000);
            var result = await logs.GetQueryResultsAsync(new GetQueryResultsRequest { QueryId = started.QueryId });
            if (result.Status == QueryStatus.Complete) return result.Results ?? [];
            if (result.Status == QueryStatus.Failed || result.Status == QueryStatus.Cancelled || result.Status == QueryStatus.Timeout)
                throw new System.InvalidOperationException($"Logs Insights query {result.Status}");
        }
    }
}
