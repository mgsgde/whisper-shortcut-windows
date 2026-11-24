using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class TakeProfileTests
{
    // The Mode enum and the two switches of Take.cs before W10, kept here as the reference.
    private enum LegacyMode { Dictate, Prompt, Template }

    private static LegacyMode LegacyModeOf(RecordingKindName kind) => kind switch
    {
        RecordingKindName.Prompt => LegacyMode.Prompt,
        RecordingKindName.Template => LegacyMode.Template,
        _ => LegacyMode.Dictate,
    };

    private static RecordingKindName LegacyKindOf(LegacyMode mode) => mode switch
    {
        LegacyMode.Prompt => RecordingKindName.Prompt,
        LegacyMode.Template => RecordingKindName.Template,
        _ => RecordingKindName.Dictate,
    };

    [Fact]
    public void OneRowPerKind() =>
        Assert.Equal(Enum.GetValues<RecordingKindName>().OrderBy(k => k),
            TakeProfile.All.Select(p => p.Kind).OrderBy(k => k));

    [Theory]
    [InlineData(RecordingKindName.Dictate)]
    [InlineData(RecordingKindName.Prompt)]
    [InlineData(RecordingKindName.Template)]
    [InlineData(RecordingKindName.Live)]
    public void FamilyMatchesTheLegacyModeSwitch(RecordingKindName kind)
    {
        var profile = TakeProfile.Of(kind);
        Assert.Equal(kind, profile.Kind);
        Assert.Equal(LegacyModeOf(kind).ToString(), profile.Family.ToString());
        Assert.Equal(profile.Family.ToString(), profile.Name); // the log label stays the old mode name
    }

    [Theory]
    [InlineData(HotkeyFamily.Dictate)]
    [InlineData(HotkeyFamily.Prompt)]
    [InlineData(HotkeyFamily.Template)]
    public void ForFamilyMatchesTheLegacyKindSwitch(HotkeyFamily family) =>
        Assert.Equal(LegacyKindOf(Enum.Parse<LegacyMode>(family.ToString())), TakeProfile.ForFamily(family).Kind);

    [Fact]
    public void ColumnsMatchTheBranchesTheyReplaced()
    {
        // Dictate/Live: engines of Dictation only, no selection, no screenshot, 30 min; Prompt: 5 min + card; Template: 30 min.
        foreach (var p in new[] { TakeProfile.Dictate, TakeProfile.Live })
        {
            Assert.False(p.CopiesSelection); Assert.False(p.UsesTextEngine); Assert.False(p.TakesScreenshot);
            Assert.False(p.OffersQuickActions); Assert.Equal(TimeSpan.FromMinutes(30), p.Limit);
        }
        Assert.True(TakeProfile.Prompt.CopiesSelection); Assert.True(TakeProfile.Prompt.UsesTextEngine);
        Assert.True(TakeProfile.Prompt.TakesScreenshot); Assert.True(TakeProfile.Prompt.OffersQuickActions);
        Assert.Equal(TimeSpan.FromMinutes(5), TakeProfile.Prompt.Limit);
        Assert.True(TakeProfile.Template.CopiesSelection); Assert.True(TakeProfile.Template.UsesTextEngine);
        Assert.True(TakeProfile.Template.TakesScreenshot); Assert.False(TakeProfile.Template.OffersQuickActions);
        Assert.Equal(TimeSpan.FromMinutes(30), TakeProfile.Template.Limit);
    }

    [Fact]
    public void PersistedNamesAreUnchanged()
    {
        Assert.Equal("Dictate", RecordingKind.Format(TakeProfile.Dictate.Kind));
        Assert.Equal("Live", RecordingKind.Format(TakeProfile.Live.Kind));
        Assert.Equal("Prompt", RecordingKind.Format(TakeProfile.Prompt.Kind));
        Assert.Equal("Template:x", RecordingKind.Format(TakeProfile.Template.Kind, "x"));
    }
}
