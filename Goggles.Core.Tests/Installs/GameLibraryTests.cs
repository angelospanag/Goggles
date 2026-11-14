using Goggles.Core.Installs;

namespace Goggles.Core.Tests.Installs;

public class GameLibraryTests : IDisposable
{
    private const long ProductId = 1136126792;

    private readonly string _root = Directory.CreateTempSubdirectory("goggles-library").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Installs_under_the_directory_the_manifest_names()
    {
        var library = new GameLibrary(_root);

        Assert.Equal(
            Path.Combine(_root, "absolutedrift.app"),
            library.InstallPathFor(ProductId, "absolutedrift.app"));
    }

    [Fact]
    public void Falls_back_to_the_product_id_when_a_build_names_no_directory()
    {
        var library = new GameLibrary(_root);

        Assert.Equal(Path.Combine(_root, ProductId.ToString()), library.InstallPathFor(ProductId, null));
    }

    [Fact]
    public void Finds_a_plain_install()
    {
        Install("Tyranny", bundle: false);

        var installed = new GameLibrary(_root).Scan().Single();

        Assert.Equal(ProductId, installed.ProductId);
        Assert.Equal(Path.Combine(_root, "Tyranny"), installed.InstallPath);
    }

    [Fact]
    public void Finds_a_macos_bundle()
    {
        Install("absolutedrift.app", bundle: true);

        var installed = new GameLibrary(_root).Scan().Single();

        // Play-task paths are relative to the bundle, not to Contents/Resources.
        Assert.Equal(Path.Combine(_root, "absolutedrift.app"), installed.InstallPath);
    }

    [Fact]
    public void Reads_the_name_and_build_from_the_marker()
    {
        Install("absolutedrift.app", bundle: true);

        var installed = new GameLibrary(_root).Scan().Single();

        Assert.Equal("Absolute Drift", installed.Name);
        Assert.Equal("56635440836961629", installed.BuildId);
    }

    [Fact]
    public void Ignores_directories_that_are_not_installs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Screenshots", "2026"));
        File.WriteAllText(Path.Combine(_root, "Screenshots", "2026", "shot.png"), "not a game");

        Assert.Empty(new GameLibrary(_root).Scan());
    }

    [Fact]
    public void Survives_an_unreadable_marker()
    {
        var directory = Path.Combine(_root, "Broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"goggame-{ProductId}.info"), "{ not json");

        Assert.Empty(new GameLibrary(_root).Scan());
    }

    [Fact]
    public void An_absent_root_is_simply_empty()
    {
        Assert.Empty(new GameLibrary(Path.Combine(_root, "nowhere")).Scan());
    }

    [Fact]
    public void Finds_one_game_among_several()
    {
        Install("absolutedrift.app", bundle: true);
        Install("Baldur's Gate", bundle: false, productId: 1207658930);

        var library = new GameLibrary(_root);

        Assert.Equal(2, library.Scan().Count);
        Assert.Equal(Path.Combine(_root, "Baldur's Gate"), library.Find(1207658930)?.InstallPath);
        Assert.Null(library.Find(42));
    }

    [Fact]
    public void Confirms_whether_a_directory_holds_a_given_product()
    {
        Install("absolutedrift.app", bundle: true);
        var install = Path.Combine(_root, "absolutedrift.app");

        Assert.True(GameLibrary.Contains(install, ProductId));
        Assert.False(GameLibrary.Contains(install, 42));
        Assert.False(GameLibrary.Contains(Path.Combine(_root, "nothing"), ProductId));
    }

    private const string Marker = """
        {
          "buildId": "56635440836961629",
          "gameId": "1136126792",
          "name": "Absolute Drift",
          "playTasks": [{ "isPrimary": true, "path": "Contents/MacOS/absolutedrift", "type": "FileTask" }],
          "version": 1
        }
        """;

    private void Install(string installDirectory, bool bundle, long productId = ProductId)
    {
        var directory = bundle
            ? Path.Combine(_root, installDirectory, "Contents", "Resources")
            : Path.Combine(_root, installDirectory);

        Directory.CreateDirectory(directory);

        var marker = productId == ProductId
            ? Marker
            : Marker.Replace("1136126792", productId.ToString());

        File.WriteAllText(Path.Combine(directory, $"goggame-{productId}.info"), marker);
    }
}
