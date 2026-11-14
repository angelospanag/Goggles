using System.Text.Json;
using Goggles.Core.Api.ContentSystem;

namespace Goggles.Core.Tests.Api.Serialization;

/// <summary>
/// The converter itself is internal, so it is exercised through the client that uses it.
/// </summary>
public class LenientDateTimeOffsetConverterTests
{
    [Theory]
    [InlineData("2023-09-19T18:36:09+0000", "2023-09-19T18:36:09+00:00")]
    [InlineData("2023-09-19T18:36:09+0200", "2023-09-19T18:36:09+02:00")]
    [InlineData("2023-09-19T18:36:09-0500", "2023-09-19T18:36:09-05:00")]
    [InlineData("2023-09-19T18:36:09+02:00", "2023-09-19T18:36:09+02:00")]
    [InlineData("2023-09-19T18:36:09Z", "2023-09-19T18:36:09+00:00")]
    public async Task Reads_offsets_with_or_without_a_colon(string published, string expected)
    {
        var build = await ReadDatePublishedAsync($"\"{published}\"");

        Assert.Equal(DateTimeOffset.Parse(expected), build);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"not a date at all\"")]
    public async Task Unreadable_values_come_back_null(string published)
    {
        Assert.Null(await ReadDatePublishedAsync(published));
    }

    [Fact]
    public async Task A_non_string_token_is_an_error()
    {
        await Assert.ThrowsAsync<JsonException>(() => ReadDatePublishedAsync("12345"));
    }

    private static async Task<DateTimeOffset?> ReadDatePublishedAsync(string published)
    {
        var handler = new StubHttpMessageHandler(
            $$"""{"items":[{"build_id":"1","date_published":{{published}}}]}""");

        var builds = await new BuildsClient(new HttpClient(handler)).GetBuildsAsync(1);

        return builds.Items.Single().DatePublished;
    }
}
