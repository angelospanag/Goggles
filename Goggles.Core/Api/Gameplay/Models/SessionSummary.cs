namespace Goggles.Core.Api.Gameplay.Models;

/// <summary>
/// The result of <see cref="GameplayClient.GetSessionSummaryAsync"/>. <see cref="Available"/> is
/// false — not an exception — when the response didn't contain a value this client recognizes,
/// since the sessions endpoint's shape is unverified.
/// </summary>
public sealed record SessionSummary
{
    public bool Available { get; init; }

    public int Minutes { get; init; }
}
