using System.Diagnostics;

namespace AgentCore.Evaluations.Otel;

/// <summary>
/// OpenTelemetry GenAI semantic-convention names used by AgentCore Evaluations.
/// See https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/supported-frameworks-generic.html
/// </summary>
public static class GenAi
{
    /// <summary>
    /// THE most important line in this repo.
    /// AgentCore Evaluations routes spans by instrumentation scope name. A scope that does not start with
    /// "opentelemetry.instrumentation." (or "openinference.instrumentation.") is ignored, even if every
    /// attribute is perfect. In .NET the ActivitySource name becomes the scope name.
    /// Try renaming this to "MyCompany.Agent" and watch Evaluate return a ValidationException.
    /// </summary>
    public const string ScopeName = "opentelemetry.instrumentation.genai.dotnet";

    public static readonly ActivitySource Source = new(ScopeName, "1.0.0");

    // Span classification: invoke_agent (one per user turn), chat (one per model call), execute_tool (one per tool call).
    public const string OperationName = "gen_ai.operation.name";
    public const string OpInvokeAgent = "invoke_agent";
    public const string OpChat = "chat";
    public const string OpExecuteTool = "execute_tool";

    // Session grouping. Every span in a conversation must carry the same session.id.
    public const string SessionId = "session.id";
    public const string ConversationId = "gen_ai.conversation.id";

    public const string ProviderName = "gen_ai.provider.name";
    public const string AgentName = "gen_ai.agent.name";
    public const string RequestModel = "gen_ai.request.model";
    public const string RequestMaxTokens = "gen_ai.request.max_tokens";
    public const string ResponseModel = "gen_ai.response.model";
    public const string ResponseFinishReasons = "gen_ai.response.finish_reasons";
    public const string UsageInputTokens = "gen_ai.usage.input_tokens";
    public const string UsageOutputTokens = "gen_ai.usage.output_tokens";

    // Content. Kept ON the span ("unified telemetry"), so a single log group holds everything evaluators need.
    public const string SystemInstructions = "gen_ai.system_instructions";
    public const string ToolDefinitions = "gen_ai.tool.definitions";
    public const string InputMessages = "gen_ai.input.messages";
    public const string OutputMessages = "gen_ai.output.messages";
    public const string TaskInput = "gen_ai.task.input";   // user prompt on the invoke_agent span
    public const string TaskOutput = "gen_ai.task.output"; // final answer on the invoke_agent span

    // Tool calls.
    public const string ToolName = "gen_ai.tool.name";
    public const string ToolCallId = "gen_ai.tool.call.id";
    public const string ToolCallArguments = "gen_ai.tool.call.arguments";
    public const string ToolCallResult = "gen_ai.tool.call.result";
}
