// DEMO 1 — chat with the agent, then score the session with AgentCore built-in evaluators.
//
//   dotnet run --project src/Jozi.TravelAgent.Chat
//   dotnet run --project src/Jozi.TravelAgent.Chat -- --config config/variants/agent.regressed.json
//
// Commands:  /eval   score this session      /spans  dump the spans that get sent
//            /new    start a new session     /exit

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Evaluations.Otel;
using Amazon.BedrockRuntime;
using Jozi.Shared;
using Jozi.TravelAgent;
using Spectre.Console;

var configPath = Arg("--config") ?? "config/agent.json";
var export = Arg("--export") switch
{
    "cloudwatch" => SpanExport.CloudWatchOtlp,
    "logs" => SpanExport.CloudWatchLogs,
    "otlp" => SpanExport.Otlp,
    _ => SpanExport.None,
};
string[] evaluators = Arg("--evaluators")?.Split(',') ??
[
    "Builtin.GoalSuccessRate",
    "Builtin.Correctness",
    "Builtin.Helpfulness",
    "Builtin.ToolSelectionAccuracy",
    "Builtin.ToolParameterAccuracy",
];

var config = AgentConfig.Load(configPath);
using var telemetry = AgentTelemetry.Start(new AgentTelemetryOptions { Export = export, Environment = "demo" });
var bedrock = new AmazonBedrockRuntimeClient();
var evaluator = AgentCoreEvaluator.Create();
var agent = new TravelAgent(bedrock, config);

AnsiConsole.Write(new FigletText("Jozi").Color(Color.Gold1));
AnsiConsole.MarkupLine($"[grey]Travel agent for .NET conference attendees · model[/] [bold]{config.ModelId}[/] [grey]· config[/] {Markup.Escape(configPath)} [grey](v{config.Version})[/]");
AnsiConsole.MarkupLine($"[grey]Spans scope:[/] [bold]{GenAi.ScopeName}[/]  [grey]export:[/] {export}");
AnsiConsole.MarkupLine("[grey]Commands: /eval  /spans  /new  /exit[/]");
PrintSession();

while (true)
{
    var input = ReadInput();
    if (input.Length == 0) continue;

    switch (input)
    {
        case "/exit" or "/quit":
            return;
        case "/new":
            agent = new TravelAgent(bedrock, config);
            PrintSession();
            continue;
        case "/spans":
            DumpSpans();
            continue;
        case "/eval":
            await EvaluateSessionAsync();
            continue;
    }

    try
    {
        var turn = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("thinking…", _ => agent.AskAsync(input));
        if (turn.ToolsCalled.Count > 0)
            AnsiConsole.MarkupLine($"[grey]  tools → {Markup.Escape(string.Join(", ", turn.ToolsCalled))}[/]");
        AnsiConsole.MarkupLine($"[bold gold1]jozi>[/] {Markup.Escape(turn.Answer)}");
        AnsiConsole.WriteLine();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    }
}

async Task EvaluateSessionAsync()
{
    var spans = telemetry.Recorder!.GetSessionSpans(agent.SessionId);
    if (spans.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]Nothing to evaluate yet. Ask Jozi something first.[/]");
        return;
    }

    var toolCalls = SpanQueries.ToolsCalled(spans).Count;
    var toRun = evaluators.Where(e => toolCalls > 0 || !e.Contains("Tool", StringComparison.Ordinal)).ToList();

    var results = await AnsiConsole.Status().Spinner(Spinner.Known.Star).StartAsync(
        $"Scoring {spans.Count} spans with {toRun.Count} evaluators…",
        async _ => (await Task.WhenAll(toRun.Select(async id =>
        {
            try { return await evaluator.EvaluateAsync(id, spans); }
            catch (Exception ex) { return new[] { new EvaluationScore(id, null, null, null, agent.SessionId, null, null, ex.GetType().Name, ex.Message) }; }
        }))).SelectMany(r => r).ToList());

    AnsiConsole.Write(new Rule($"[bold]AgentCore Evaluations · session {agent.SessionId}[/]").LeftJustified());
    AnsiConsole.Write(ScoreRendering.ScoreTable(results, explanationLength: 110));

    var worst = results.Where(r => !r.Failed && r.Value is not null).MinBy(r => r.Value);
    if (worst is not null && worst.Value < 0.8)
    {
        AnsiConsole.Write(new Panel(Markup.Escape(worst.Explanation ?? ""))
            .Header($"[red]Lowest score: {Markup.Escape(worst.EvaluatorId)} = {ScoreRendering.Format(worst.Value)}[/]")
            .BorderColor(Color.Red));
    }
    AnsiConsole.WriteLine();
}

void DumpSpans()
{
    var spans = telemetry.Recorder!.GetSessionSpans(agent.SessionId);
    Directory.CreateDirectory("artifacts");
    var path = Path.Combine("artifacts", $"{agent.SessionId}.spans.json");
    File.WriteAllText(path, new JsonArray(spans.Select(s => (JsonNode?)s.DeepClone()).ToArray())
        .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var table = new Table().RoundedBorder().AddColumn("Span").AddColumn("gen_ai.operation.name").AddColumn("trace").AddColumn("scope");
    foreach (var s in spans)
    {
        table.AddRow(
            Markup.Escape(s["name"]!.GetValue<string>()),
            Markup.Escape(s["attributes"]?[GenAi.OperationName]?.GetValue<string>() ?? ""),
            s["traceId"]!.GetValue<string>()[..8],
            Markup.Escape(s["scope"]!["name"]!.GetValue<string>()));
    }
    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"[grey]{spans.Count} spans written to[/] {Markup.Escape(path)}");
}

// Interactive prompt on stage; plain stdin when input is piped (rehearsal scripts: `cat demo1.txt | dotnet run ...`).
string ReadInput()
{
    if (!Console.IsInputRedirected)
        return AnsiConsole.Prompt(new TextPrompt<string>("[bold green]you>[/]").AllowEmpty()).Trim();

    AnsiConsole.Markup("[bold green]you>[/] ");
    var line = Console.ReadLine();
    AnsiConsole.WriteLine(line ?? "/exit");
    return line?.Trim() ?? "/exit";
}

void PrintSession() =>
    AnsiConsole.MarkupLine($"[grey]session.id =[/] [bold]{agent.SessionId}[/]\n");

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
