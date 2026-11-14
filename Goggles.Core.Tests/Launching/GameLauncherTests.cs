using Goggles.Core.Launching;

namespace Goggles.Core.Tests.Launching;

public class GameLauncherTests : IDisposable
{
    private const long ProductId = 1136126792;

    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-launch").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Captured from a real Windows install.</summary>
    private const string WindowsInfo = """
        {
          "buildId": "56631974714622235",
          "clientId": "52472569771423029",
          "gameId": "1136126792",
          "language": "English",
          "name": "Absolute Drift",
          "osBitness": ["64"],
          "playTasks": [
            {
              "category": "game",
              "isPrimary": true,
              "languages": ["en-US"],
              "name": "Absolute Drift",
              "path": "absolutedrift.exe",
              "type": "FileTask"
            }
          ],
          "rootGameId": "1136126792",
          "version": 1
        }
        """;

    /// <summary>Captured from the same game's macOS install, where the file lives in the bundle.</summary>
    private const string MacInfo = """
        {
          "buildId": "56635440836961629",
          "gameId": "1136126792",
          "name": "Absolute Drift",
          "playTasks": [
            {
              "category": "game",
              "isPrimary": true,
              "name": "Absolute Drift",
              "path": "Contents/MacOS/absolutedrift",
              "type": "FileTask"
            }
          ],
          "version": 1
        }
        """;

    [Fact]
    public async Task Reads_the_info_file_from_the_install_root()
    {
        WriteInfo(WindowsInfo);

        var info = await GameLauncher.ReadInfoAsync(
            _directory,
            ProductId,
            TestContext.Current.CancellationToken);

        Assert.Equal("Absolute Drift", info?.Name);
        Assert.Equal("52472569771423029", info?.ClientId);
        Assert.Equal(["64"], info?.OsBitness);
        Assert.True(info?.PlayTasks.Single().IsPrimary);
    }

    [Fact]
    public async Task Finds_the_info_file_inside_a_macos_bundle()
    {
        WriteInfo(MacInfo, Path.Combine("Contents", "Resources"));

        var info = await GameLauncher.ReadInfoAsync(
            _directory,
            ProductId,
            TestContext.Current.CancellationToken);

        Assert.Equal("Absolute Drift", info?.Name);
    }

    [Fact]
    public async Task Reports_no_info_file_rather_than_throwing()
    {
        Assert.Null(await GameLauncher.ReadInfoAsync(
            _directory,
            ProductId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolves_the_primary_task()
    {
        WriteInfo(WindowsInfo);

        var target = await GameLauncher.ResolveTargetAsync(
            _directory,
            ProductId,
            TestContext.Current.CancellationToken);

        Assert.Equal("Absolute Drift", target?.Name);
        Assert.Equal(Path.Combine(_directory, "absolutedrift.exe"), target?.Executable);
        Assert.Equal(_directory, target?.WorkingDirectory);
    }

    [Fact]
    public void Prefers_the_primary_task_over_earlier_ones()
    {
        var info = new GameInfo
        {
            PlayTasks =
            [
                new PlayTask { Name = "Config", Path = "config.exe", Type = "FileTask" },
                new PlayTask { Name = "Game", Path = "game.exe", Type = "FileTask", IsPrimary = true },
            ],
        };

        Assert.Equal("Game", GameLauncher.ResolveTarget(info, _directory)?.Name);
    }

    [Fact]
    public void Falls_back_to_the_first_runnable_task_when_none_is_primary()
    {
        var info = new GameInfo
        {
            PlayTasks =
            [
                new PlayTask { Name = "Manual", Link = "https://gog.com", Type = "URLTask" },
                new PlayTask { Name = "Game", Path = "game.exe", Type = "FileTask" },
            ],
        };

        Assert.Equal("Game", GameLauncher.ResolveTarget(info, _directory)?.Name);
    }

    [Fact]
    public void Ignores_url_tasks_entirely()
    {
        var info = new GameInfo
        {
            PlayTasks = [new PlayTask { Name = "Support", Link = "https://gog.com", Type = "URLTask" }],
        };

        Assert.Null(GameLauncher.ResolveTarget(info, _directory));
    }

    [Fact]
    public void Refuses_a_play_task_that_escapes_the_install_directory()
    {
        var info = new GameInfo
        {
            PlayTasks = [new PlayTask { Path = @"..\..\..\usr\bin\evil", Type = "FileTask", IsPrimary = true }],
        };

        Assert.Throws<InvalidDataException>(() => GameLauncher.ResolveTarget(info, _directory));
    }

    [Fact]
    public void Honours_an_explicit_working_directory()
    {
        var info = new GameInfo
        {
            PlayTasks =
            [
                new PlayTask
                {
                    Path = "bin/game.exe",
                    WorkingDir = "data",
                    Type = "FileTask",
                    IsPrimary = true,
                },
            ],
        };

        Assert.Equal(Path.Combine(_directory, "data"), GameLauncher.ResolveTarget(info, _directory)?.WorkingDirectory);
    }

    [Fact]
    public async Task Detects_the_enclosing_application_bundle()
    {
        var bundle = Path.Combine(_directory, "absolutedrift.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "Contents", "Resources", $"goggame-{ProductId}.info"),
            MacInfo,
            TestContext.Current.CancellationToken);

        var target = await GameLauncher.ResolveTargetAsync(
            bundle,
            ProductId,
            TestContext.Current.CancellationToken);

        Assert.Equal(bundle, target?.Bundle);
    }

    [Fact]
    public async Task A_plain_install_has_no_bundle()
    {
        WriteInfo(WindowsInfo);

        var target = await GameLauncher.ResolveTargetAsync(
            _directory,
            ProductId,
            TestContext.Current.CancellationToken);

        Assert.Null(target?.Bundle);
    }

    [Fact]
    public void Launching_something_that_is_not_there_says_so()
    {
        var target = new LaunchTarget
        {
            Name = "Absolute Drift",
            Executable = Path.Combine(_directory, "missing.exe"),
            WorkingDirectory = _directory,
        };

        Assert.Throws<FileNotFoundException>(() => new GameLauncher().Launch(target));
    }

    [Fact]
    public async Task A_corrupt_info_file_is_reported_clearly()
    {
        WriteInfo("{ not json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => GameLauncher.ReadInfoAsync(
                _directory,
                ProductId,
                TestContext.Current.CancellationToken));

        Assert.Contains("Could not read", exception.Message);
    }

    private void WriteInfo(string json, string? subdirectory = null)
    {
        var directory = subdirectory is null ? _directory : Path.Combine(_directory, subdirectory);
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, $"goggame-{ProductId}.info"), json);
    }
}
