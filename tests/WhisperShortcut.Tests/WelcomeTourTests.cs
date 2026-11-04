using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class WelcomeTourTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void FreshInstall_IsNotCompleted_AndStartsAtTheIntro()
    {
        var s = Settings.Load(NewPath(), new PlainSecretProtector());
        Assert.False(s.WelcomeCompleted);
        Assert.Equal(WelcomeStep.Intro, WelcomeTour.Resume(s.WelcomeStep));
    }

    [Fact]
    public void FieldsRoundTrip()
    {
        var path = NewPath();
        var s = Settings.Load(path, new PlainSecretProtector());
        s.WelcomeStep = (int)WelcomeStep.TryIt;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.False(t.WelcomeCompleted); // closing is not finishing
        Assert.Equal(WelcomeStep.TryIt, WelcomeTour.Resume(t.WelcomeStep));
        t.WelcomeCompleted = true;
        t.Save();
        Assert.True(Settings.Load(path, new PlainSecretProtector()).WelcomeCompleted);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Theory]
    [InlineData("{\"apiKeyProtected\":\"AIzaKEY\"}", true)]
    [InlineData("{\"openAiKeyProtected\":\"sk-KEY\"}", true)]
    [InlineData("{\"offlineMode\":true}", true)]
    [InlineData("{\"glossary\":[\"a\"]}", false)]
    [InlineData("{\"apiKeyProtected\":\"AIzaKEY\",\"welcomeCompleted\":false}", false)]
    public void OldFiles_CountAsCompletedWhenSetUp(string json, bool completed)
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        Assert.Equal(completed, Settings.Load(path, new PlainSecretProtector()).WelcomeCompleted);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Theory]
    [InlineData(-1, WelcomeStep.Intro)]
    [InlineData(0, WelcomeStep.Intro)]
    [InlineData(2, WelcomeStep.Permissions)]
    [InlineData(4, WelcomeStep.Done)]
    [InlineData(9, WelcomeStep.Intro)]
    public void Resume_ClampsTheStoredStep(int stored, WelcomeStep step) => Assert.Equal(step, WelcomeTour.Resume(stored));

    [Fact]
    public void IndexLabel_CountsFromOne() => Assert.Equal("2 von 5", WelcomeTour.IndexLabel(WelcomeStep.Setup));

    [Fact]
    public void Setup_NeedsADictationThatCanRun()
    {
        var s = Settings.Load(NewPath(), new PlainSecretProtector());
        Assert.False(WelcomeTour.TranscriptionReady(s, speechModelInstalled: true)); // Gemini without a key
        Assert.False(WelcomeTour.CanAdvance(WelcomeStep.Setup, false));
        Assert.True(WelcomeTour.CanAdvance(WelcomeStep.Intro, false));
        Assert.True(WelcomeTour.CanAdvance(WelcomeStep.TryIt, false));

        WelcomeTour.ChooseOffline(s);
        Assert.False(WelcomeTour.TranscriptionReady(s, speechModelInstalled: false));
        Assert.True(WelcomeTour.TranscriptionReady(s, speechModelInstalled: true));
    }

    [Fact]
    public void ChooseCloud_TurnsOfflineOff_AndMovesStepsWithoutAKey()
    {
        var s = Settings.Load(NewPath(), new PlainSecretProtector());
        s.OfflineMode = true;
        s.SpeechEngine = Engine.Local;
        s.OpenAiKey = "sk-test";
        WelcomeTour.ChooseCloud(s);
        Assert.False(s.OfflineMode);
        Assert.Equal(Engine.OpenAI, s.SpeechEngine);
        Assert.Equal(Engine.OpenAI, s.TextEngine); // Gemini had no key
        Assert.True(WelcomeTour.TranscriptionReady(s, speechModelInstalled: false));

        s.ApiKey = "AIza-test";
        s.SpeechEngine = Engine.OpenAI;
        WelcomeTour.ChooseCloud(s);
        Assert.Equal(Engine.OpenAI, s.SpeechEngine); // a step that can run stays
    }

    [Fact]
    public void Inferred_FollowsWhatIsSetUp()
    {
        var s = Settings.Load(NewPath(), new PlainSecretProtector());
        Assert.Null(WelcomeTour.Inferred(s, false));
        Assert.Equal(DictationChoice.Offline, WelcomeTour.Inferred(s, true));
        s.ApiKey = "AIza";
        Assert.Equal(DictationChoice.Cloud, WelcomeTour.Inferred(s, true));
        s.OfflineMode = true;
        Assert.Equal(DictationChoice.Offline, WelcomeTour.Inferred(s, false));
    }
}
