using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AgentCore.Evaluations.Otel;

/// <summary>
/// Turns a finished <see cref="Activity"/> into the JSON span record that CloudWatch Transaction Search
/// stores (and that AgentCore Evaluations reads): resource, scope, ids, timings, flat dotted attributes.
/// </summary>
public static class CloudWatchSpanFormatter
{
    public static JsonObject Format(Activity activity, IEnumerable<KeyValuePair<string, object>> resourceAttributes)
    {
        var start = ToUnixNanos(activity.StartTimeUtc);
        var end = start + activity.Duration.Ticks * 100L;

        var resource = new JsonObject();
        foreach (var (key, value) in resourceAttributes) resource[key] = ToJsonValue(value);

        var attributes = new JsonObject();
        foreach (var (key, value) in activity.TagObjects) attributes[key] = ToJsonValue(value);

        var span = new JsonObject
        {
            ["resource"] = new JsonObject { ["attributes"] = resource },
            ["scope"] = new JsonObject { ["name"] = activity.Source.Name, ["version"] = activity.Source.Version ?? "" },
            ["traceId"] = activity.TraceId.ToHexString(),
            ["spanId"] = activity.SpanId.ToHexString(),
            ["flags"] = 256,
            ["name"] = activity.DisplayName,
            ["kind"] = activity.Kind.ToString().ToUpperInvariant(),
            ["startTimeUnixNano"] = start,
            ["endTimeUnixNano"] = end,
            ["durationNano"] = end - start,
            ["attributes"] = attributes,
            ["status"] = new JsonObject
            {
                ["code"] = activity.Status switch
                {
                    ActivityStatusCode.Ok => "OK",
                    ActivityStatusCode.Error => "ERROR",
                    _ => "UNSET",
                },
            },
        };

        if (activity.ParentSpanId != default)
            span["parentSpanId"] = activity.ParentSpanId.ToHexString();

        return span;
    }

    public static long ToUnixNanos(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks * 100L;

    private static JsonNode? ToJsonValue(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        string[] arr => new JsonArray(arr.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}
