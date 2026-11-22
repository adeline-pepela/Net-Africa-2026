using Amazon.CloudWatchLogs;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentCore.Evaluations.Otel;

public enum SpanExport
{
    /// <summary>Keep spans in memory only (on-demand evaluation, CI).</summary>
    None,
    /// <summary>
    /// OTLP straight to the CloudWatch OTLP endpoint (xray.{region}.amazonaws.com/v1/traces), SigV4-signed in-process.
    /// The documented ingestion path: spans land in Transaction Search (aws/spans), where online evaluation finds sessions.
    /// </summary>
    CloudWatchOtlp,
    /// <summary>
    /// Write span records directly into a CloudWatch Logs log group with the AWS SDK. Batch evaluation (with explicit
    /// session IDs) reads these, but online evaluation does NOT discover them. Use CloudWatchOtlp for online evaluation.
    /// </summary>
    CloudWatchLogs,
    /// <summary>Standard OTLP exporter, configured with OTEL_EXPORTER_OTLP_* (point it at an ADOT/OTel collector).</summary>
    Otlp,
}

public sealed record AgentTelemetryOptions
{
    public string ServiceName { get; init; } = "dotnet-travel-agent";
    public string Environment { get; init; } = "dev";
    public SpanExport Export { get; init; } = SpanExport.None;
    public string LogGroupName { get; init; } = "/aws/bedrock-agentcore/evals-demo/dotnet-travel-agent";
    public string LogStreamName { get; init; } = "spans";
    public bool RecordInMemory { get; init; } = true;
    public string Region { get; init; } = System.Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";
}

/// <summary>Everything an app needs: a tracer provider plus (optionally) an in-memory recorder for on-demand evals.</summary>
public sealed class AgentTelemetry : IDisposable
{
    private readonly TracerProvider _tracerProvider;

    public SessionSpanRecorder? Recorder { get; }
    public AgentTelemetryOptions Options { get; }

    private AgentTelemetry(TracerProvider tracerProvider, SessionSpanRecorder? recorder, AgentTelemetryOptions options)
    {
        _tracerProvider = tracerProvider;
        Recorder = recorder;
        Options = options;
    }

    public static AgentTelemetry Start(AgentTelemetryOptions options)
    {
        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(options.ServiceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = options.Environment,
                    // Online evaluation only discovers sessions whose resource says it's a GenAI agent.
                    // ADOT (AWS's OTel distro, used by Python/JS agents) sets this for you; plain .NET OpenTelemetry does not.
                    // Without it the online config polls every 5 minutes, matches nothing, and never reports an error.
                    ["aws.service.type"] = "gen_ai_agent",
                    ["aws.log.group.names"] = options.LogGroupName,
                }))
            .AddSource(GenAi.ScopeName);

        SessionSpanRecorder? recorder = null;
        if (options.RecordInMemory)
        {
            recorder = new SessionSpanRecorder();
            builder.AddProcessor(recorder);
        }

        switch (options.Export)
        {
            case SpanExport.CloudWatchLogs:
                builder.AddProcessor(new BatchActivityExportProcessor(
                    new CloudWatchLogsSpanExporter(new AmazonCloudWatchLogsClient(), options.LogGroupName, options.LogStreamName),
                    scheduledDelayMilliseconds: 2000));
                break;
            case SpanExport.CloudWatchOtlp:
                builder.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri($"https://xray.{options.Region}.amazonaws.com/v1/traces");
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.HttpClientFactory = () => new HttpClient(new SigV4Handler(options.Region, "xray"));
                    otlp.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 2000;
                });
                break;
            case SpanExport.Otlp:
                builder.AddOtlpExporter();
                break;
        }

        return new AgentTelemetry(builder.Build()!, recorder, options);
    }

    public void Flush() => _tracerProvider.ForceFlush();

    public void Dispose()
    {
        _tracerProvider.ForceFlush();
        _tracerProvider.Dispose();
    }
}
