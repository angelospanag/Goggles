namespace Goggles.Core.Api.Embed.Models;

/// <summary>Response of <c>embed.gog.com/user/data/games</c> — ids only, no metadata.</summary>
public sealed record OwnedProducts
{
    public IReadOnlyList<long> Owned { get; init; } = [];
}
