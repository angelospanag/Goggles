namespace Goggles.Core.Manifests;

/// <summary>
/// A generation 2 build metadata blob — the top of the manifest tree. Captured from live
/// responses; see CLAUDE.md for the shape.
/// </summary>
public sealed record BuildManifest
{
    public int Version { get; init; }

    public string? BaseProductId { get; init; }

    public string? BuildId { get; init; }

    public string? Platform { get; init; }

    /// <summary>Folder name the game expects to be installed into.</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>Per-game Galaxy credentials, used later for achievements and cloud saves.</summary>
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public bool ScriptInterpreter { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<DepotReference> Depots { get; init; } = [];

    public IReadOnlyList<ProductReference> Products { get; init; } = [];

    public DepotReference? OfflineDepot { get; init; }

    /// <summary>Redistributables (DirectX, VC++ runtimes and similar), when a build needs them.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

/// <summary>Points at a depot manifest, with enough detail to decide whether it is wanted.</summary>
public sealed record DepotReference
{
    /// <summary>Hash of the depot manifest blob.</summary>
    public string? Manifest { get; init; }

    public long Size { get; init; }

    public long CompressedSize { get; init; }

    public string? ProductId { get; init; }

    /// <summary><c>*</c> means "any", otherwise tags such as <c>en-US</c>.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Empty means "any"; otherwise <c>32</c> or <c>64</c>.</summary>
    public IReadOnlyList<string> OsBitness { get; init; } = [];
}

public sealed record ProductReference
{
    public string? Name { get; init; }

    public string? ProductId { get; init; }
}
