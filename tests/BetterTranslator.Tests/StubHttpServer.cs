using System.Net.Http;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BetterTranslator.Tests;

/// <summary>
/// A minimal HTTP server on an ephemeral loopback port. Built on TcpListener
/// rather than HttpListener so it needs no URL reservation, which would make
/// the test suite depend on an elevated prompt.
/// </summary>
internal sealed record StubRequest(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Start offset of a "bytes=N-" Range header, or null when absent.</summary>
    public long? RangeFrom =>
        Headers.TryGetValue("range", out var raw) && raw.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) &&
        long.TryParse(raw["bytes=".Length..].Split('-')[0], out var from)
            ? from
            : null;
}

/// <summary>
/// A response the test controls completely, including the case that matters
/// most here: a Content-Length that disagrees with the bytes actually sent,
/// which is what a truncated transfer looks like on the wire.
/// </summary>
internal sealed record StubResponse
{
    public int Status { get; init; } = 200;

    public string ContentType { get; init; } = "application/octet-stream";

    public byte[] Body { get; init; } = [];

    /// <summary>Advertised length. Defaults to the real one; set it higher to truncate.</summary>
    public long? DeclaredLength { get; init; }

    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>Pause before the body, to trip a stall timeout.</summary>
    public TimeSpan Delay { get; init; }
}

internal sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<StubRequest, StubResponse> _handler;
    private readonly Task _loop;

    public StubHttpServer(Func<string, string, (int Status, string ContentType, byte[] Body)> handler)
        : this(r =>
        {
            var (status, contentType, body) = handler(r.Path, r.Body);
            return new StubResponse { Status = status, ContentType = contentType, Body = body };
        })
    {
    }

    public StubHttpServer(Func<StubRequest, StubResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseAddress = new Uri($"http://127.0.0.1:{Port}");

        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public Uri BaseAddress { get; }

    /// <summary>Requests seen so far, as "METHOD path".</summary>
    public List<string> Requests { get; } = [];

    public HttpClient CreateClient() => new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down; a cancelled accept is expected.
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine))
            {
                return;
            }

            var parts = requestLine.Split(' ');
            var method = parts[0];
            var path = parts.Length > 1 ? parts[1] : "/";

            var contentLength = 0;
            var chunked = false;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;

            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                }
                else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    chunked = true;
                }
            }

            // HttpClient sends a JsonContent body chunked, because it does not
            // know its length up front, so both framings have to be handled.
            var body = chunked
                ? await ReadChunkedAsync(reader)
                : await ReadFixedAsync(reader, contentLength);

            lock (Requests)
            {
                Requests.Add($"{method} {path}");
            }

            var response = _handler(new StubRequest(method, path, body, headers));

            const string Crlf = "\r\n";

            var extra = string.Concat(response.Headers.Select(h => h.Key + ": " + h.Value + Crlf));

            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + response.Status + (response.Status is 200 or 206 ? " OK" : " Error") + Crlf +
                "Content-Type: " + response.ContentType + Crlf +
                "Content-Length: " + (response.DeclaredLength ?? response.Body.Length) + Crlf +
                extra +
                "Connection: close" + Crlf + Crlf);

            await stream.WriteAsync(head);

            if (response.Delay > TimeSpan.Zero)
            {
                await Task.Delay(response.Delay, _stopping.Token);
            }

            await stream.WriteAsync(response.Body);
            await stream.FlushAsync();
        }
    }

    private static async Task<string> ReadFixedAsync(StreamReader reader, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length];
        var read = 0;

        while (read < length)
        {
            var got = await reader.ReadAsync(buffer.AsMemory(read, length - read));
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return new string(buffer, 0, read);
    }

    private static async Task<string> ReadChunkedAsync(StreamReader reader)
    {
        var body = new StringBuilder();

        while (true)
        {
            var sizeLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(sizeLine))
            {
                break;
            }

            // A chunk header may carry extensions after a semicolon.
            var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                break;
            }

            body.Append(await ReadFixedAsync(reader, size));

            // Trailing CRLF after each chunk.
            await reader.ReadLineAsync();
        }

        return body.ToString();
    }
}
