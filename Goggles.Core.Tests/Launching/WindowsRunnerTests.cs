using Goggles.Core.Launching;

namespace Goggles.Core.Tests.Launching;

public class WindowsRunnerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-runner-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static LaunchTarget Target(string? arguments = null) => new()
    {
        Name = "Tyranny",
        Executable = "/Games/Tyranny/Tyranny.exe",
        WorkingDirectory = "/Games/Tyranny",
        Arguments = arguments,
    };

    [Fact]
    public void CrossOver_runs_the_game_in_the_bottle_through_its_wine_wrapper()
    {
        var runner = new CrossOverRunner { AppPath = "/Applications/CrossOver.app", Bottle = "Games" };

        var startInfo = runner.CreateStartInfo(Target("-skipintro"));

        // Built with Path.Combine, so the separator is the host's; CrossOver itself only exists on macOS.
        Assert.Equal(
            Path.Combine("/Applications/CrossOver.app", "Contents", "SharedSupport", "CrossOver", "bin", "wine"),
            startInfo.FileName);
        Assert.Equal(
            ["--bottle", "Games", "--workdir", "/Games/Tyranny", "/Games/Tyranny/Tyranny.exe", "-skipintro"],
            startInfo.ArgumentList);
        Assert.Equal("/Games/Tyranny", startInfo.WorkingDirectory);
        Assert.Equal("CrossOver (Games)", runner.DisplayName);
    }

    [Fact]
    public void Wine_runs_the_game_in_its_prefix()
    {
        var runner = new WineRunner { WineBinary = "/opt/homebrew/bin/wine", Prefix = "/Users/me/.wine-goggles" };

        var startInfo = runner.CreateStartInfo(Target());

        Assert.Equal("/opt/homebrew/bin/wine", startInfo.FileName);
        Assert.Equal(["/Games/Tyranny/Tyranny.exe"], startInfo.ArgumentList);
        Assert.Equal("/Users/me/.wine-goggles", startInfo.Environment["WINEPREFIX"]);
        Assert.Equal("/Games/Tyranny", startInfo.WorkingDirectory);
    }

    [Fact]
    public void Play_task_arguments_are_split_rather_than_passed_as_one()
    {
        var runner = new WineRunner { WineBinary = "/usr/bin/wine", Prefix = "/tmp/prefix" };

        var startInfo = runner.CreateStartInfo(Target("""-config "C:\Program Files\cfg.ini" -windowed"""));

        Assert.Equal(
            ["/Games/Tyranny/Tyranny.exe", "-config", @"C:\Program Files\cfg.ini", "-windowed"],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("\"two words\" plain", new[] { "two words", "plain" })]
    [InlineData("a\\\\b c:\\dir\\", new[] { "a\\\\b", "c:\\dir\\" })]
    [InlineData("\"a\\\"b\"", new[] { "a\"b" })]
    [InlineData("\"a\\\\\" b", new[] { "a\\", "b" })]
    [InlineData("\"say \"\"hi\"\"\"", new[] { "say \"hi\"" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("   ", new string[0])]
    public void Windows_command_lines_split_by_the_c_runtime_rules(string commandLine, string[] expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Split(commandLine));
    }

    [Fact]
    public void A_runner_that_is_no_longer_installed_cannot_launch()
    {
        var executable = Path.Combine(_directory, "Game.exe");
        File.WriteAllText(executable, "MZ");
        var target = new LaunchTarget { Name = "Game", Executable = executable, WorkingDirectory = _directory };
        var runner = new WineRunner { WineBinary = Path.Combine(_directory, "missing-wine"), Prefix = _directory };

        Assert.False(runner.IsAvailable);
        var exception = Assert.Throws<FileNotFoundException>(() => new GameLauncher().Launch(target, runner));
        Assert.Contains("missing-wine", exception.Message);
    }
}
