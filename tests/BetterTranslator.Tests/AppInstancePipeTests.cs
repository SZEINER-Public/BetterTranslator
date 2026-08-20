using System.Text;
using BetterTranslator.Updates.Ipc;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class AppInstancePipeTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("ping", AppInstanceVerb.Ping)]
    [InlineData("notify", AppInstanceVerb.Notify)]
    [InlineData("activate", AppInstanceVerb.Activate)]
    [InlineData("close", AppInstanceVerb.Close)]
    public void The_four_verbs_are_accepted(string name, AppInstanceVerb expected)
    {
        AppInstanceProtocol.TryParseRequest(Utf8($"{{\"verb\":\"{name}\"}}"), out var request, out _)
            .Should().BeTrue();

        request.Verb.Should().Be(expected);
    }

    [Theory]
    [InlineData("{\"verb\":\"shutdown\"}")]
    [InlineData("{\"verb\":\"PING\"}")]
    [InlineData("{\"verb\":\"\"}")]
    public void An_unknown_verb_is_refused_without_saying_more(string message)
    {
        AppInstanceProtocol.TryParseRequest(Utf8(message), out _, out var refusal).Should().BeFalse();

        refusal.Should().Be("That verb is not one this accepts.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"verb\":7}")]
    [InlineData("{\"verb\":\"ping\"} trailing")]
    public void Malformed_input_is_refused_rather_than_thrown(string message)
    {
        AppInstanceProtocol.TryParseRequest(Utf8(message), out _, out var refusal).Should().BeFalse();

        refusal.Should().NotBeEmpty();
    }

    [Fact]
    public void A_request_past_the_cap_is_refused()
    {
        var oversized = Utf8("{\"verb\":\"activate\",\"arguments\":\"" + new string('a', AppInstanceProtocol.MaxRequestBytes) + "\"}");

        AppInstanceProtocol.TryParseRequest(oversized, out _, out var refusal).Should().BeFalse();

        refusal.Should().Be("The request is not a size this accepts.");
    }

    [Fact]
    public void Arguments_longer_than_the_protocol_allows_are_refused()
    {
        var payload = Utf8("{\"verb\":\"activate\",\"arguments\":\""
            + new string('a', AppInstanceProtocol.MaxArgumentsLength + 1)
            + "\"}");

        AppInstanceProtocol.TryParseRequest(payload, out _, out var refusal).Should().BeFalse();

        refusal.Should().Contain("more than this accepts");
    }

    [Fact]
    public void An_activation_survives_the_round_trip()
    {
        var wire = AppInstanceProtocol.Serialize(
            new AppInstanceRequest(AppInstanceVerb.Activate, "action=install&version=1.0.1&commit=b7d41c9"));

        AppInstanceProtocol.TryParseRequest(wire, out var request, out _).Should().BeTrue();

        request.Verb.Should().Be(AppInstanceVerb.Activate);
        request.Arguments.Should().Be("action=install&version=1.0.1&commit=b7d41c9");
    }

    [Fact]
    public void A_reply_survives_the_round_trip()
    {
        var wire = AppInstanceProtocol.Serialize(AppInstanceReply.Done("Closing."));

        AppInstanceProtocol.TryParseReply(wire, out var reply).Should().BeTrue();

        reply.Ok.Should().BeTrue();
        reply.Detail.Should().Be("Closing.");
    }

    [Fact]
    public void The_pipe_name_is_scoped_to_the_signed_in_session()
    {
        AppInstanceProtocol.PipeName(1).Should().Be("BetterTranslator.App.1");
        AppInstanceProtocol.PipeName(3).Should().NotBe(AppInstanceProtocol.PipeName(1));
    }

    [Fact]
    public async Task An_absent_application_answers_nothing_rather_than_throwing()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var client = new AppInstanceClient("BetterTranslator.App.NotThere", TimeSpan.FromMilliseconds(400));

        (await client.AskAsync(AppInstanceVerb.Ping, string.Empty, stopping.Token)).Should().BeNull();
        (await client.IsRunningAsync(stopping.Token)).Should().BeFalse();
    }

    [Fact]
    public async Task A_request_and_its_answer_travel_over_a_real_pipe()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var name = "BetterTranslator.App.Test." + Guid.NewGuid().ToString("N");
        var seen = new List<AppInstanceRequest>();

        var server = new AppInstanceServer(
            (request, _) =>
            {
                seen.Add(request);

                return Task.FromResult(AppInstanceReply.Done("Running."));
            },
            name);

        var serving = server.RunAsync(stopping.Token);

        try
        {
            var reply = await new AppInstanceClient(name)
                .AskAsync(AppInstanceVerb.Activate, "action=later&version=1.0.1", stopping.Token);

            reply.Should().NotBeNull();
            reply!.Ok.Should().BeTrue();
            seen.Should().ContainSingle();
            seen[0].Arguments.Should().Be("action=later&version=1.0.1");
        }
        finally
        {
            await stopping.CancelAsync();

            try
            {
                await serving;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
