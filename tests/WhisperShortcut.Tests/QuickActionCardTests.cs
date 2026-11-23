using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class QuickActionCardTests
{
    private static readonly string[] Own = { "Korrigieren", "Klarer", "Kürzer", "Aufgabenliste", "Englisch", "E-Mail", "Angebot" };

    [Fact]
    public void NoUsage_ShowsTheFirstThree() =>
        Assert.Equal(new[] { "Korrigieren", "Klarer", "Kürzer" }, QuickActionCard.Select(Own, new Dictionary<string, int>()));

    [Fact]
    public void MostUsedWin_ButKeepTheSettingsOrder()
    {
        var counts = new Dictionary<string, int> { ["angebot"] = 5, ["e-mail"] = 2, ["korrigieren"] = 2 };
        // Angebot 5, then E-Mail and Korrigieren tie at 2; all three shown in Settings order.
        Assert.Equal(new[] { "Korrigieren", "E-Mail", "Angebot" }, QuickActionCard.Select(Own, counts));
    }

    [Fact]
    public void TiesGoToTheEarlierOwnPrompt()
    {
        var counts = new Dictionary<string, int> { ["angebot"] = 1, ["englisch"] = 1 };
        Assert.Equal(new[] { "Korrigieren", "Englisch", "Angebot" }, QuickActionCard.Select(Own, counts));
    }

    [Fact]
    public void FewerThanThree_ShowsAll() =>
        Assert.Equal(new[] { "A", "B" }, QuickActionCard.Select(new[] { "A", "B" }, new Dictionary<string, int>()));

    [Theory]
    [InlineData("Kürzer fassen.", "kürzer fassen")]
    [InlineData("  Ähm, klarer formulieren!", "klarer formulieren")]
    [InlineData("äh", "")]
    public void NormalizeKey_LikeTheMac(string raw, string key) => Assert.Equal(key, QuickActionCard.NormalizeKey(raw));

    [Fact]
    public void Usage_CountsWithinThirtyDays_AndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "wsw-quick-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            var usage = new QuickActionUsage(path);
            usage.Record("Kürzer fassen", now.AddDays(-40));
            usage.Record("Kürzer fassen.", now.AddDays(-1));
            usage.Record("Als Angebot formulieren", now);
            var counts = new QuickActionUsage(path).Counts(now);
            Assert.Equal(1, counts["kürzer fassen"]);
            Assert.Equal(1, counts["als angebot formulieren"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
