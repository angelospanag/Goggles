using Goggles.Core.Launching;

namespace Goggles.Core.Tests.Launching;

public class RunnerPreferencesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("goggles-prefs-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly WindowsRunner Games = new CrossOverRunner { AppPath = "/CrossOver.app", Bottle = "Games" };

    private static readonly WindowsRunner Steam = new CrossOverRunner { AppPath = "/CrossOver.app", Bottle = "Steam" };

    private static readonly WindowsRunner Wine = new WineRunner { WineBinary = "/usr/bin/wine", Prefix = "/p" };

    private RunnerPreferences Create() => new(Path.Combine(_directory, "nested", "runners.json"));

    [Fact]
    public void Offers_the_first_available_runner_when_nothing_was_chosen()
    {
        Assert.Same(Games, Create().Choose(1, [Games, Wine]));
    }

    [Fact]
    public void Remembers_a_game_s_choice_across_instances()
    {
        Create().Remember(1, Wine);

        Assert.Equal(Wine, Create().Choose(1, [Games, Wine]));
    }

    [Fact]
    public void A_new_game_starts_with_the_last_runner_picked()
    {
        var preferences = Create();
        preferences.Remember(1, Steam);
        preferences.Remember(2, Wine);

        Assert.Equal(Steam, preferences.Choose(1, [Games, Steam, Wine]));
        Assert.Equal(Wine, preferences.Choose(3, [Games, Steam, Wine]));
    }

    [Fact]
    public void A_remembered_runner_that_is_gone_falls_back()
    {
        var preferences = Create();
        preferences.Remember(1, Steam);

        Assert.Same(Games, preferences.Choose(1, [Games, Wine]));
    }

    [Fact]
    public void A_corrupt_file_is_treated_as_no_preference()
    {
        var preferences = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(preferences.Path)!);
        File.WriteAllText(preferences.Path, "{ not json");

        Assert.Same(Games, preferences.Choose(1, [Games]));
    }

    [Fact]
    public void Nothing_is_chosen_when_nothing_is_available()
    {
        Assert.Null(Create().Choose(1, []));
    }
}
