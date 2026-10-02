using HADA.Service.Updates;

namespace HADA.Tests.Service;

public class UpdateTests
{
    [Fact]
    public void The_highest_published_version_wins_whatever_the_order()
    {
        const string Json = """
            [
              { "tag_name": "v0.3.0", "html_url": "https://example.com/v0.3.0", "draft": false, "prerelease": true },
              { "tag_name": "v0.10.1", "html_url": "https://example.com/v0.10.1", "draft": false, "prerelease": true },
              { "tag_name": "v0.9.0", "html_url": "https://example.com/v0.9.0", "draft": false, "prerelease": false },
              { "tag_name": "v9.0.0", "html_url": "https://example.com/v9.0.0", "draft": true },
              { "tag_name": "nightly", "html_url": "https://example.com/nightly", "draft": false },
              { "tag_name": "v1.0", "html_url": "https://example.com/v1.0", "draft": false }
            ]
            """;

        var latest = ReleaseFeed.FindLatest(Json);

        Assert.Equal((new Version(0, 10, 1), "https://example.com/v0.10.1"), latest);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "message": "API rate limit exceeded" }""")]
    [InlineData("not json")]
    [InlineData("""[ "v1.2.3", { "tag_name": 5 } ]""")]
    public void Anything_but_a_list_of_releases_yields_nothing(string json)
    {
        Assert.Null(ReleaseFeed.FindLatest(json));
    }

    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.0+3f2a1bc", "0.4.0")]
    [InlineData("V1.2.3-beta", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void Versions_are_parsed_from_tags_and_from_the_assembly(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), ReleaseFeed.ParseVersion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2")]
    public void What_is_not_a_three_part_version_is_not_a_version(string? text)
    {
        Assert.Null(ReleaseFeed.ParseVersion(text));
    }
}
