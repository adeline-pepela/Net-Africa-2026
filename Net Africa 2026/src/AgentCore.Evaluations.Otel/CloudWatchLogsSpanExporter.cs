using System.Diagnostics;
using System.Text;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace AgentCore.Evaluations.Otel;

/// <summary>
/// Exports spans straight into a CloudWatch Logs log group (one JSON record per span) using the AWS SDK.
/// Online and batch evaluations read spans from that log group by service.name.
///
/// This keeps the demo to a single process with no collector or sidecar. For production, the documented path is
/// OTLP to the CloudWatch OTLP endpoint (xray.{region}.amazonaws.com/v1/traces) via the ADOT SDK or an
/// OpenTelemetry Collector, which also feeds the CloudWatch GenAI Observability dashboard. See infra/otel-collector.yaml.
/// </summary>
public sealed class CloudWatchLogsSpanExporter(IAmazonCloudWatchLogs logs, string logGroupName, string logStreamName)
    : BaseExporter<Activity>
{
    private const int MaxBatchBytes = 900_000; // PutLogEvents limit is 1 MB including 26 bytes overhead per event.
    private bool _destinationReady;

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var resource = ParentProvider?.GetResource() ?? Resource.Empty;
        var events = new List<InputLogEvent>();
        foreach (var activity in batch)
        {
            events.Add(new InputLogEvent
            {
                Timestamp = activity.StartTimeUtc,
                Message = CloudWatchSpanFormatter.Format(activity, resource.Attributes).ToJsonString(),
            });
        }

        try
        {
            ExportAsync(events).GetAwaiter().GetResult();
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CloudWatchLogsSpanExporter] export failed: {ex.Message}");
            return ExportResult.Failure;
        }
    }

    private async Task ExportAsync(List<InputLogEvent> events)
    {
        if (events.Count == 0) return;
        await EnsureDestinationAsync();

        // PutLogEvents requires chronological order and at most ~1 MB per call.
        var chunk = new List<InputLogEvent>();
        var chunkBytes = 0;
        foreach (var e in events.OrderBy(e => e.Timestamp))
        {
            var size = Encoding.UTF8.GetByteCount(e.Message) + 26;
            if (chunk.Count > 0 && chunkBytes + size > MaxBatchBytes)
            {
                await PutAsync(chunk);
                chunk = [];
                chunkBytes = 0;
            }
            chunk.Add(e);
            chunkBytes += size;
        }
        if (chunk.Count > 0) await PutAsync(chunk);
    }

    private Task PutAsync(List<InputLogEvent> chunk) => logs.PutLogEventsAsync(new PutLogEventsRequest
    {
        LogGroupName = logGroupName,
        LogStreamName = logStreamName,
        LogEvents = chunk,
    });

    private async Task EnsureDestinationAsync()
    {
        if (_destinationReady) return;
        try { await logs.CreateLogGroupAsync(new CreateLogGroupRequest { LogGroupName = logGroupName }); }
        catch (ResourceAlreadyExistsException) { }
        try { await logs.CreateLogStreamAsync(new CreateLogStreamRequest { LogGroupName = logGroupName, LogStreamName = logStreamName }); }
        catch (ResourceAlreadyExistsException) { }
        _destinationReady = true;
    }
}
