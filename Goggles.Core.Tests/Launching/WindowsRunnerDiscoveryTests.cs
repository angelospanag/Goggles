using Goggles.Core.Launching;

namespace Goggles.Core.Tests.Launching;

public class WindowsRunnerDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("goggles-discovery-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CrossOverApp => Path.Combine(_root, "CrossOver.app");

    private string Bottles => Path.Combine(_root, "Bottles");

    private string Prefix => Path.Combine(_root, "prefix");

    private void InstallCrossOver(params string[] bottles)
    {
        var bin = Path.Combine(CrossOverApp, "Contents", "SharedSupport", "CrossOver", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "wine"), "#!/bin/sh");

        foreach (var bottle in bottles)
        {
            Directory.CreateDirectory(Path.Combine(Bottles, bottle));
            File.WriteAllText(Path.Combine(Bottles, bottle, "cxbottle.conf"), string.Empty);
        }
    }

    private string InstallWine()
    {
        var wine = Path.Combine(_root, "bin", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(wine)!);
        File.WriteAllText(wine, "#!/bin/sh");
        return wine;
    }

    [Fact]
    public void Finds_each_CrossOver_bottle_in_name_order()
    {
        InstallCrossOver("Steam", "Games");

        var runners = WindowsRunnerDiscovery.Find(CrossOverApp, Bottles, [], Prefix);

        Assert.Equal(["crossover:Games", "crossover:Steam"], runners.Select(runner => runner.Id));
    }

    [Fact]
    public void Ignores_folders_in_the_bottles_directory_that_are_not_bottles()
    {
        InstallCrossOver("Games");
        Directory.CreateDirectory(Path.Combine(Bottles, "not-a-bottle"));

        var runners = WindowsRunnerDiscovery.Find(CrossOverApp, Bottles, [], Prefix);

        Assert.Equal("crossover:Games", Assert.Single(runners).Id);
    }

    [Fact]
    public void Bottles_without_CrossOver_itself_are_not_offered()
    {
        Directory.CreateDirectory(Path.Combine(Bottles, "Games"));
        File.WriteAllText(Path.Combine(Bottles, "Games", "cxbottle.conf"), string.Empty);

        Assert.Empty(WindowsRunnerDiscovery.Find(CrossOverApp, Bottles, [], Prefix));
    }

    [Fact]
    public void Finds_the_first_wine_that_exists_after_CrossOver()
    {
        InstallCrossOver("Games");
        var wine = InstallWine();

        var runners = WindowsRunnerDiscovery.Find(
            CrossOverApp,
            Bottles,
            [Path.Combine(_root, "missing", "wine"), wine],
            Prefix);

        Assert.Equal(2, runners.Count);
        var plain = Assert.IsType<WineRunner>(runners[1]);
        Assert.Equal(wine, plain.WineBinary);
        Assert.Equal(Prefix, plain.Prefix);
    }

    [Fact]
    public void Finds_nothing_on_a_machine_without_either()
    {
        Assert.Empty(WindowsRunnerDiscovery.Find(CrossOverApp, Bottles, [Path.Combine(_root, "wine")], Prefix));
    }
}
