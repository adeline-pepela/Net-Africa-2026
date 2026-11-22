using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace AgentCore.Evaluations.Otel;

/// <summary>
/// An OpenTelemetry processor that keeps finished spans in memory, grouped by session.id, already
/// formatted the way AgentCore Evaluations expects. This lets you call the Evaluate API immediately
/// after a conversation (live demo, unit test, CI) instead of waiting minutes for CloudWatch ingestion.
/// </summary>
public sealed class SessionSpanRecorder : BaseProcessor<Activity>
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<JsonObject>> _spansBySession = new();

    public override void OnEnd(Activity activity)
    {
        if (activity.GetTagItem(GenAi.SessionId) is not string sessionId) return;

        var resource = ParentProvider?.GetResource() ?? Resource.Empty;
        var span = CloudWatchSpanFormatter.Format(activity, resource.Attributes);
        _spansBySession.GetOrAdd(sessionId, _ => new ConcurrentQueue<JsonObject>()).Enqueue(span);
    }

    /// <summary>All spans recorded for a session, ordered by start time.</summary>
    public IReadOnlyList<JsonObject> GetSessionSpans(string sessionId) =>
        _spansBySession.TryGetValue(sessionId, out var queue)
            ? queue.OrderBy(s => s["startTimeUnixNano"]!.GetValue<long>()).ToList()
            : [];

    public void Forget(string sessionId) => _spansBySession.TryRemove(sessionId, out _);
}
