namespace Goggles.Core.Installs;

/// <summary>A game found on disk by <see cref="GameLibrary.Scan"/>.</summary>
public sealed record InstalledGame
{
    public required long ProductId { get; init; }

    /// <summary>
    /// The build's directory under the library root. Play-task paths are relative to it, and it is
    /// what uninstall removes.
    /// </summary>
    public required string InstallPath { get; init; }

    public string? Name { get; init; }

    public string? BuildId { get; init; }
}
