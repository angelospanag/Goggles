using Goggles.Core.Api;
using Goggles.Core.Api.ContentSystem;
using Goggles.Core.Manifests;

namespace Goggles.Core.Tests.Manifests;

public class ManifestResolverTests
{
    private const string BuildLink = "https://cdn.test/meta/build";

    [Fact]
    public async Task Resolves_the_newest_build_on_the_default_branch()
    {
        var build = await ResolveAsync();

        Assert.Equal("public-build", build.BuildId);
        Assert.Equal("windows", build.Platform);
        Assert.Equal("Absolute Drift", build.InstallDirectory);
    }

    [Fact]
    public async Task Flattens_files_and_accumulates_chunk_offsets()
    {
        var build = await ResolveAsync();
        var file = build.Files.Single(file => file.Path.EndsWith("big.bin"));

        Assert.Equal([0, 100, 250], file.Chunks.Select(chunk => chunk.Offset));
        Assert.Equal(300, file.Size);
        Assert.Equal(30, file.DownloadSize);
    }

    [Fact]
    public async Task Converts_windows_separators_to_this_platform()
    {
        var build = await ResolveAsync();

        Assert.Contains(
            Path.Combine("data", "big.bin"),
            build.Files.Select(file => file.Path));
    }

    [Fact]
    public async Task Keeps_only_depot_files()
    {
        var build = await ResolveAsync();

        // The depot also lists a DepotDirectory and a DepotLink; the third depot adds the DLC file.
        Assert.Equal(3, build.Files.Count);
    }

    [Fact]
    public async Task Skips_depots_belonging_to_products_the_account_does_not_own()
    {
        // A build can bundle DLC; GOG refuses to sign downloads for products you have not bought,
        // so those depots must never reach the download engine.
        var build = await ResolveAsync(new ResolveOptions { OwnedProductIds = new HashSet<long> { 1 } });

        Assert.DoesNotContain(build.Files, file => file.Path == "dlc.pak");
        Assert.All(build.Files, file => Assert.Equal(1, file.ProductId));
    }

    [Fact]
    public async Task Includes_owned_dlc()
    {
        var build = await ResolveAsync(new ResolveOptions { OwnedProductIds = new HashSet<long> { 1, 999 } });

        Assert.Contains(build.Files, file => file.Path == "dlc.pak" && file.ProductId == 999);
    }

    [Fact]
    public async Task Keeps_the_base_product_even_when_the_owned_set_omits_it()
    {
        // Resolving a game implies owning it; an incomplete set must not empty the install.
        var build = await ResolveAsync(new ResolveOptions { OwnedProductIds = new HashSet<long>() });

        Assert.Contains(build.Files, file => file.Path == "game.exe");
        Assert.DoesNotContain(build.Files, file => file.Path == "dlc.pak");
    }

    [Fact]
    public async Task Includes_every_depot_when_ownership_is_unknown()
    {
        var build = await ResolveAsync();

        Assert.Contains(build.Files, file => file.Path == "dlc.pak");
    }

    [Fact]
    public async Task Carries_flags_and_hashes_through()
    {
        var build = await ResolveAsync();
        var executable = build.Files.Single(file => file.Path == "game.exe");

        Assert.True(executable.IsExecutable);
        Assert.Equal("abc123", executable.Sha256);
    }

    [Fact]
    public async Task Selects_depots_by_language()
    {
        var german = await ResolveAsync(new ResolveOptions { Language = "de-DE" });

        // The German depot carries one extra file; the "*" depot is always included.
        Assert.Contains(german.Files, file => file.Path == "german.pak");
        Assert.DoesNotContain((await ResolveAsync()).Files, file => file.Path == "german.pak");
    }

    [Fact]
    public async Task Matches_a_bare_language_tag_against_a_regional_one()
    {
        var build = await ResolveAsync(new ResolveOptions { Language = "de" });

        Assert.Contains(build.Files, file => file.Path == "german.pak");
    }

    [Fact]
    public async Task Selects_depots_by_bitness()
    {
        var thirtyTwo = await ResolveAsync(new ResolveOptions { Bitness = "32" });

        Assert.DoesNotContain(thirtyTwo.Files, file => file.Path == "game.exe");
        Assert.Contains((await ResolveAsync()).Files, file => file.Path == "game.exe");
    }

    [Fact]
    public async Task The_first_depot_to_claim_a_path_keeps_it()
    {
        var build = await ResolveAsync(new ResolveOptions { Language = "de-DE" });

        Assert.Single(build.Files, file => file.Path == "game.exe");
    }

    [Fact]
    public async Task Refuses_a_path_that_escapes_the_install_directory()
    {
        Assert.Throws<InvalidDataException>(() => ManifestResolver.ToRelativePath(@"..\..\etc\passwd"));
        Assert.Throws<InvalidDataException>(() => ManifestResolver.ToRelativePath("a/../../b"));
        Assert.Throws<InvalidDataException>(() => ManifestResolver.ToRelativePath(""));
    }

    [Fact]
    public void Strips_leading_separators_rather_than_treating_a_path_as_rooted()
    {
        Assert.Equal(Path.Combine("data", "file.bin"), ManifestResolver.ToRelativePath(@"\data\file.bin"));
    }

    [Fact]
    public async Task Rejects_a_generation_1_build()
    {
        var handler = Responder(BuildsJson.Replace("\"generation\": 2", "\"generation\": 1"));

        await Assert.ThrowsAsync<NotSupportedException>(() => CreateResolver(handler).ResolveAsync(
            1,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reports_when_no_build_matches()
    {
        var handler = Responder(BuildsJson);

        await Assert.ThrowsAsync<GogApiException>(
            () => CreateResolver(handler).ResolveAsync(
                1,
                new ResolveOptions { Branch = "nope" },
                TestContext.Current.CancellationToken));
    }

    private static async Task<ResolvedBuild> ResolveAsync(ResolveOptions? options = null) =>
        await CreateResolver(Responder(BuildsJson)).ResolveAsync(1, options);

    private static ManifestResolver CreateResolver(StubHttpMessageHandler handler) =>
        new(new BuildsClient(new HttpClient(handler)), new CdnClient(new HttpClient(handler)));

    /// <summary>Serves the build list, the build blob, and one blob per depot manifest hash.</summary>
    private static StubHttpMessageHandler Responder(string buildsJson) =>
        new((request, _) =>
        {
            var uri = request.RequestUri!;

            if (uri.Host == "content-system.gog.com")
            {
                return StubHttpMessageHandler.Json(buildsJson);
            }

            return StubHttpMessageHandler.Json(uri.AbsolutePath switch
            {
                "/meta/build" => BuildManifestJson,
                var path when path.EndsWith("aaaa") => CommonDepotJson,
                var path when path.EndsWith("bbbb") => GermanDepotJson,
                var path when path.EndsWith("cccc") => DlcDepotJson,
                _ => "{}",
            });
        });

    private const string BuildsJson = $$"""
        {
          "items": [
            {
              "build_id": "beta-build",
              "branch": "beta",
              "generation": 2,
              "link": "{{BuildLink}}"
            },
            {
              "build_id": "public-build",
              "branch": null,
              "generation": 2,
              "link": "{{BuildLink}}"
            }
          ]
        }
        """;

    private const string BuildManifestJson = """
        {
          "version": 2,
          "baseProductId": "1",
          "buildId": "public-build",
          "platform": "windows",
          "installDirectory": "Absolute Drift",
          "depots": [
            {
              "manifest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "productId": "1",
              "languages": ["*"],
              "osBitness": ["64"]
            },
            {
              "manifest": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "productId": "1",
              "languages": ["de-DE"],
              "osBitness": []
            },
            {
              "manifest": "cccccccccccccccccccccccccccccccc",
              "productId": "999",
              "languages": ["*"],
              "osBitness": []
            }
          ]
        }
        """;

    /// <summary>A DLC bundled into the same build, belonging to a different product.</summary>
    private const string DlcDepotJson = """
        {
          "version": 2,
          "depot": {
            "items": [
              {
                "path": "dlc.pak",
                "type": "DepotFile",
                "chunks": [{ "md5": "m9", "compressedMd5": "c9", "size": 5, "compressedSize": 3 }]
              }
            ]
          }
        }
        """;

    private const string CommonDepotJson = """
        {
          "version": 2,
          "depot": {
            "items": [
              {
                "path": "game.exe",
                "type": "DepotFile",
                "sha256": "abc123",
                "flags": ["executable"],
                "chunks": [{ "md5": "m1", "compressedMd5": "c1", "size": 10, "compressedSize": 5 }]
              },
              {
                "path": "data\\big.bin",
                "type": "DepotFile",
                "chunks": [
                  { "md5": "m2", "compressedMd5": "c2", "size": 100, "compressedSize": 10 },
                  { "md5": "m3", "compressedMd5": "c3", "size": 150, "compressedSize": 12 },
                  { "md5": "m4", "compressedMd5": "c4", "size": 50, "compressedSize": 8 }
                ]
              },
              { "path": "data", "type": "DepotDirectory" },
              { "path": "link", "type": "DepotLink" }
            ]
          }
        }
        """;

    private const string GermanDepotJson = """
        {
          "version": 2,
          "depot": {
            "items": [
              {
                "path": "german.pak",
                "type": "DepotFile",
                "chunks": [{ "md5": "m5", "compressedMd5": "c5", "size": 20, "compressedSize": 9 }]
              },
              {
                "path": "game.exe",
                "type": "DepotFile",
                "chunks": [{ "md5": "zz", "compressedMd5": "zz", "size": 99, "compressedSize": 9 }]
              }
            ]
          }
        }
        """;
}
