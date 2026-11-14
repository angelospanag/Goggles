using Goggles.Core.Installs;

namespace Goggles.Core.Tests.Installs;

public class GameUninstallerTests : IDisposable
{
    private const long ProductId = 1136126792;

    private readonly string _root = Directory.CreateTempSubdirectory("goggles-uninstall").FullName;
    private readonly GameUninstaller _uninstaller = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Removes_an_install_and_reports_what_it_freed()
    {
        var install = CreateInstall("Absolute Drift", fileSize: 2048);

        var result = _uninstaller.Uninstall(_root, install, ProductId);

        Assert.True(result.Removed);
        Assert.Equal(2048, result.BytesFreed);
        Assert.False(Directory.Exists(install));
    }

    [Fact]
    public void Removes_an_install_that_is_a_macos_bundle()
    {
        var bundle = Path.Combine(_root, "absolutedrift.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Resources", $"goggame-{ProductId}.info"),
            "{}");

        var result = _uninstaller.Uninstall(_root, bundle, ProductId);

        Assert.True(result.Removed);
        Assert.False(Directory.Exists(bundle));
    }

    [Fact]
    public void An_install_that_is_already_gone_is_not_an_error()
    {
        var result = _uninstaller.Uninstall(_root, Path.Combine(_root, "Never Installed"), ProductId);

        Assert.False(result.Removed);
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public void Refuses_to_delete_the_library_root()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => _uninstaller.Uninstall(_root, _root, ProductId));

        Assert.Contains("library root itself", exception.Message);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Refuses_a_target_outside_the_library_root()
    {
        var outside = Directory.CreateTempSubdirectory("goggles-outside").FullName;

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => _uninstaller.Uninstall(_root, outside, ProductId));

            Assert.Contains("outside the library root", exception.Message);
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Refuses_a_path_that_climbs_out_of_the_library_root()
    {
        var escape = Path.Combine(_root, "..", Path.GetFileName(_root) + "-elsewhere");

        Assert.Throws<InvalidOperationException>(() => _uninstaller.Uninstall(_root, escape, ProductId));
    }

    [Fact]
    public void Refuses_a_directory_that_is_not_this_game()
    {
        // Right shape, wrong game: the info file names a different product.
        var install = CreateInstall("Some Other Game", fileSize: 10, productId: 999);

        var exception = Assert.Throws<InvalidOperationException>(
            () => _uninstaller.Uninstall(_root, install, ProductId));

        Assert.Contains($"goggame-{ProductId}.info", exception.Message);
        Assert.True(Directory.Exists(install));
    }

    [Fact]
    public void Refuses_a_directory_with_no_install_marker_at_all()
    {
        var stray = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "important.txt"), "do not delete");

        Assert.Throws<InvalidOperationException>(() => _uninstaller.Uninstall(_root, stray, ProductId));
        Assert.True(File.Exists(Path.Combine(stray, "important.txt")));
    }

    [Fact]
    public void Refuses_to_follow_a_symlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var real = Directory.CreateTempSubdirectory("goggles-real").FullName;
        File.WriteAllText(Path.Combine(real, $"goggame-{ProductId}.info"), "{}");

        var link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, real);

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => _uninstaller.Uninstall(_root, link, ProductId));

            Assert.Contains("is a link", exception.Message);
            Assert.True(Directory.Exists(real));
        }
        finally
        {
            Directory.Delete(real, recursive: true);
        }
    }

    [Fact]
    public void Measures_an_install()
    {
        var install = CreateInstall("Absolute Drift", fileSize: 4096);

        Assert.Equal(4096, GameUninstaller.Measure(install));
        Assert.Equal(0, GameUninstaller.Measure(Path.Combine(_root, "nothing")));
    }

    private string CreateInstall(string name, int fileSize, long productId = ProductId)
    {
        var install = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(install, "data"));

        // The marker is left empty so the measured total is exactly fileSize.
        File.WriteAllText(Path.Combine(install, $"goggame-{productId}.info"), string.Empty);
        File.WriteAllBytes(Path.Combine(install, "data", "content.bin"), new byte[fileSize]);

        return install;
    }
}
