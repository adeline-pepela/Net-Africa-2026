using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;

namespace AgentCore.Evaluations.Otel;

/// <summary>
/// Signs outgoing HTTP requests with AWS Signature Version 4. Plugged into the standard OpenTelemetry OTLP exporter,
/// it lets a .NET app send spans straight to the CloudWatch OTLP endpoint (https://xray.{region}.amazonaws.com/v1/traces)
/// with no collector or sidecar. Spans then land in CloudWatch Transaction Search, which is where online evaluation
/// discovers sessions.
/// </summary>
public sealed class SigV4Handler : DelegatingHandler
{
    private readonly string _region;
    private readonly string _service;
    private readonly AWSCredentials _credentials;

    public SigV4Handler(string region, string service = "xray", AWSCredentials? credentials = null)
        : base(new HttpClientHandler())
    {
        _region = region;
        _service = service;
        _credentials = credentials ?? DefaultAWSCredentialsIdentityResolver.GetCredentials();
    }

    // The OTLP exporter calls HttpClient.Send synchronously, so both paths must sign.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sign(request, _credentials.GetCredentials());
        return base.Send(request, cancellationToken);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sign(request, await _credentials.GetCredentialsAsync());
        return await base.SendAsync(request, cancellationToken);
    }

    private void Sign(HttpRequestMessage request, ImmutableCredentials credentials)
    {
        // Buffer the body so it can be hashed and then re-sent unchanged.
        byte[] body = [];
        MediaTypeHeaderValue? contentType = null;
        if (request.Content is not null)
        {
            contentType = request.Content.Headers.ContentType;
            using var stream = request.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            body = buffer.ToArray();
            request.Content = new ByteArrayContent(body);
            if (contentType is not null) request.Content.Headers.ContentType = contentType;
        }

        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToString("yyyyMMdd");
        var payloadHash = Hex(SHA256.HashData(body));
        var host = request.RequestUri!.Host;

        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Remove("x-amz-security-token");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var canonicalHeaders = new StringBuilder()
            .Append("host:").Append(host).Append('\n')
            .Append("x-amz-content-sha256:").Append(payloadHash).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n');
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        if (credentials.UseToken)
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.Token);
            canonicalHeaders.Append("x-amz-security-token:").Append(credentials.Token).Append('\n');
            signedHeaders += ";x-amz-security-token";
        }

        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            request.RequestUri.Query.TrimStart('?'),
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{_region}/{_service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), dateStamp);
        key = Hmac(key, _region);
        key = Hmac(key, _service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={credentials.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
