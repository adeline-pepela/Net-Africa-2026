using System.Text.Json;
using AgentCore.Evaluations.Otel;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Spectre.Console;

namespace Jozi.TravelAgent.Evals;

/// <summary>
/// DEMO 3 — simulated production traffic.
/// Runs realistic multi-turn conversations and exports every span to CloudWatch Logs. The online evaluation config
/// (infra/setup-online-eval.sh) samples completed sessions from that log group and scores them continuously.
/// </summary>
internal static class TrafficCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var configPath = options.Get("config", "config/agent.json");
        var promptsPath = options.Get("prompts", "evals/production-traffic.json");
        var sessions = options.GetInt("sessions", 10);
        var parallel = options.GetInt("parallel", 2);

        var config = AgentConfig.Load(configPath);
        var conversations = JsonSerializer.Deserialize<List<List<string>>>(File.ReadAllText(promptsPath))!;
        var telemetryOptions = new AgentTelemetryOptions
        {
            Export = options.Get("export") == "logs" ? SpanExport.CloudWatchLogs : SpanExport.CloudWatchOtlp,
            RecordInMemory = false,
            Environment = "production",
            ServiceName = options.Get("service-name", "dotnet-travel-agent"),
            LogGroupName = options.Get("log-group", "/aws/bedrock-agentcore/evals-demo/dotnet-travel-agent"),
        };

        AnsiConsole.Write(new Rule($"[bold]Production traffic · config v{Markup.Escape(config.Version)} · {Markup.Escape(config.ModelId)}[/]").LeftJustified());
        AnsiConsole.MarkupLine(telemetryOptions.Export == SpanExport.CloudWatchOtlp
            ? $"spans → [bold]CloudWatch OTLP endpoint[/] (Transaction Search, service.name = {Markup.Escape(telemetryOptions.ServiceName)})\n"
            : $"spans → [bold]{Markup.Escape(telemetryOptions.LogGroupName)}[/] (service.name = {Markup.Escape(telemetryOptions.ServiceName)})\n");

        using (var telemetry = AgentTelemetry.Start(telemetryOptions))
        {
            var bedrock = new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { RetryMode = RequestRetryMode.Adaptive, MaxErrorRetry = 8 });

            await Parallel.ForEachAsync(Enumerable.Range(1, sessions), new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (i, ct) =>
            {
                var conversation = conversations[Random.Shared.Next(conversations.Count)];
                var agent = new TravelAgent(bedrock, config);
                var tools = new List<string>();
                try
                {
                    foreach (var userTurn in conversation)
                    {
                        var turn = await agent.AskAsync(userTurn, ct);
                        tools.AddRange(turn.ToolsCalled);
                    }
                    AnsiConsole.MarkupLine($"  [green]●[/] session {i,3}  {Markup.Escape(agent.SessionId)}  {conversation.Count} turn(s)  tools: {Markup.Escape(tools.Count == 0 ? "none" : string.Join(", ", tools))}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [red]●[/] session {i,3}  {Markup.Escape(ex.Message)}");
                }
            });

            AnsiConsole.MarkupLine("\n[grey]Flushing spans to CloudWatch…[/]");
        }

        AnsiConsole.MarkupLine("[green]Done.[/] Online evaluation scores each session after it has been idle for the configured session timeout.");
        return 0;
    }
}
