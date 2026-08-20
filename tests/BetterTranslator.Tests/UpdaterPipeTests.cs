using System.Text;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Ipc;
using BetterTranslator.Updates.Releases;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class UpdaterPipeTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("status", UpdaterVerb.Status)]
    [InlineData("check", UpdaterVerb.Check)]
    [InlineData("last", UpdaterVerb.Last)]
    [InlineData("apply", UpdaterVerb.Apply)]
    public void The_four_verbs_are_accepted(string name, UpdaterVerb expected)
    {
        UpdaterProtocol.TryParseRequest(Utf8($"{{\"verb\":\"{name}\"}}"), out var request, out _)
            .Should().BeTrue();

        request.Verb.Should().Be(expected);
    }

    [Theory]
    [InlineData("{\"verb\":\"restart\"}")]
    [InlineData("{\"verb\":\"STATUS\"}")]
    [InlineData("{\"verb\":\"../../etc\"}")]
    public void An_unknown_verb_is_refused_without_saying_more(string message)
    {
        UpdaterProtocol.TryParseRequest(Utf8(message), out _, out var refusal).Should().BeFalse();

        refusal.Should().Be("That verb is not one this accepts.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"status\"")]
    [InlineData("{\"verb\":42}")]
    [InlineData("{\"verb\":null}")]
    [InlineData("{\"nothing\":\"here\"}")]
    [InlineData("{\"verb\":\"status\"} trailing")]
    public void Malformed_input_is_refused_rather_than_thrown(string message)
    {
        var parsed = UpdaterProtocol.TryParseRequest(Utf8(message), out _, out var refusal);

        parsed.Should().BeFalse();
        refusal.Should().NotBeEmpty();
    }

    [Fact]
    public void A_request_past_the_cap_is_refused_before_it_is_read()
    {
        var oversized = Utf8("{\"verb\":\"" + new string('a', UpdaterProtocol.MaxRequestBytes) + "\"}");

        UpdaterProtocol.TryParseRequest(oversized, out _, out var refusal).Should().BeFalse();

        refusal.Should().Be("The request is not a size this accepts.");
    }

    [Fact]
    public void A_verb_longer_than_a_verb_can_be_is_refused()
    {
        UpdaterProtocol.TryParseRequest(
                Utf8("{\"verb\":\"" + new string('a', 64) + "\"}"),
                out _,
                out var refusal)
            .Should().BeFalse();

        refusal.Should().NotBeEmpty();
    }

    [Fact]
    public void A_status_answer_survives_the_round_trip()
    {
        var status = new UpdateStatus(
            UpdateOutcome.UpdateAvailable,
            "1.0.1 is newer than 1.0.0.",
            "1.0.0",
            "a54ff15",
            "1.0.1",
            "b7d41c9",
            Ready: true,
            new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero));

        var wire = UpdaterProtocol.Serialize(new UpdaterResponse(true, status.Detail, status));

        wire.Length.Should().BeLessThan(UpdaterProtocol.MaxResponseBytes);

        UpdaterProtocol.TryParseResponse(wire, out var response).Should().BeTrue();

        response.Ok.Should().BeTrue();
        response.Status.Should().NotBeNull();
        response.Status!.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        response.Status.InstalledVersion.Should().Be("1.0.0");
        response.Status.LatestCommit.Should().Be("b7d41c9");
        response.Status.Ready.Should().BeTrue();
        response.Status.CheckedUtc.Should().Be(status.CheckedUtc);
    }

    [Fact]
    public void A_refusal_carries_no_status_and_still_parses()
    {
        var wire = UpdaterProtocol.Serialize(UpdaterResponse.Refused("That verb is not one this accepts."));

        UpdaterProtocol.TryParseResponse(wire, out var response).Should().BeTrue();

        response.Ok.Should().BeFalse();
        response.Status.Should().BeNull();
        response.Detail.Should().Be("That verb is not one this accepts.");
    }

    [Fact]
    public void An_oversized_answer_is_not_parsed()
    {
        var wire = Utf8("{\"ok\":true,\"detail\":\"" + new string('a', UpdaterProtocol.MaxResponseBytes) + "\"}");

        UpdaterProtocol.TryParseResponse(wire, out var response).Should().BeFalse();

        response.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_request_and_its_answer_travel_over_a_real_pipe()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var answered = new UpdateStatus(
            UpdateOutcome.UpToDate,
            "1.0.0+a54ff15 is the latest build.",
            "1.0.0",
            "a54ff15",
            "1.0.0",
            "a54ff15",
            Ready: false,
            DateTimeOffset.UtcNow);

        // A name of its own. The well known one belongs to the service, and on a
        // machine where that service is registered it answers first and this
        // asserts against whatever that machine happens to be updating.
        var pipeName = "BetterTranslator.Updater.Test." + Guid.NewGuid().ToString("n");

        var server = new UpdaterPipeServer(
            (request, _) =>
                Task.FromResult(request.Verb == UpdaterVerb.Status
                    ? new UpdaterResponse(true, answered.Detail, answered)
                    : UpdaterResponse.Refused("That verb is not one this accepts.")),
            null,
            pipeName);

        var serving = server.RunAsync(stopping.Token);

        try
        {
            var response = await new UpdaterPipeClient(pipeName).AskAsync(UpdaterVerb.Status, stopping.Token);

            response.Should().NotBeNull();
            response!.Ok.Should().BeTrue();
            response.Status!.Outcome.Should().Be(UpdateOutcome.UpToDate);
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

    [Fact]
    public async Task An_absent_updater_answers_nothing_rather_than_throwing()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var response = await new UpdaterPipeClient("BetterTranslator.Updater.NotThere")
            .AskAsync(UpdaterVerb.Status, stopping.Token);

        response.Should().BeNull();
    }
}
