using AgentCore.Evaluations.Otel;
using Spectre.Console;

namespace Jozi.Shared;

internal static class ScoreRendering
{
    public static string Colour(double? value) => value switch
    {
        null => "grey",
        >= 0.8 => "green",
        >= 0.5 => "yellow",
        _ => "red",
    };

    public static string Format(double? value) => value is { } v ? v.ToString("0.00") : "-";

    public static Table ScoreTable(IEnumerable<EvaluationScore> scores, int explanationLength = 150)
    {
        var table = new Table().RoundedBorder().Expand()
            .AddColumn("Evaluator")
            .AddColumn("Scored")
            .AddColumn(new TableColumn("Score").Centered())
            .AddColumn("Label")
            .AddColumn("Why (judge's explanation)");

        foreach (var s in scores.OrderBy(s => s.EvaluatorId))
        {
            var name = Markup.Escape(s.EvaluatorId.Replace("Builtin.", ""));
            if (s.Failed)
            {
                table.AddRow(name, s.Target, "[red]error[/]", "", $"[red]{Markup.Escape(Truncate($"{s.ErrorCode}: {s.ErrorMessage}", explanationLength))}[/]");
                continue;
            }
            table.AddRow(
                name,
                s.Target,
                $"[bold {Colour(s.Value)}]{Format(s.Value)}[/]",
                Markup.Escape(s.Label ?? ""),
                Markup.Escape(Truncate(s.Explanation ?? "", explanationLength)));
        }
        return table;
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
