using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.Tests;

internal static class UpdateFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Updates", name));
}

internal sealed class TemporaryUpdateRoot : IDisposable
{
    public TemporaryUpdateRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "bt-updates", Guid.NewGuid().ToString("N"));
        Paths = new UpdatePaths(Root);
        Paths.EnsureCreated();
        Directory.CreateDirectory(Paths.ServiceFolder);
    }

    public string Root { get; }

    public UpdatePaths Paths { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class RecordedReleaseClient(ReleaseLookup lookup) : IReleaseClient
{
    public int Calls { get; private set; }

    public Task<ReleaseLookup> LatestAsync(CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult(lookup);
    }
}

internal sealed record RecordedExchange(string Url, string? IfNoneMatch);

internal sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers)
    : HttpMessageHandler
{
    private int _index;

    public List<RecordedExchange> Exchanges { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Exchanges.Add(new RecordedExchange(
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.TryGetValues("If-None-Match", out var tags) ? string.Join(",", tags) : null));

        var answer = answers[Math.Min(_index, answers.Length - 1)];
        _index++;

        return Task.FromResult(answer(request));
    }

    public static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }

    public static HttpResponseMessage NotModified() => new(HttpStatusCode.NotModified);

    public static HttpResponseMessage RateLimited(long resetEpochSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation(
            "x-ratelimit-reset",
            resetEpochSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return response;
    }

    public static HttpResponseMessage Bytes(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    public static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}
