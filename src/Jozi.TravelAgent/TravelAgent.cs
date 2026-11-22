using System.Diagnostics;
using AgentCore.Evaluations.Otel;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Jozi.TravelAgent;

public sealed record AgentTurn(string Answer, string TraceId, IReadOnlyList<string> ToolsCalled);

/// <summary>
/// A deliberately framework-free agent: a Bedrock Converse tool-use loop plus ~30 lines of OpenTelemetry.
/// Swap this for Semantic Kernel, Microsoft.Extensions.AI or your own loop; evaluation only needs the spans.
///
/// Span tree per user turn (one trace = one turn, all spans share session.id):
///   invoke_agent JoziTravelAgent
///     ├── chat us.anthropic.claude-...      (model decides to call a tool)
///     ├── execute_tool get_weather_forecast
///     └── chat us.anthropic.claude-...      (model writes the final answer)
/// </summary>
public sealed class TravelAgent
{
    private readonly IAmazonBedrockRuntime _bedrock;
    private readonly AgentConfig _config;
    private readonly List<Message> _history = [];
    private readonly List<Tool> _tools;

    public string SessionId { get; }

    public TravelAgent(IAmazonBedrockRuntime bedrock, AgentConfig config, string? sessionId = null)
    {
        _bedrock = bedrock;
        _config = config;
        SessionId = sessionId ?? $"jozi-{Guid.NewGuid():N}";
        _tools = TravelTools.All.Select(t => new Tool
        {
            ToolSpec = new ToolSpecification
            {
                Name = t.Name,
                Description = config.ToolDescriptions.GetValueOrDefault(t.Name, t.DefaultDescription),
                InputSchema = new ToolInputSchema { Json = DocumentJson.FromJson(t.InputSchemaJson) },
            },
        }).ToList();
    }

    public async Task<AgentTurn> AskAsync(string userInput, CancellationToken cancellationToken = default)
    {
        using var turn = GenAi.Source.StartActivity($"invoke_agent {_config.Name}", ActivityKind.Internal);
        Tag(turn, GenAi.OperationName, GenAi.OpInvokeAgent);
        Tag(turn, GenAi.AgentName, _config.Name);
        Tag(turn, GenAi.ProviderName, "aws.bedrock");
        Tag(turn, GenAi.RequestModel, _config.ModelId);
        Tag(turn, GenAi.TaskInput, userInput);
        Tag(turn, "agent.config.version", _config.Version);

        _history.Add(new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = userInput }] });
        var turnStart = _history.Count - 1;
        var toolsCalled = new List<string>();
        var answer = "Sorry, I couldn't finish that request.";

        try
        {
            for (var step = 0; step < _config.MaxSteps; step++)
            {
                var response = await ChatAsync(cancellationToken);
                var assistant = response.Output.Message;
                _history.Add(assistant);

                var toolUses = (assistant.Content ?? []).Where(c => c.ToolUse is not null).Select(c => c.ToolUse).ToList();
                if (toolUses.Count == 0)
                {
                    answer = string.Join("\n", (assistant.Content ?? []).Where(c => c.Text is not null).Select(c => c.Text));
                    break;
                }

                var results = new List<ContentBlock>();
                foreach (var toolUse in toolUses)
                {
                    results.Add(ExecuteTool(toolUse));
                    toolsCalled.Add(toolUse.Name);
                }
                _history.Add(new Message { Role = ConversationRole.User, Content = results });
            }
        }
        catch (Exception ex)
        {
            turn?.SetStatus(ActivityStatusCode.Error, ex.Message);
            turn?.AddException(ex);
            throw;
        }

        Tag(turn, GenAi.TaskOutput, answer);
        Tag(turn, GenAi.InputMessages, BedrockGenAiMessages.ToJson(_history.Take(turnStart + 1)));
        Tag(turn, GenAi.OutputMessages, BedrockGenAiMessages.ToJson([_history[^1]], "end_turn"));
        turn?.SetStatus(ActivityStatusCode.Ok);

        return new AgentTurn(answer, turn?.TraceId.ToHexString() ?? "", toolsCalled);
    }

    private async Task<ConverseResponse> ChatAsync(CancellationToken cancellationToken)
    {
        var request = new ConverseRequest
        {
            ModelId = _config.ModelId,
            System = [new SystemContentBlock { Text = _config.SystemPrompt }],
            Messages = [.. _history],
            ToolConfig = new ToolConfiguration { Tools = _tools },
            InferenceConfig = new InferenceConfiguration { MaxTokens = _config.MaxTokens },
        };
        if (_config.Temperature is { } temperature) request.InferenceConfig.Temperature = temperature;

        using var chat = GenAi.Source.StartActivity($"chat {_config.ModelId}", ActivityKind.Client);
        Tag(chat, GenAi.OperationName, GenAi.OpChat);
        Tag(chat, GenAi.ProviderName, "aws.bedrock");
        Tag(chat, GenAi.RequestModel, _config.ModelId);
        Tag(chat, GenAi.RequestMaxTokens, _config.MaxTokens);
        Tag(chat, GenAi.SystemInstructions, BedrockGenAiMessages.SystemInstructions(_config.SystemPrompt));
        Tag(chat, GenAi.ToolDefinitions, BedrockGenAiMessages.ToolDefinitions(_tools));
        Tag(chat, GenAi.InputMessages, BedrockGenAiMessages.ToJson(_history));

        var response = await _bedrock.ConverseAsync(request, cancellationToken);

        var finishReason = response.StopReason?.Value ?? "unknown";
        Tag(chat, GenAi.OutputMessages, BedrockGenAiMessages.ToJson([response.Output.Message], finishReason));
        chat?.SetTag(GenAi.ResponseFinishReasons, new[] { finishReason });
        Tag(chat, GenAi.ResponseModel, _config.ModelId);
        if (response.Usage is { } usage)
        {
            Tag(chat, GenAi.UsageInputTokens, usage.InputTokens ?? 0);
            Tag(chat, GenAi.UsageOutputTokens, usage.OutputTokens ?? 0);
        }
        chat?.SetStatus(ActivityStatusCode.Ok);
        return response;
    }

    private ContentBlock ExecuteTool(ToolUseBlock toolUse)
    {
        var arguments = DocumentJson.ToJson(toolUse.Input);

        using var span = GenAi.Source.StartActivity($"execute_tool {toolUse.Name}", ActivityKind.Internal);
        Tag(span, GenAi.OperationName, GenAi.OpExecuteTool);
        Tag(span, GenAi.ToolName, toolUse.Name);
        Tag(span, GenAi.ToolCallId, toolUse.ToolUseId);
        Tag(span, GenAi.ToolCallArguments, arguments);

        var (result, ok) = TravelTools.Execute(toolUse.Name, arguments);

        Tag(span, GenAi.ToolCallResult, result);
        span?.SetStatus(ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error);

        return new ContentBlock
        {
            ToolResult = new ToolResultBlock
            {
                ToolUseId = toolUse.ToolUseId,
                Content = [new ToolResultContentBlock { Text = result }],
                Status = ok ? ToolResultStatus.Success : ToolResultStatus.Error,
            },
        };
    }

    /// <summary>Every span gets session.id so AgentCore can stitch traces into a session.</summary>
    private void Tag(Activity? activity, string key, object value)
    {
        if (activity is null) return;
        if (activity.GetTagItem(GenAi.SessionId) is null)
        {
            activity.SetTag(GenAi.SessionId, SessionId);
            activity.SetTag(GenAi.ConversationId, SessionId);
        }
        activity.SetTag(key, value);
    }
}
