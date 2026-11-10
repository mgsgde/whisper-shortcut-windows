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
    public void ChooseOffline_MeetingsFollowDictationAndStayLocal()
    {
        var s = Settings.Load(NewPath(), new PlainSecretProtector());
        s.ApiKey = "key";
        Assert.Equal(Engine.Gemini, s.EffectiveMeetingSpeechEngine);
        WelcomeTour.ChooseOffline(s);
        Assert.Equal(Engine.Local, s.EffectiveMeetingSpeechEngine);

        // An earlier explicit cloud pick is given up by the Offline answer.
        s.MeetingTranscription = s.MeetingTranscription with { Engine = Engine.Gemini };
        s.MeetingSpeechEngineExplicit = true;
        WelcomeTour.ChooseOffline(s);
        Assert.Equal(Engine.Local, s.EffectiveMeetingSpeechEngine);
    }

    [Fact]
    public void ChooseCloud_TurnsOfflineOff_AndMovesStepsWithoutAKey()
    {
        var s = Settings.Load(NewPath(), new PlainSecretProtector());
        s.OfflineMode = true;
        s.Dictate = s.Dictate with { Engine = Engine.Local };
        s.OpenAiKey = "sk-test";
        WelcomeTour.ChooseCloud(s);
        Assert.False(s.OfflineMode);
        Assert.Equal(Engine.OpenAI, s.Dictate.Engine);
        Assert.Equal(Engine.OpenAI, s.Prompt.Engine); // Gemini had no key
        Assert.True(WelcomeTour.TranscriptionReady(s, speechModelInstalled: false));

        s.ApiKey = "AIza-test";
        s.Dictate = s.Dictate with { Engine = Engine.OpenAI };
        WelcomeTour.ChooseCloud(s);
        Assert.Equal(Engine.OpenAI, s.Dictate.Engine); // a step that can run stays
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

public class TranscriptionLanguageTests
{
    [Fact]
    public void Normalize_KeepsKnownCodes_ElseAuto()
    {
        Assert.Equal("de", TranscriptionLanguages.Normalize(" DE "));
        Assert.Equal("auto", TranscriptionLanguages.Normalize(null));
        Assert.Equal("auto", TranscriptionLanguages.Normalize("klingonisch"));
        Assert.Null(TranscriptionLanguages.ForRequest("auto"));
        Assert.Equal("fr", TranscriptionLanguages.ForRequest("fr"));
        Assert.Equal("Automatisch erkennen", TranscriptionLanguages.Name("x"));
        Assert.Equal(24, TranscriptionLanguages.All.Count); // auto + 23 like the Mac
    }

    [Fact]
    public void Setting_RoundTrips_AndOldFilesAreAuto()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("auto", s.TranscriptionLanguage);
        s.TranscriptionLanguage = "it";
        s.Save();
        Assert.Equal("it", Settings.Load(path, new PlainSecretProtector()).TranscriptionLanguage);
        File.WriteAllText(path, "{\"glossary\":[]}");
        Assert.Equal("auto", Settings.Load(path, new PlainSecretProtector()).TranscriptionLanguage);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class TranscriptionTuningTests
{
    [Fact]
    public void Defaults_AreTheMacs()
    {
        var s = Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"), new PlainSecretProtector());
        Assert.Equal(0.0, s.TranscriptionTemperature);
        Assert.Equal("minimal", s.TranscriptionThinking);
    }

    [Fact]
    public void Normalize_SnapsToOfferedValues()
    {
        Assert.Equal(0.2, TranscriptionTuning.NormalizeTemperature(0.25));
        Assert.Equal(1.0, TranscriptionTuning.NormalizeTemperature(2.0));
        Assert.Equal(0.0, TranscriptionTuning.NormalizeTemperature(null));
        Assert.Equal(0.0, TranscriptionTuning.NormalizeTemperature(double.NaN));
        Assert.Equal("medium", TranscriptionTuning.NormalizeThinking(" Medium "));
        Assert.Equal("minimal", TranscriptionTuning.NormalizeThinking("maximal"));
        Assert.Equal("low", TranscriptionTuning.ThinkingFor("minimal", modelRejectsMinimal: true));
        Assert.Equal("high", TranscriptionTuning.ThinkingFor("high", modelRejectsMinimal: true));
        Assert.Equal("minimal", TranscriptionTuning.ThinkingFor("minimal", modelRejectsMinimal: false));
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.TranscriptionTemperature = 0.5;
        s.TranscriptionThinking = "high";
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(0.5, t.TranscriptionTemperature);
        Assert.Equal("high", t.TranscriptionThinking);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class RecordingReplayTests
{
    [Fact]
    public void TemplateReplay_StaysTenMinutes_ThenIsGone()
    {
        var r = new TemplateReplay();
        var t0 = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        Assert.Null(r.Current(t0));
        r.Keep(new byte[] { 1, 2 }, 3.5, " Befund ", t0);
        Assert.Equal("Befund", r.Current(t0.AddMinutes(9))!.Value.TemplateName);
        Assert.False(r.DropIfExpired(t0.AddMinutes(9.9)));
        Assert.True(r.DropIfExpired(t0.AddMinutes(10)));
        Assert.Null(r.Current(t0.AddMinutes(10)));
        r.Keep(new byte[] { 3 }, 1, "", t0);
        Assert.Equal("Vorlage", r.Current(t0)!.Value.TemplateName);
        r.Clear();
        Assert.Null(r.Current(t0));
    }

    [Fact]
    public void CancelledRecording_KeepsOnlyOne_AndTakeEmptiesIt()
    {
        var c = new CancelledRecording();
        c.Keep(Array.Empty<byte>(), 0);
        Assert.False(c.HasRecording);
        c.Keep(new byte[] { 1 }, 1);
        c.Keep(new byte[] { 2, 2 }, 2);
        Assert.True(c.HasRecording);
        var taken = c.Take();
        Assert.Equal(new byte[] { 2, 2 }, taken!.Value.Wav);
        Assert.False(c.HasRecording);
        Assert.Null(c.Take());
    }
}
