// jozi-evals: the quality tooling around the agent.
//
//   gate     DEMO 2  Run the golden dataset, score every session, fail (exit 1) if any evaluator drops below its threshold.
//   traffic  DEMO 3  Simulate production users. Spans go to CloudWatch Logs where online evaluation picks them up.
//   drift    DEMO 3  Compare online-evaluation scores for a recent window against a baseline window (exit 2 on drift).

using Jozi.TravelAgent.Evals;
using Spectre.Console;

var command = args.FirstOrDefault();
var options = CliOptions.Parse(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "gate" => await GateCommand.RunAsync(options),
        "traffic" => await TrafficCommand.RunAsync(options),
        "drift" => await DriftCommand.RunAsync(options),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    return 3;
}

static int Usage()
{
    AnsiConsole.MarkupLine("""
        [bold]jozi-evals[/] <command> [[options]]

          [bold]gate[/]     --config config/agent.json --dataset evals/golden-dataset.json [[--report eval-report.md]] [[--parallel 3]]
          [bold]traffic[/]  --config config/agent.json --prompts evals/production-traffic.json [[--sessions 10]] [[--log-group ...]] [[--service-name ...]]
          [bold]drift[/]    --results-log-group <online eval results log group> [[--recent-minutes 30]] [[--baseline-hours 24]] [[--max-drop 0.15]]
        """);
    return 1;
}
