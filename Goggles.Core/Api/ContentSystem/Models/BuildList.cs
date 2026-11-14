using System.Text.Json.Serialization;
using Goggles.Core.Api.Serialization;

namespace Goggles.Core.Api.ContentSystem.Models;

/// <summary>
/// Response of <c>content-system.gog.com/products/{id}/os/{os}/builds?generation=2</c>.
/// </summary>
/// <remarks>
/// This host speaks snake_case, unlike the embed endpoints — see the serializer options on
/// <see cref="BuildsClient"/>.
/// </remarks>
public sealed record BuildList
{
    public int TotalCount { get; init; }

    public int Count { get; init; }

    public bool HasPrivateBranches { get; init; }

    public IReadOnlyList<Build> Items { get; init; } = [];
}

public sealed record Build
{
    public string? BuildId { get; init; }

    public string? ProductId { get; init; }

    public string? Os { get; init; }

    /// <summary>Null for the default (public) branch.</summary>
    public string? Branch { get; init; }

    public string? VersionName { get; init; }

    public bool Public { get; init; }

    public int Generation { get; init; }

    [JsonConverter(typeof(LenientDateTimeOffsetConverter))]
    public DateTimeOffset? DatePublished { get; init; }

    /// <summary>
    /// Where this build's metadata blob lives. Following it is safer than deriving the CDN path
    /// from the hash, which GOG shards in a way the documentation does not describe.
    /// </summary>
    public Uri? Link { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? LegacyBuildId { get; init; }
}
