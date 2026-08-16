using System.Net.Http;
using System.Text;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A single round-trip completion and a single embedding, against a stub that
/// speaks the runtime's API. This exercises the real request shape, the real
/// parsing and the real failure path; only the model behind it is stubbed.
/// </summary>
public sealed class InferenceEndpointTests
{
    private static byte[] Json(string body) => Encoding.UTF8.GetBytes(body);

    [Fact]
    public async Task ACompletionRoundTripReturnsTheGeneratedText()
    {
        using var server = new StubHttpServer((path, body) => path switch
        {
            "/completion" => (200, "application/json", Json("""{"content":" pracovni prostor "}""")),
            _ => (404, "text/plain", Json("no")),
        });

        using var client = server.CreateClient();
        var endpoint = new InferenceEndpoint(client);

        var result = await endpoint.CompleteAsync("Translate: workspace", 0.2, CancellationToken.None);

        result.Should().Be("pracovni prostor", "the result is trimmed");
        server.Requests.Should().Contain("POST /completion");
    }

    [Fact]
    public async Task TheCompletionRequestCarriesThePromptAndTemperature()
    {
        string? seen = null;

        using var server = new StubHttpServer((path, body) =>
        {
            seen = body;
            return (200, "application/json", Json("""{"content":"ok"}"""));
        });

        using var client = server.CreateClient();
        await new InferenceEndpoint(client).CompleteAsync("keep product names in English", 0.35, CancellationToken.None);

        seen.Should().NotBeNull();
        seen.Should().Contain("keep product names in English");
        seen.Should().Contain("0.35");
        seen.Should().Contain("\"stream\":false", "the app reads one response rather than a stream");
    }

    [Fact]
    public async Task AnEmbeddingRoundTripReturnsTheVector()
    {
        using var server = new StubHttpServer((path, body) => path switch
        {
            "/embedding" => (200, "application/json", Json("""{"embedding":[0.25,-0.5,0.75]}""")),
            _ => (404, "text/plain", Json("no")),
        });

        using var client = server.CreateClient();
        var vector = await new InferenceEndpoint(client).EmbedAsync("workspace", CancellationToken.None);

        vector.Should().Equal(0.25f, -0.5f, 0.75f);
        server.Requests.Should().Contain("POST /embedding");
    }

    [Fact]
    public async Task AnUnreachableRuntimeRaisesTheUnavailableError()
    {
        // Nothing is listening on this port.
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:1"),
            Timeout = TimeSpan.FromSeconds(5),
        };

        var act = () => new InferenceEndpoint(client).CompleteAsync("workspace", 0.2, CancellationToken.None);

        await act.Should().ThrowAsync<InferenceUnavailableException>();
    }

    [Fact]
    public async Task ARefusedCallRaisesTheUnavailableErrorWithItsStatus()
    {
        using var server = new StubHttpServer((path, body) => (503, "text/plain", Json("busy")));
        using var client = server.CreateClient();

        var act = () => new InferenceEndpoint(client).CompleteAsync("workspace", 0.2, CancellationToken.None);

        (await act.Should().ThrowAsync<InferenceUnavailableException>())
            .Which.Message.Should().Contain("503");
    }

    [Fact]
    public async Task ReachabilityIsFalseRatherThanThrowingWhenNothingAnswers()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:1"),
            Timeout = TimeSpan.FromSeconds(3),
        };

        var reachable = await new InferenceEndpoint(client).IsReachableAsync(CancellationToken.None);

        reachable.Should().BeFalse();
    }

    [Fact]
    public async Task ReachabilityIsTrueWhenHealthAnswers()
    {
        using var server = new StubHttpServer((path, body) => (200, "application/json", Json("""{"status":"ok"}""")));
        using var client = server.CreateClient();

        var reachable = await new InferenceEndpoint(client).IsReachableAsync(CancellationToken.None);

        reachable.Should().BeTrue();
        server.Requests.Should().Contain("GET /health");
    }
}
