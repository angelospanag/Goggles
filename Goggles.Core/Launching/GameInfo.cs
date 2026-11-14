namespace Goggles.Core.Launching;

/// <summary>
/// The <c>goggame-{productId}.info</c> file GOG ships inside an installed build. It is what Galaxy
/// reads to decide what "Play" does, so Goggles reads it too rather than guessing at executables.
/// </summary>
public sealed record GameInfo
{
    public int Version { get; init; }

    public string? GameId { get; init; }

    public string? RootGameId { get; init; }

    public string? BuildId { get; init; }

    /// <summary>Per-game Galaxy credentials, matching the ones in the build manifest.</summary>
    public string? ClientId { get; init; }

    public string? Name { get; init; }

    public string? Language { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];

    public IReadOnlyList<string> OsBitness { get; init; } = [];

    public IReadOnlyList<PlayTask> PlayTasks { get; init; } = [];
}

/// <summary>Something the user can start: the game itself, or an extra such as a config tool.</summary>
public sealed record PlayTask
{
    /// <summary>Observed: <c>game</c>, <c>tool</c>, <c>document</c>, <c>launcher</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Exactly one task per build is normally primary; that is the "Play" button.</summary>
    public bool IsPrimary { get; init; }

    public string? Name { get; init; }

    /// <summary>Relative to the install directory. Backslash-separated on Windows builds.</summary>
    public string? Path { get; init; }

    /// <summary>Observed: <c>FileTask</c>, <c>URLTask</c>.</summary>
    public string? Type { get; init; }

    public string? Arguments { get; init; }

    /// <summary>Relative working directory, when the game insists on one.</summary>
    public string? WorkingDir { get; init; }

    /// <summary>Set on <c>URLTask</c> entries such as manuals and support links.</summary>
    public string? Link { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];

    public IReadOnlyList<string> OsBitness { get; init; } = [];

    public bool IsRunnable =>
        string.Equals(Type, "FileTask", StringComparison.OrdinalIgnoreCase) &&
        Path is { Length: > 0 };
}
