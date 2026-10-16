using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DeployAngel.Core;

/// <summary>
/// Outcome is Ok (2xx), Retry (network error or 5xx), or Drop (any other
/// status, including 429 rate limiting, which the agent honors by pausing).
/// </summary>
internal sealed record TransportResult(TransportOutcome Outcome, int? Status = null, int? RetryAfter = null,
    JsonElement? Body = null)
{
    public bool Ok => Outcome == TransportOutcome.Ok;
}

internal enum TransportOutcome
{
    Ok,
    Retry,
    Drop,
}

internal interface ITransport
{
    /// <summary>body is gzipped JSON.</summary>
    TransportResult Post(string path, byte[] body, TimeSpan? timeout = null);
}

/// <summary>
/// Sends gzipped JSON to DeployAngel. Only ever called from the reporter
/// thread (or at shutdown), never from a request, so it sends synchronously.
/// </summary>
internal sealed class Transport : ITransport
{
    private readonly DeployAngelOptions _options;
    private readonly HttpClient _client;

    public Transport(DeployAngelOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        // The timeout is set per request; the client's own would cap it.
        _client = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>A payload encoded as gzipped JSON. Queued telemetry is kept this way: small while it waits.</summary>
    public static byte[] Encode(byte[] json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(json);
        return output.ToArray();
    }

    public TransportResult Post(string path, byte[] body, TimeSpan? timeout = null)
    {
        try
        {
            var endpoint = (_options.Endpoint ?? "").TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/{path.TrimStart('/')}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
            request.Headers.TryAddWithoutValidation("User-Agent", $"deployangel-dotnet/{AgentInfo.Version} dotnet/{Environment.Version}");
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");
            using var cancel = new System.Threading.CancellationTokenSource(timeout ?? _options.Timeout);
            using var response = _client.Send(request, cancel.Token);
            var status = (int)response.StatusCode;
            if (status >= 200 && status <= 299) return new TransportResult(TransportOutcome.Ok, status, Body: Parse(response));
            if (status >= 500 && status <= 599) return new TransportResult(TransportOutcome.Retry, status);
            int? retryAfter = null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter?.Delta is { } delta)
                retryAfter = (int)delta.TotalSeconds;
            return new TransportResult(TransportOutcome.Drop, status, retryAfter);
        }
        catch (Exception error)
        {
            _options.Log(LogLevel.Debug, $"DeployAngel transport error: {error.GetType().Name}: {error.Message}");
            return new TransportResult(TransportOutcome.Retry);
        }
    }

    private static JsonElement? Parse(HttpResponseMessage response)
    {
        try
        {
            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
