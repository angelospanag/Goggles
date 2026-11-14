using System.Text.Json;
using Goggles.Core.Api;
using Goggles.Core.Api.ContentSystem;

namespace Goggles.Core.Manifests;

/// <summary>
/// Turns "this product, this platform" into a flat list of files to fetch: picks a build, selects
/// the depots that match the requested language and bitness, and flattens their contents.
/// </summary>
public sealed class ManifestResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly BuildsClient _builds;
    private readonly CdnClient _cdn;

    public ManifestResolver(BuildsClient builds, CdnClient cdn)
    {
        _builds = builds;
        _cdn = cdn;
    }

    public async Task<ResolvedBuild> ResolveAsync(
        long productId,
        ResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ResolveOptions();

        var builds = await _builds
            .GetBuildsAsync(productId, options.Os, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Builds arrive newest-first across every branch, so the top entry is often a beta.
        var build = options.BuildId is not null
            ? builds.Items.FirstOrDefault(candidate => candidate.BuildId == options.BuildId)
            : builds.Items.FirstOrDefault(candidate => candidate.Branch == options.Branch);

        if (build?.Link is null)
        {
            throw new GogApiException(
                $"No build for product {productId} on {options.Os} " +
                $"(branch {options.Branch ?? "default"}, build {options.BuildId ?? "newest"}).");
        }

        if (build.Generation != 2)
        {
            throw new NotSupportedException(
                $"Build {build.BuildId} is generation {build.Generation}; only generation 2 is supported.");
        }

        var manifest = Deserialize<BuildManifest>(
            await _cdn.GetMetaJsonAsync(build.Link, cancellationToken).ConfigureAwait(false));

        var files = new List<ResolvedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var depot in manifest.Depots.Where(depot => Wanted(depot, options, productId)))
        {
            if (depot.Manifest is not { Length: > 0 } hash)
            {
                continue;
            }

            var depotManifest = Deserialize<DepotManifest>(
                await _cdn.GetMetaJsonAsync(hash, cancellationToken).ConfigureAwait(false));

            var depotProductId = ParseProductId(depot.ProductId) ?? productId;

            foreach (var item in depotManifest.Depot?.Items ?? [])
            {
                var file = Flatten(item, depotProductId);

                // Depots can overlap; the first depot to claim a path owns it, so a later one
                // cannot silently overwrite a file another already accounted for.
                if (file is not null && seen.Add(file.Path))
                {
                    files.Add(file);
                }
            }
        }

        return new ResolvedBuild
        {
            BuildId = build.BuildId ?? string.Empty,
            Platform = manifest.Platform ?? options.Os,
            InstallDirectory = manifest.InstallDirectory,
            Files = files,
            Dependencies = manifest.Dependencies,
        };
    }

    /// <summary>
    /// Turns a depot path into a safe relative path. Manifests are remote input, so a path that
    /// escapes the install directory is rejected rather than normalised into something plausible.
    /// </summary>
    public static string ToRelativePath(string path)
    {
        var normalised = path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (normalised.Length == 0)
        {
            throw new InvalidDataException("Depot item has an empty path.");
        }

        if (Path.IsPathRooted(normalised) ||
            normalised.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
        {
            throw new InvalidDataException($"Depot item path '{path}' escapes the install directory.");
        }

        return normalised;
    }

    private static ResolvedFile? Flatten(DepotItem item, long productId)
    {
        // Directories are implied by file paths, and links are not something Goggles creates yet.
        if (!string.Equals(item.Type, "DepotFile", StringComparison.OrdinalIgnoreCase) ||
            item.Path is not { Length: > 0 } path)
        {
            return null;
        }

        var offset = 0L;
        var chunks = new List<ResolvedChunk>(item.Chunks.Count);

        foreach (var chunk in item.Chunks)
        {
            if (chunk.CompressedMd5 is not { Length: > 0 } compressedMd5)
            {
                throw new InvalidDataException($"Chunk of '{path}' has no compressedMd5 to fetch it by.");
            }

            chunks.Add(new ResolvedChunk
            {
                CompressedMd5 = compressedMd5,
                Md5 = chunk.Md5,
                Size = chunk.Size,
                CompressedSize = chunk.CompressedSize,
                Offset = offset,
            });

            offset += chunk.Size;
        }

        return new ResolvedFile
        {
            Path = ToRelativePath(path),
            ProductId = productId,
            Sha256 = item.Sha256,
            Flags = item.Flags,
            Chunks = chunks,
        };
    }

    private static bool Wanted(DepotReference depot, ResolveOptions options, long baseProductId) =>
        Owned(depot, options, baseProductId) &&
        MatchesLanguage(depot.Languages, options.Language) &&
        MatchesBitness(depot.OsBitness, options.Bitness);

    /// <summary>
    /// A build can span several products: the game and its DLC share one manifest. GOG signs
    /// download URLs per product and refuses (403) for anything the account has not bought, so
    /// depots belonging to unowned products are dropped before they can be requested.
    /// </summary>
    private static bool Owned(DepotReference depot, ResolveOptions options, long baseProductId)
    {
        if (options.OwnedProductIds is not { } owned)
        {
            return true;
        }

        // A depot with no product of its own belongs to the game being installed.
        return ParseProductId(depot.ProductId) is not { } productId ||
               productId == baseProductId ||
               owned.Contains(productId);
    }

    private static bool MatchesLanguage(IReadOnlyList<string> languages, string wanted)
    {
        if (languages.Count == 0)
        {
            return true;
        }

        return languages.Any(language =>
            language == "*" ||
            string.Equals(language, wanted, StringComparison.OrdinalIgnoreCase) ||
            // Depots are tagged "en-US" or plain "en" depending on the title's vintage.
            string.Equals(Primary(language), Primary(wanted), StringComparison.OrdinalIgnoreCase));

        static string Primary(string tag) => tag.Split('-')[0];
    }

    private static bool MatchesBitness(IReadOnlyList<string> bitness, string? wanted) =>
        bitness.Count == 0 || wanted is null || bitness.Contains(wanted);

    private static long? ParseProductId(string? value) =>
        long.TryParse(value, out var parsed) ? parsed : null;

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new InvalidDataException($"Empty {typeof(T).Name} blob.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Could not read a {typeof(T).Name}: {exception.Message}", exception);
        }
    }
}

public sealed record ResolveOptions
{
    /// <summary><c>windows</c>, <c>osx</c> or <c>linux</c>.</summary>
    public string Os { get; init; } = "windows";

    /// <summary>Null selects the default (public) branch.</summary>
    public string? Branch { get; init; }

    /// <summary>Pins an exact build; otherwise the newest on <see cref="Branch"/> is used.</summary>
    public string? BuildId { get; init; }

    public string Language { get; init; } = "en-US";

    /// <summary>Null accepts depots of any bitness.</summary>
    public string? Bitness { get; init; } = "64";

    /// <summary>
    /// Products the account owns. Depots belonging to anything else — DLC bundled into the same
    /// build — are skipped, because GOG will not sign download URLs for them. Null includes
    /// everything, which is only useful for inspecting a build rather than installing it.
    /// </summary>
    public IReadOnlySet<long>? OwnedProductIds { get; init; }
}
