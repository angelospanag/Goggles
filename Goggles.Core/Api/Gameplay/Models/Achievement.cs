namespace Goggles.Core.Api.Gameplay.Models;

/// <summary>One achievement definition plus this user's unlock state for it.</summary>
public sealed record Achievement
{
    public required string Key { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? ImageUrlUnlocked { get; init; }

    public string? ImageUrlLocked { get; init; }

    /// <summary>
    /// False for a secret achievement GOG hasn't revealed the name/description of yet — true once
    /// unlocked, regardless of the original value. Callers showing the full achievement list (not
    /// just unlocked ones) must check this before displaying Name/Description, or they'll spoil a
    /// secret.
    /// </summary>
    public bool Visible { get; init; }

    /// <summary>
    /// Null until the raw <c>date_unlocked</c> value both exists and parses — an unparseable date is
    /// treated as "not unlocked" rather than failing the whole fetch, since this host's response
    /// shape isn't officially documented.
    /// </summary>
    public DateTimeOffset? UnlockedAt { get; init; }
}
