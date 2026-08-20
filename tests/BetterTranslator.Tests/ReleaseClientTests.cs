using System.Linq;
using System.Net.Http;
using BetterTranslator.Updates.Releases;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class ReleaseClientTests
{
    [Fact]
    public void A_recorded_release_parses_into_tag_publish_time_and_assets()
    {
        var release = GitHubReleaseClient.Parse(UpdateFixtures.Read("release-latest.json"));

        release.Should().NotBeNull();
        release!.Tag.Should().Be("v1.0.0");
        release.PublishedUtc.Should().Be(new DateTimeOffset(2026, 8, 15, 9, 30, 0, TimeSpan.Zero));
        release.Assets.Should().HaveCount(1);

        var asset = release.Payload("BetterTranslator.exe");

        asset.Should().NotBeNull();
        asset!.SizeBytes.Should().Be(105625088);
        asset.Sha256.Should().Be("9f2c1b7d6a4e35c80d1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f");
    }

    [Fact]
    public void A_branch_name_is_not_taken_for_a_commit()
    {
        GitHubReleaseClient.Parse(UpdateFixtures.Read("release-latest.json"))!.HasCommit.Should().BeFalse();
        GitHubReleaseClient.Parse(UpdateFixtures.Read("release-next.json"))!.Commit
            .Should().Be("b7d41c9e2f3a5061728394a5b6c7d8e9f0a1b2c3");
    }

    [Fact]
    public void An_asset_offered_over_plain_http_is_dropped()
    {
        var release = GitHubReleaseClient.Parse(
            """
            {
              "tag_name": "v9.9.9",
              "assets": [
                { "name": "BetterTranslator.exe", "size": 10, "browser_download_url": "http://example.invalid/x.exe" }
              ]
            }
            """);

        release.Should().NotBeNull();
        release!.Assets.Should().BeEmpty();
    }

    [Fact]
    public void Something_that_is_not_a_release_parses_to_nothing()
    {
        GitHubReleaseClient.Parse("[]").Should().BeNull();
        GitHubReleaseClient.Parse("{\"message\":\"Not Found\"}").Should().BeNull();
        GitHubReleaseClient.Parse("not json at all").Should().BeNull();
    }

    [Fact]
    public async Task The_commit_is_resolved_from_the_tag_when_the_release_names_a_branch()
    {
        using var root = new TemporaryUpdateRoot();

        var handler = new RecordingHandler(
            _ => RecordingHandler.Json(UpdateFixtures.Read("release-latest.json"), "\"first\""),
            _ => RecordingHandler.Json(UpdateFixtures.Read("tag-ref.json"), "\"tag\""));

        using var http = new HttpClient(handler);

        var lookup = await new GitHubReleaseClient(http, root.Paths).LatestAsync(CancellationToken.None);

        lookup.Ok.Should().BeTrue();
        lookup.Release!.Commit.Should().Be("f9457dbb1c2d3e4f5061728394a5b6c7d8e9f0a1");
        handler.Exchanges.Should().HaveCount(2);
        handler.Exchanges[1].Url.Should().EndWith("/git/ref/tags/v1.0.0");
    }

    [Fact]
    public async Task The_second_check_asks_conditionally_and_a_304_answers_from_the_cache()
    {
        using var root = new TemporaryUpdateRoot();

        var handler = new RecordingHandler(
            _ => RecordingHandler.Json(UpdateFixtures.Read("release-next.json"), "\"etag-1\""),
            _ => RecordingHandler.NotModified());

        using var http = new HttpClient(handler);
        var client = new GitHubReleaseClient(http, root.Paths);

        var first = await client.LatestAsync(CancellationToken.None);
        var second = await client.LatestAsync(CancellationToken.None);

        first.Ok.Should().BeTrue();
        second.Ok.Should().BeTrue();
        second.Release!.Tag.Should().Be("v1.0.1");

        handler.Exchanges[0].IfNoneMatch.Should().BeNull();
        handler.Exchanges[1].IfNoneMatch.Should().Be("\"etag-1\"");
    }

    [Fact]
    public async Task A_rate_limited_answer_backs_off_and_falls_back_to_what_was_cached()
    {
        using var root = new TemporaryUpdateRoot();

        var reset = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var handler = new RecordingHandler(
            _ => RecordingHandler.Json(UpdateFixtures.Read("release-next.json"), "\"etag-1\""),
            _ => RecordingHandler.RateLimited(reset));

        using var http = new HttpClient(handler);
        var client = new GitHubReleaseClient(http, root.Paths);

        await client.LatestAsync(CancellationToken.None);

        var limited = await client.LatestAsync(CancellationToken.None);

        limited.Ok.Should().BeTrue();
        limited.Release!.Tag.Should().Be("v1.0.1");

        var held = await client.LatestAsync(CancellationToken.None);

        held.Ok.Should().BeTrue();
        handler.Exchanges.Should().HaveCount(2, "the hold off keeps the third check off the wire");
    }

    [Fact]
    public async Task A_rate_limited_first_check_with_nothing_cached_fails_the_check()
    {
        using var root = new TemporaryUpdateRoot();

        var handler = new RecordingHandler(
            _ => RecordingHandler.RateLimited(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));

        using var http = new HttpClient(handler);

        var lookup = await new GitHubReleaseClient(http, root.Paths).LatestAsync(CancellationToken.None);

        lookup.Ok.Should().BeFalse();
        lookup.Detail.Should().Contain("rate limit");
    }

    [Fact]
    public void The_client_identifies_itself_and_sends_no_credential_by_default()
    {
        using var client = GitHubReleaseClient.CreateHttpClient();

        client.DefaultRequestHeaders.UserAgent.ToString().Should().StartWith("BetterTranslator/");
        client.DefaultRequestHeaders.Accept.Select(a => a.MediaType)
            .Should().Contain("application/vnd.github+json");

        if (Environment.GetEnvironmentVariable(GitHubReleaseClient.TokenVariable) is null)
        {
            client.DefaultRequestHeaders.Authorization.Should().BeNull();
        }
    }
}
