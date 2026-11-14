using Goggles.Core.Api.Gameplay.Models;

namespace Goggles.Core.Tests.Api.Gameplay;

public class AchievementsTests
{
    [Fact]
    public void AllSorted_puts_unlocked_first_then_locked_alphabetically()
    {
        var older = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        var achievements = new Achievements
        {
            Items =
            [
                new Achievement { Key = "locked-b", Name = "Zebra" },
                new Achievement { Key = "unlocked-old", Name = "Old One", UnlockedAt = older },
                new Achievement { Key = "locked-a", Name = "Apple" },
                new Achievement { Key = "unlocked-new", Name = "New One", UnlockedAt = newer },
            ],
        };

        var got = achievements.AllSorted();

        Assert.Equal(
            ["unlocked-new", "unlocked-old", "locked-a", "locked-b"],
            got.Select(item => item.Key));
    }
}
