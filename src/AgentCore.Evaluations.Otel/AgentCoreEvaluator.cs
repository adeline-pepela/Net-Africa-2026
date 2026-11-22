using System.Text.Json.Nodes;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime;
using EvalContext = Amazon.BedrockAgentCore.Model.Context;

namespace AgentCore.Evaluations.Otel;

/// <summary>Optional ground truth for a session. Evaluators ignore the fields they don't use.</summary>
/// <param name="ExpectedResponse">Expected answer for the last turn. Used by Builtin.Correctness.</param>
/// <param name="Assertions">Behaviours the session should show. Used by Builtin.GoalSuccessRate.</param>
/// <param name="ExpectedTools">Expected tool sequence. Used by Builtin.Trajectory* evaluators.</param>
public sealed record ReferenceData(
    string? ExpectedResponse = null,
    IReadOnlyList<string>? Assertions = null,
    IReadOnlyList<string>? ExpectedTools = null);

public sealed record EvaluationScore(
    string EvaluatorId,
    double? Value,
    string? Label,
    string? Explanation,
    string? SessionId,
    string? TraceId,
    string? SpanId,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Failed => ErrorCode is not null || ErrorMessage is not null;

    /// <summary>What was scored: the session, one trace (user turn) or one span (tool call).</summary>
    public string Target => SpanId is not null ? $"tool span {SpanId[..8]}" : TraceId is not null ? $"turn {TraceId[..8]}" : "session";
}

/// <summary>Thin wrapper over the AgentCore Evaluate API (on-demand, synchronous LLM-as-a-judge scoring).</summary>
public sealed class AgentCoreEvaluator(IAmazonBedrockAgentCore client)
{
    public static AgentCoreEvaluator Create() => new(new AmazonBedrockAgentCoreClient(new AmazonBedrockAgentCoreConfig
    {
        RetryMode = RequestRetryMode.Adaptive,
        MaxErrorRetry = 8,
    }));

    public async Task<IReadOnlyList<EvaluationScore>> EvaluateAsync(
        string evaluatorId,
        IReadOnlyList<JsonObject> sessionSpans,
        ReferenceData? reference = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionSpans.Count == 0) throw new ArgumentException("No spans to evaluate.", nameof(sessionSpans));

        var request = new EvaluateRequest
        {
            EvaluatorId = evaluatorId,
            EvaluationInput = new EvaluationInput
            {
                SessionSpans = sessionSpans.Select(s => DocumentJson.FromNode(s)).ToList(),
            },
        };

        if (reference is not null)
            request.EvaluationReferenceInputs = BuildReferenceInputs(sessionSpans, reference);

        var response = await client.EvaluateAsync(request, cancellationToken);

        return (response.EvaluationResults ?? []).Select(r => new EvaluationScore(
            r.EvaluatorId,
            r.Value,
            r.Label,
            r.Explanation,
            r.Context?.SpanContext?.SessionId,
            r.Context?.SpanContext?.TraceId,
            r.Context?.SpanContext?.SpanId,
            r.ErrorCode,
            r.ErrorMessage)).ToList();
    }

    private static List<EvaluationReferenceInput> BuildReferenceInputs(IReadOnlyList<JsonObject> spans, ReferenceData reference)
    {
        var sessionId = SpanQueries.SessionId(spans);
        var inputs = new List<EvaluationReferenceInput>();

        // Session-level ground truth: assertions + expected tool trajectory.
        if (reference.Assertions is { Count: > 0 } || reference.ExpectedTools is not null)
        {
            var sessionLevel = new EvaluationReferenceInput
            {
                Context = new EvalContext { SpanContext = new SpanContext { SessionId = sessionId } },
            };
            if (reference.Assertions is { Count: > 0 })
                sessionLevel.Assertions = reference.Assertions.Select(a => new EvaluationContent { Text = a }).ToList();
            if (reference.ExpectedTools is not null)
                sessionLevel.ExpectedTrajectory = new EvaluationExpectedTrajectory { ToolNames = reference.ExpectedTools.ToList() };
            inputs.Add(sessionLevel);
        }

        // Trace-level ground truth: the expected answer to the final user turn.
        if (!string.IsNullOrWhiteSpace(reference.ExpectedResponse))
        {
            inputs.Add(new EvaluationReferenceInput
            {
                Context = new EvalContext
                {
                    SpanContext = new SpanContext { SessionId = sessionId, TraceId = SpanQueries.LastTurnTraceId(spans) },
                },
                ExpectedResponse = new EvaluationContent { Text = reference.ExpectedResponse },
            });
        }

        return inputs;
    }
}

public static class SpanQueries
{
    public static string SessionId(IReadOnlyList<JsonObject> spans) =>
        spans.Select(s => s["attributes"]?[GenAi.SessionId]?.GetValue<string>()).FirstOrDefault(id => id is not null)
        ?? throw new InvalidOperationException("Spans carry no session.id attribute.");

    public static string LastTurnTraceId(IReadOnlyList<JsonObject> spans) =>
        spans.Where(s => s["attributes"]?[GenAi.OperationName]?.GetValue<string>() == GenAi.OpInvokeAgent)
             .OrderBy(s => s["startTimeUnixNano"]!.GetValue<long>())
             .Select(s => s["traceId"]!.GetValue<string>())
             .LastOrDefault()
        ?? throw new InvalidOperationException("No invoke_agent span found.");

    public static IReadOnlyList<string> ToolsCalled(IReadOnlyList<JsonObject> spans) =>
        spans.Where(s => s["attributes"]?[GenAi.OperationName]?.GetValue<string>() == GenAi.OpExecuteTool)
             .OrderBy(s => s["startTimeUnixNano"]!.GetValue<long>())
             .Select(s => s["attributes"]![GenAi.ToolName]!.GetValue<string>())
             .ToList();
}
