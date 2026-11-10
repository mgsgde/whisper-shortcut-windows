using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SettingsTests
{
    private sealed class Reverse : ISecretProtector
    {
        public string Protect(string plain) => new(plain.Reverse().ToArray());
        public string? Unprotect(string stored) => new(stored.Reverse().ToArray());
    }

    [Fact]
    public void RoundTrip_KeyIsNotPlainOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new Reverse());
        Assert.Equal("Alt+1", s.DictateHotkey);
        Assert.Equal("Alt+2", s.PromptHotkey);
        Assert.Equal("Alt+7", s.GlossaryHotkey);
        Assert.Equal("gemini-3.1-flash-lite", s.TranscriptionModel);
        Assert.Equal("gemini-3.8-flash", s.PromptModel);

        s.ApiKey = "abc123SECRET";
        s.Glossary = new() { "Kubernetes", "Müller" };
        s.DictatePromptTemplate = "Mein Prompt";
        s.DictateHotkey = "Ctrl+Shift+X";
        s.GlossaryHotkey = "Ctrl+Alt+G";
        s.Save();

        Assert.DoesNotContain("abc123SECRET", File.ReadAllText(path));
        // '+' is written as \u002B, so the raw hotkey text is not in the file. The name is.
        Assert.Contains("\"glossaryHotkey\":", File.ReadAllText(path));
        var t = Settings.Load(path, new Reverse());
        Assert.Equal("abc123SECRET", t.ApiKey);
        Assert.Equal(new[] { "Kubernetes", "Müller" }, t.Glossary);
        Assert.Equal("Mein Prompt", t.DictatePromptTemplate);
        Assert.Equal("Ctrl+Shift+X", t.DictateHotkey);
        Assert.Equal("Ctrl+Alt+G", t.GlossaryHotkey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void OfflineSettings_DefaultsAndRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var old = Settings.Load(path, new PlainSecretProtector());
        Assert.False(old.OfflineMode);
        Assert.Equal("", old.OfflineLlmId);
        Assert.Equal("Ctrl+Shift+K", old.GlossaryHotkey);

        old.OfflineMode = true;
        old.OfflineLlmId = "qwen3-4b-instruct-2507";
        old.Save();
        Assert.Contains("\"offlineMode\": true", File.ReadAllText(path));
        Assert.Contains("\"offlineLlmId\": \"qwen3-4b-instruct-2507\"", File.ReadAllText(path));
        Assert.Contains("\"glossaryHotkey\":", File.ReadAllText(path));
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.True(t.OfflineMode);
        Assert.Equal("qwen3-4b-instruct-2507", t.OfflineLlmId);
        Assert.Equal("Ctrl+Shift+K", t.GlossaryHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptFile_GivesDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ nope");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.ApiKey);
        Assert.True(File.Exists(path + ".bak"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void DictateMode_DefaultsToDictateAndRoundTrips()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}"); // an old file without the key
        var s = Settings.Load(path, new Reverse());
        Assert.Equal(DictateMode.Dictate, s.DictateMode);

        s.DictateMode = DictateMode.Live;
        s.Save();
        Assert.Contains("\"dictateMode\": \"live\"", File.ReadAllText(path));
        Assert.Equal(DictateMode.Live, Settings.Load(path, new Reverse()).DictateMode);

        File.WriteAllText(path, "{\"dictateMode\": \" LIVE \"}");
        Assert.Equal(DictateMode.Live, Settings.Load(path, new Reverse()).DictateMode);
        File.WriteAllText(path, "{\"dictateMode\": \"quatsch\"}");
        Assert.Equal(DictateMode.Dictate, Settings.Load(path, new Reverse()).DictateMode);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void PunctuationMode_DefaultsToAutomaticAndRoundTrips()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        var s = Settings.Load(path, new Reverse());
        Assert.Equal(PunctuationMode.Automatic, s.PunctuationMode);

        s.PunctuationMode = PunctuationMode.SpokenOnly;
        s.Save();
        Assert.Contains("\"punctuationMode\": \"spokenOnly\"", File.ReadAllText(path));
        Assert.Equal(PunctuationMode.SpokenOnly, Settings.Load(path, new Reverse()).PunctuationMode);

        File.WriteAllText(path, "{\"punctuationMode\": \"  SPOKENONLY \"}");
        Assert.Equal(PunctuationMode.SpokenOnly, Settings.Load(path, new Reverse()).PunctuationMode);
        File.WriteAllText(path, "{\"punctuationMode\": \"quatsch\"}");
        Assert.Equal(PunctuationMode.Automatic, Settings.Load(path, new Reverse()).PunctuationMode);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    private static string Tmp() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void RoundTrip_CustomPrompts()
    {
        var path = Tmp();
        var s = Settings.Load(path, new Reverse());
        s.DictateSystemPrompt = "Schreib alles klein.";
        s.PromptModeSystemPrompt = "Formuliere knapp.";
        s.Save();
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("Schreib alles klein.", l.DictateSystemPrompt);
        Assert.Equal("Formuliere knapp.", l.PromptModeSystemPrompt);
    }

    [Fact]
    public void OldFile_WithoutPromptFields_LoadsEmpty()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"promptSystemPrompt\":\"Vorlage\"}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("", l.DictateSystemPrompt);
        Assert.Equal("", l.PromptModeSystemPrompt);
        Assert.Equal("Vorlage", l.DictatePromptTemplate);
    }

    [Fact]
    public void Load_ReadsLegacyPromptSystemPrompt()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"promptSystemPrompt\":\"Alt\"}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("Alt", l.DictatePromptTemplate);
        l.Save();
        var text = File.ReadAllText(path);
        Assert.Contains("dictatePromptTemplate", text);
        Assert.DoesNotContain("\"promptSystemPrompt\"", text);
    }

    [Fact]
    public void Load_NewKeyWinsOverLegacy()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"dictatePromptTemplate\":\"Neu\",\"promptSystemPrompt\":\"Alt\"}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("Neu", l.DictatePromptTemplate);
    }

    [Fact]
    public void Load_NormalizesModels()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"promptModel\":\"gemini-3.7-flash\",\"transcriptionModel\":\"quatsch\",\"openAiPromptModel\":\"gpt-5\",\"openAiTranscriptionModel\":\"whisper-1\"}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal("gemini-3.8-flash", l.PromptModel);
        Assert.Equal(Settings.DefaultTranscriptionModel, l.TranscriptionModel);
        Assert.Equal("gpt-6-sol", l.OpenAiPromptModel);
        Assert.Equal("gpt-4o-transcribe", l.OpenAiTranscriptionModel);
    }

    [Fact]
    public void Load_DropsNullGlossaryEntries_SoAddingATermDoesNotCrash()
    {
        var path = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"glossary\":[null,\"Ramipril\",null]}");
        var l = Settings.Load(path, new Reverse());
        Assert.Equal(new[] { "Ramipril" }, l.Glossary);
        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(l.Glossary, "Goedde", out _));
        Assert.Equal(new[] { "Ramipril", "Goedde" }, l.Glossary);
    }

    [Fact]
    public void CustomOrEmpty_Cases()
    {
        var def = "Zeile eins\nZeile zwei";
        Assert.Equal("", Settings.CustomOrEmpty("Zeile eins\r\nZeile zwei\r\n", def));
        Assert.Equal("", Settings.CustomOrEmpty("  \r\n ", def));
        Assert.Equal("", Settings.CustomOrEmpty(null, def));
        Assert.Equal("a\nb", Settings.CustomOrEmpty("  a\r\nb \r\n", def));
    }

    private static string TempSettingsFile(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Engines_OldFile_DefaultToGemini_OfflineUnchanged()
    {
        var path = TempSettingsFile("{\"offlineMode\":true,\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.Gemini, s.SpeechEngine);
        Assert.Equal(Engine.Gemini, s.TextEngine);
        Assert.True(s.OfflineMode);
        Assert.Equal("", s.OpenAiKey);
        Assert.Equal("gpt-4o-transcribe", s.OpenAiTranscriptionModel);
        Assert.Equal("gpt-6-sol", s.OpenAiPromptModel);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Engines_UnknownValue_IsGemini()
    {
        var path = TempSettingsFile("{\"speechEngine\":\"azure\",\"textEngine\":\"\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.Gemini, s.SpeechEngine);
        Assert.Equal(Engine.Gemini, s.TextEngine);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Engines_AndOpenAi_RoundTrip_KeyProtected()
    {
        var path = TempSettingsFile("{}");
        var s = Settings.Load(path, new Reverse());
        s.SpeechEngine = Engine.Local;
        s.TextEngine = Engine.OpenAI;
        s.OpenAiKey = "sk-SECRET123";
        s.OpenAiTranscriptionModel = "gpt-4o-mini-transcribe";
        s.OpenAiPromptModel = "gpt-6-luna";
        s.Save();

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-SECRET123", raw);
        Assert.Contains("\"speechEngine\": \"local\"", raw);
        Assert.Contains("\"textEngine\": \"openai\"", raw);

        var t = Settings.Load(path, new Reverse());
        Assert.Equal(Engine.Local, t.SpeechEngine);
        Assert.Equal(Engine.OpenAI, t.TextEngine);
        Assert.Equal("sk-SECRET123", t.OpenAiKey);
        Assert.Equal("gpt-4o-mini-transcribe", t.OpenAiTranscriptionModel);
        Assert.Equal("gpt-6-luna", t.OpenAiPromptModel);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Load_MigratesPlaintextKeysAndRewritesFile()
    {
        var path = TempSettingsFile("""{"apiKey":"AIzaSyA1234567890abcdefghijklmnop","openAiKey":"sk-abcdefghijklmnopqrstuvwxyz123456"}""");
        var s = Settings.Load(path, new Reverse());
        Assert.Equal("AIzaSyA1234567890abcdefghijklmnop", s.ApiKey);
        Assert.Equal("sk-abcdefghijklmnopqrstuvwxyz123456", s.OpenAiKey);
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("AIzaSyA1234567890", raw);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz123456", raw);
        Assert.Contains("apiKeyProtected", raw);
        Assert.Contains("openAiKeyProtected", raw);
        Assert.DoesNotContain("\"apiKey\":", raw);
        Assert.DoesNotContain("\"openAiKey\":", raw);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void EffectiveEngines_OfflineMode_OverridesBoth()
    {
        var path = TempSettingsFile("{}");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.SpeechEngine = Engine.OpenAI;
        s.TextEngine = Engine.Gemini;
        Assert.Equal(Engine.OpenAI, s.EffectiveSpeechEngine);
        Assert.Equal(Engine.Gemini, s.EffectiveTextEngine);
        s.OfflineMode = true;
        Assert.Equal(Engine.Local, s.EffectiveSpeechEngine);
        Assert.Equal(Engine.Local, s.EffectiveTextEngine);
        s.Save();
        Assert.DoesNotContain("effective", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void MissingFile_GivesDefaults_WithoutCreatingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.ApiKey);
        Assert.Equal("", s.OpenAiKey);
        Assert.Equal("Alt+1", s.DictateHotkey);
        Assert.Equal(Engine.Gemini, s.SpeechEngine);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void UnprotectFailure_ClearsTheKey()
    {
        var path = TempSettingsFile("{\"apiKeyProtected\":\"x\",\"openAiKeyProtected\":\"y\"}");
        var s = Settings.Load(path, new ThrowingUnprotect());
        Assert.Equal("", s.ApiKey);
        Assert.Equal("", s.OpenAiKey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void UnprotectNull_ClearsTheKey()
    {
        var path = TempSettingsFile("{\"apiKeyProtected\":\"x\",\"openAiKeyProtected\":\"y\"}");
        var s = Settings.Load(path, new NullUnprotect());
        Assert.Equal("", s.ApiKey);
        Assert.Equal("", s.OpenAiKey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void EmptyKeys_AreStoredEmpty_NotProtected()
    {
        var path = TempSettingsFile("{}");
        var s = Settings.Load(path, new Reverse());
        s.ApiKey = "";
        s.OpenAiKey = "";
        s.Save();
        var raw = File.ReadAllText(path);
        Assert.Contains("\"apiKeyProtected\": \"\"", raw);
        Assert.Contains("\"openAiKeyProtected\": \"\"", raw);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void WhitespaceOnlyHotkeys_KeepDefaults()
    {
        var path = TempSettingsFile("{\"dictateHotkey\":\"  \",\"promptHotkey\":\"\",\"glossaryHotkey\":\"\\t\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+D", s.DictateHotkey);
        Assert.Equal("Ctrl+Shift+P", s.PromptHotkey);
        Assert.Equal("Ctrl+Shift+K", s.GlossaryHotkey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    private sealed class ThrowingUnprotect : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string? Unprotect(string stored) => throw new InvalidOperationException("cannot decrypt");
    }

    private sealed class NullUnprotect : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string? Unprotect(string stored) => null;
    }
}

public class LogAndAudioTests
{
    [Fact]
    public async Task FileAudioSource_ReturnsFileBytes_AndWavDuration()
    {
        var path = Paths.TestAudio("diktat.wav");
        if (!File.Exists(path)) return;
        var src = new FileAudioSource(path);
        src.Start();
        var wav = await src.StopAsync();
        Assert.Equal(File.ReadAllBytes(path), wav);
        Assert.InRange(Wav.DurationSeconds(wav), 0.5, 120);
    }

    [Fact]
    public void AudioSilentException_HasGermanMicMessage()
    {
        var ex = new AudioSilentException();
        Assert.Contains("Mikrofon liefert Stille", ex.Message);
        Assert.Contains("Datenschutz", ex.Message);
    }

    [Fact]
    public void DurationSeconds_UnreadableOrNoFmt_IsZero()
    {
        Assert.Equal(0, Wav.DurationSeconds(new byte[8]));
        Assert.Equal(0, Wav.DurationSeconds(new byte[20]));
        var speech = TestWav.Speech();
        Assert.InRange(Wav.DurationSeconds(speech), 1.2, 1.4);
        Assert.Throws<InvalidDataException>(() => Wav.ReadMonoFloat("RIFFxxxxWAVE"u8.ToArray(), out _));
    }

    [Fact]
    public void Log_WritesLines_AndIsNoopBeforeInit()
    {
        Log.Init(null);
        Log.Info("ignored");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "wsw.log");
        Log.Init(path);
        Log.Info("hello");
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { Log.Error("bad", ex); }
        Log.Info("secret AIzaSyA1234567890abcdefghijklmnop");
        Log.Init(null);
        // Log is static and other test classes run in parallel: their lines may land here too, so look for ours only.
        var lines = File.ReadAllLines(path);
        Assert.DoesNotContain(lines, l => l.Contains("ignored"));
        int hello = Array.FindIndex(lines, l => l.Contains("INFO hello"));
        int bad = Array.FindIndex(lines, l => l.Contains("ERROR bad (InvalidOperationException: boom)"));
        Assert.True(hello >= 0 && bad > hello);
        Assert.Contains("Log_WritesLines_AndIsNoopBeforeInit", lines[bad]);
        Assert.DoesNotContain(lines, l => l.Contains("AIzaSyA1234567890"));
        Assert.Contains(lines, l => l.Contains("secret AIza…"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Log_RotatesWhenFileExceedsLimit()
    {
        Log.Init(null);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "wsw.log");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, new byte[Log.MaxFileBytes]);
        Log.Init(path);
        Log.Info("after-rotate");
        Log.Init(null);
        Assert.True(File.Exists(path + ".1"));
        Assert.Equal(Log.MaxFileBytes, new FileInfo(path + ".1").Length);
        Assert.Contains("after-rotate", File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Sounds_off_for_new_installs_on_for_old_files_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.False(s.Sounds);

        s.Sounds = false;
        s.Save();
        Assert.False(Settings.Load(path, new PlainSecretProtector()).Sounds);

        File.WriteAllText(path, "{\"glossary\":[\"a\"]}"); // old file without the field
        Assert.True(Settings.Load(path, new PlainSecretProtector()).Sounds);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class QuickActionTests
{
    private static string TempFile(string? json)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        if (json != null) File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void OldFile_GetsDefaults()
    {
        var path = TempFile("{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(QuickActionList.Defaults, s.QuickActions);
        Assert.Equal(5, s.QuickActions.Count);
    }

    private static readonly string[] OldDefaults =
    {
        "Als Anamnese, Befund und Therapie gliedern",
        "In Stichpunkten zusammenfassen",
        "Rechtschreibung und Grammatik korrigieren",
        "Kürzer fassen",
        "Freundlicher formulieren",
    };

    [Fact]
    public void OldMedicalDefaults_MigrateToNewDefaults()
    {
        var path = TempFile(System.Text.Json.JsonSerializer.Serialize(new { quickActions = OldDefaults }));
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(QuickActionList.Defaults, s.QuickActions);
        Assert.DoesNotContain("Als Anamnese, Befund und Therapie gliedern", s.QuickActions);
    }

    [Fact]
    public void CustomisedOldDefaults_AreKept()
    {
        var custom = (string[])OldDefaults.Clone();
        custom[4] = "Förmlicher formulieren";
        var path = TempFile(System.Text.Json.JsonSerializer.Serialize(new { quickActions = custom }));
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(custom, s.QuickActions);
    }

    [Fact]
    public void RoundTrip_KeepsCustomList()
    {
        var path = TempFile(null);
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(QuickActionList.Defaults, s.QuickActions);
        s.QuickActions = new() { "Eins", "Zwei" };
        s.Save();
        Assert.Contains("\"quickActions\":", File.ReadAllText(path));
        Assert.Equal(new[] { "Eins", "Zwei" }, Settings.Load(path, new PlainSecretProtector()).QuickActions);
    }

    [Fact]
    public void ExplicitEmptyList_StaysEmpty()
    {
        var path = TempFile(null);
        var s = Settings.Load(path, new PlainSecretProtector());
        s.QuickActions = new();
        s.Save();
        Assert.Empty(Settings.Load(path, new PlainSecretProtector()).QuickActions);
    }

    [Fact]
    public void Clean_TrimsCollapsesSkipsDedupesAndCaps()
    {
        var cleaned = QuickActionList.Clean(new string?[]
        {
            "  Kürzer   fassen ", "", "   ", null, "kürzer fassen", new string('x', 81), new string('y', 80),
            "A", "B", "C", "D", "E", "F", "G", "H", "I",
        });
        Assert.Equal(QuickActionList.Max, cleaned.Count);
        Assert.Equal(new[] { "Kürzer fassen", new string('y', 80), "A", "B", "C", "D", "E", "F", "G" }, cleaned);
    }
}

public class QuickActionRequestTests
{
    [Fact]
    public void SelectionAndBlankTranscript_UsesActionAlone() =>
        Assert.Equal(("Text", "Kürzer fassen", false), QuickActionRequest.Build("Kürzer fassen", "Text", "  ")!.Value);

    [Fact]
    public void SelectionAndTranscript_AppendsWhatWasSaid() =>
        Assert.Equal(("Text", "Kürzer fassen\n\nZusätzlich gesagt: bitte knapp", false),
            QuickActionRequest.Build("Kürzer fassen", "Text", " bitte knapp ")!.Value);

    [Fact]
    public void NoSelection_DictationBecomesTheText() =>
        Assert.Equal(("Patient klagt", "Kürzer fassen", true),
            QuickActionRequest.Build("Kürzer fassen", null, " Patient klagt ")!.Value);

    [Fact]
    public void NoSelection_IsComposeTurn_NoHistoryGoesAlong()
    {
        var r = QuickActionRequest.Build("Kürzer fassen", null, " Patient klagt ")!.Value;
        Assert.True(r.FromDictation);
        Assert.Equal("Patient klagt", r.Selection);
        Assert.False(PromptConversationMemory.Includes(isCompose: r.FromDictation, offline: false));
    }

    [Fact]
    public void WithSelection_IsNotFromDictation()
    {
        Assert.False(QuickActionRequest.Build("Kürzer fassen", "Text", "")!.Value.FromDictation);
        Assert.False(QuickActionRequest.Build("Kürzer fassen", "Text", "bitte knapp")!.Value.FromDictation);
    }

    [Fact]
    public void NothingToApplyTo_IsNull()
    {
        Assert.Null(QuickActionRequest.Build("Kürzer fassen", "", " "));
        Assert.Null(QuickActionRequest.Build("Kürzer fassen", null, ""));
    }

    [Fact]
    public void Microphone_DefaultsEmptyAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.Microphone);

        s.Microphone = "Headset (USB Audio)";
        s.Save();
        Assert.Contains("\"microphone\": \"Headset (USB Audio)\"", File.ReadAllText(path));
        Assert.Equal("Headset (USB Audio)", Settings.Load(path, new PlainSecretProtector()).Microphone);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PasteOptions_Defaults_RoundTrip_AndOldFileLoadsAsRestore()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.True(s.AutoPaste);
        // New installs leave the dictation on the clipboard, like the Mac.
        Assert.Equal(ClipboardAfterPaste.KeepDictation, s.ClipboardAfterPaste);
        Assert.False(s.RestoreClipboard);

        s.AutoPaste = false;
        s.ClipboardAfterPaste = ClipboardAfterPaste.Clear;
        s.Save();
        Assert.Contains("\"clipboardAfterPaste\": \"clear\"", File.ReadAllText(path));
        Assert.Contains("\"restoreClipboard\": false", File.ReadAllText(path)); // still written for older versions
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.False(t.AutoPaste);
        Assert.Equal(ClipboardAfterPaste.Clear, t.ClipboardAfterPaste);

        t.ClipboardAfterPaste = ClipboardAfterPaste.RestorePrevious;
        t.Save();
        Assert.Contains("\"restoreClipboard\": true", File.ReadAllText(path));
        Assert.Equal(ClipboardAfterPaste.RestorePrevious, Settings.Load(path, new PlainSecretProtector()).ClipboardAfterPaste);

        File.WriteAllText(path, "{\"glossary\":[\"a\"]}"); // old file without the fields: restore was the old default
        var u = Settings.Load(path, new PlainSecretProtector());
        Assert.True(u.AutoPaste);
        Assert.Equal(ClipboardAfterPaste.RestorePrevious, u.ClipboardAfterPaste);

        File.WriteAllText(path, "{\"restoreClipboard\":false}"); // old file with the switch off
        Assert.Equal(ClipboardAfterPaste.KeepDictation, Settings.Load(path, new PlainSecretProtector()).ClipboardAfterPaste);
        File.WriteAllText(path, "{\"restoreClipboard\":true}");
        Assert.Equal(ClipboardAfterPaste.RestorePrevious, Settings.Load(path, new PlainSecretProtector()).ClipboardAfterPaste);
        // The new field wins over the old switch; an unknown value falls back to the switch.
        File.WriteAllText(path, "{\"restoreClipboard\":true,\"clipboardAfterPaste\":\"keep\"}");
        Assert.Equal(ClipboardAfterPaste.KeepDictation, Settings.Load(path, new PlainSecretProtector()).ClipboardAfterPaste);
        File.WriteAllText(path, "{\"restoreClipboard\":false,\"clipboardAfterPaste\":\"later\"}");
        Assert.Equal(ClipboardAfterPaste.KeepDictation, Settings.Load(path, new PlainSecretProtector()).ClipboardAfterPaste);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void RestoreClipboard_IsAViewOfTheMode()
    {
        var s = Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"), new PlainSecretProtector());
        s.RestoreClipboard = true;
        Assert.Equal(ClipboardAfterPaste.RestorePrevious, s.ClipboardAfterPaste);
        s.RestoreClipboard = false;
        Assert.Equal(ClipboardAfterPaste.KeepDictation, s.ClipboardAfterPaste);
        s.ClipboardAfterPaste = ClipboardAfterPaste.Clear;
        s.RestoreClipboard = false; // "off" leaves "clear" alone
        Assert.Equal(ClipboardAfterPaste.Clear, s.ClipboardAfterPaste);
        Assert.False(s.RestoreClipboard);
    }

    [Fact]
    public void AutoUpdateCheck_defaults_on_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.True(s.AutoUpdateCheck);

        s.AutoUpdateCheck = false;
        s.Save();
        Assert.False(Settings.Load(path, new PlainSecretProtector()).AutoUpdateCheck);

        File.WriteAllText(path, "{\"glossary\":[\"a\"]}");
        Assert.True(Settings.Load(path, new PlainSecretProtector()).AutoUpdateCheck);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    private static string TempSettingsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    [Fact]
    public void Templates_RoundTrip()
    {
        var path = TempSettingsPath();
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Empty(s.Templates);
        Assert.Equal("", s.TemplateHotkey);
        Assert.Equal("", s.LastTemplateId);

        var a = new DictationTemplate { Name = "Anamnese", Instructions = "Zeile 1\nZeile 2\n\nZeile 4", Hotkey = "Ctrl+Shift+1" };
        var b = new DictationTemplate { Name = "Brief", Instructions = "Schreibe einen Brief.", Hotkey = "" };
        s.Templates = new() { a, b };
        s.TemplateHotkey = "Ctrl+Shift+T";
        s.LastTemplateId = b.Id;
        s.Save();

        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(2, t.Templates.Count);
        Assert.Equal(a.Id, t.Templates[0].Id);
        Assert.Equal("Anamnese", t.Templates[0].Name);
        Assert.Equal("Zeile 1\nZeile 2\n\nZeile 4", t.Templates[0].Instructions);
        Assert.Equal("Ctrl+Shift+1", t.Templates[0].Hotkey);
        Assert.Equal(b.Id, t.Templates[1].Id);
        Assert.Equal("", t.Templates[1].Hotkey);
        Assert.Equal("Ctrl+Shift+T", t.TemplateHotkey);
        Assert.Equal(b.Id, t.LastTemplateId);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Templates_OldFileWithoutTheKeys_LoadsEmptyAndKeepsOtherFields()
    {
        var path = TempSettingsPath();
        File.WriteAllText(path, "{\"dictateHotkey\":\"Ctrl+Shift+X\",\"glossary\":[\"Müller\"],\"sounds\":false}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Empty(s.Templates);
        Assert.Equal("", s.TemplateHotkey);
        Assert.Equal("", s.LastTemplateId);
        Assert.Equal("Ctrl+Shift+X", s.DictateHotkey);
        Assert.Equal(new[] { "Müller" }, s.Glossary);
        Assert.False(s.Sounds);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Templates_EntryWithMissingIdAndNullFields_GetsFreshIdAndEmptyStrings()
    {
        var path = TempSettingsPath();
        File.WriteAllText(path, "{\"templates\":[null,{\"name\":null,\"instructions\":null,\"hotkey\":null},{\"id\":\"  \",\"name\":\"X\",\"instructions\":\"a\\r\\nb\"}],\"templateHotkey\":null,\"glossary\":[\"G\"]}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(2, s.Templates.Count);
        Assert.True(Guid.TryParse(s.Templates[0].Id, out _));
        Assert.Equal("", s.Templates[0].Name);
        Assert.Equal("", s.Templates[0].Instructions);
        Assert.Equal("", s.Templates[0].Hotkey);
        Assert.True(Guid.TryParse(s.Templates[1].Id, out _));
        Assert.NotEqual(s.Templates[0].Id, s.Templates[1].Id);
        Assert.Equal("a\nb", s.Templates[1].Instructions);
        Assert.Equal("", s.TemplateHotkey);
        Assert.Equal(new[] { "G" }, s.Glossary);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Screenshots_DefaultsOffAndEmpty_OldFileLoadsDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"dictateHotkey\":\"Ctrl+Shift+X\"}");
        var old = Settings.Load(path, new PlainSecretProtector());
        Assert.False(old.SendScreenshots);
        Assert.Equal("", old.ScreenshotHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Screenshots_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.SendScreenshots = true;
        s.ScreenshotHotkey = "Ctrl+Shift+S";
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.True(t.SendScreenshots);
        Assert.Equal("Ctrl+Shift+S", t.ScreenshotHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Meeting_settings_default_round_trip_and_unknown_mode()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.MeetingHotkey);
        Assert.Equal("auto", s.MeetingAudioMode);
        Assert.True(s.MeetingSummary);

        s.MeetingHotkey = "Ctrl+Shift+M";
        s.MeetingAudioMode = "mix";
        s.MeetingSummary = false;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Ctrl+Shift+M", t.MeetingHotkey);
        Assert.Equal("mix", t.MeetingAudioMode);
        Assert.False(t.MeetingSummary);

        File.WriteAllText(path, "{\"meetingAudioMode\":\"bogus\",\"meetingHotkey\":\"\"}");
        var u = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("auto", u.MeetingAudioMode);
        Assert.Equal("", u.MeetingHotkey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void LiveDictateHotkey_defaults_to_suggestion_switched_off_and_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Alt+5", s.LiveDictateHotkey);
        Assert.Equal("", s.ActiveLiveDictateHotkey);

        s.LiveDictateHotkey = "Ctrl+Alt+L";
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Ctrl+Alt+L", t.LiveDictateHotkey);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
    [Fact]
    public void ReadAloud_WindowsVoiceAndRewritePrompt_DefaultsAndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("", s.ReadAloudVoiceSystem);
        Assert.Equal("", s.ReadAloudRewritePrompt);
        Assert.Equal(ReadAloud.DefaultRewritePrompt, s.EffectiveReadAloudRewritePrompt);
        Assert.Equal(ReadAloud.DefaultModel, s.EffectiveReadAloudModel.Id);
        s.OfflineMode = true;
        Assert.Equal(TtsProvider.System, s.EffectiveReadAloudModel.Provider);

        s.ReadAloudVoiceSystem = "Microsoft Hedda Desktop";
        s.ReadAloudRewritePrompt = "Lies es kurz vor.";
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Microsoft Hedda Desktop", t.ReadAloudVoiceFor(TtsProvider.System));
        Assert.Equal("Lies es kurz vor.", t.EffectiveReadAloudRewritePrompt);

        // Saving the default text stores "", so a later improvement of the default reaches the user.
        t.ReadAloudRewritePrompt = ReadAloud.DefaultRewritePrompt;
        t.Save();
        Assert.Equal("", Settings.Load(path, new PlainSecretProtector()).ReadAloudRewritePrompt);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Meetings_NewInstall_MacDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.Gemini, s.MeetingSpeechEngine);
        Assert.Equal(Settings.DefaultTranscriptionModel, s.MeetingTranscriptionModel);
        Assert.Equal("gemini-3.5-flash-lite", s.MeetingSummaryModel);
        Assert.Equal(60, s.MeetingChunkSeconds);
        Assert.Equal(90, s.MeetingSafeguardMinutes);
        s.OfflineMode = true;
        Assert.Equal(Engine.Local, s.EffectiveMeetingSpeechEngine);
        Assert.Equal(Engine.Local, s.EffectiveMeetingTextEngine);
    }

    [Fact]
    public void Meetings_OlderFile_KeepsTheDictationProviders()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """
            { "speechEngine": "openai", "textEngine": "local", "openAiTranscriptionModel": "gpt-4o-mini-transcribe",
              "transcriptionModel": "gemini-3.8-flash", "openAiPromptModel": "gpt-6-luna" }
            """);
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.OpenAI, s.MeetingSpeechEngine);
        Assert.Equal("gpt-4o-mini-transcribe", s.MeetingOpenAiTranscriptionModel);
        Assert.Equal("gemini-3.8-flash", s.MeetingTranscriptionModel);
        Assert.Equal(Engine.Local, s.MeetingTextEngine);
        Assert.Equal("gpt-6-luna", s.MeetingOpenAiSummaryModel);
        Assert.Equal("gemini-3.5-flash-lite", s.MeetingSummaryModel);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Meetings_RoundTrip_AndUnknownValuesFallBack()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.MeetingSpeechEngine = Engine.Local;
        s.MeetingTextEngine = Engine.OpenAI;
        s.MeetingOpenAiSummaryModel = "gpt-5.4-mini";
        s.MeetingChunkSeconds = 30;
        s.MeetingSafeguardMinutes = 0;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.Local, t.MeetingSpeechEngine);
        Assert.Equal(Engine.OpenAI, t.MeetingTextEngine);
        Assert.Equal("gpt-5.4-mini", t.MeetingOpenAiSummaryModel);
        Assert.Equal(30, t.MeetingChunkSeconds);
        Assert.Equal(0, t.MeetingSafeguardMinutes);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"meetingChunkSeconds\": 30", "\"meetingChunkSeconds\": 25"));
        Assert.Equal(60, Settings.Load(path, new PlainSecretProtector()).MeetingChunkSeconds);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Chat_NewInstall_MacDefaults_AndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Alt+6", s.ActiveChatHotkey);
        Assert.Equal(Engine.Gemini, s.EffectiveChatEngine);
        Assert.Equal("gemini-3.8-flash", s.EffectiveChatModel);
        Assert.True(s.ChatWebSearch);
        Assert.True(s.ChatCloseOnFocusLoss);
        Assert.Equal("", s.ChatSystemPrompt);

        s.ChatEngine = Engine.OpenAI;
        s.ChatOpenAiModel = "gpt-6-luna";
        s.ChatWebSearch = false;
        s.ChatCloseOnFocusLoss = false;
        s.ChatSystemPrompt = "Antworte knapp.";
        s.ChatHotkeyEnabled = false;
        s.Save();
        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.OpenAI, t.ChatEngine);
        Assert.Equal("gpt-6-luna", t.EffectiveChatModel);
        Assert.False(t.ChatWebSearch);
        Assert.False(t.ChatCloseOnFocusLoss);
        Assert.Equal("Antworte knapp.", t.ChatSystemPrompt);
        Assert.Equal("", t.ActiveChatHotkey);
        t.OfflineMode = true;
        Assert.Equal(Engine.Local, t.EffectiveChatEngine);
        Assert.Equal(ModelPickers.LocalId, t.EffectiveChatModel);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Chat_OlderFile_FollowsDictatePrompt_AndKeyStaysOffWhenTaken()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """
            { "textEngine": "openai", "openAiPromptModel": "gpt-5.4-mini", "templateHotkey": "Alt+6" }
            """);
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(Engine.OpenAI, s.ChatEngine);
        Assert.Equal("gpt-5.4-mini", s.ChatOpenAiModel);
        Assert.Equal("Alt+6", s.ChatHotkey);
        Assert.False(s.ChatHotkeyEnabled); // Alt+6 already opens the template picker

        File.WriteAllText(path, """{ "textEngine": "gemini" }""");
        Assert.Equal("Alt+6", Settings.Load(path, new PlainSecretProtector()).ActiveChatHotkey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ReadAloud_Defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(ReadAloud.DefaultModel, s.ReadAloudModel);
        Assert.Equal("gemini-3.8-flash-lite-tts", s.ReadAloudTtsModel.Id);
        Assert.Equal("Charon", s.ReadAloudVoiceGemini);
        Assert.Equal("alloy", s.ReadAloudVoiceOpenAi);
        Assert.Equal("Charon", s.ReadAloudVoiceFor(TtsProvider.Gemini));
        Assert.Equal("alloy", s.ReadAloudVoiceFor(TtsProvider.OpenAI));
        Assert.Equal(1.0, s.ReadAloudSpeed);
        Assert.True(s.ReadAloudSmartRewrite);
    }

    [Fact]
    public void ReadAloud_AllFieldsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var s = Settings.Load(path, new PlainSecretProtector());
        s.ReadAloudHotkey = "Alt+8";
        s.ReadAloudHotkeyEnabled = false;
        s.ReadAloudModel = "gpt-4o-mini-tts";
        s.ReadAloudVoiceGemini = "Kore";
        s.ReadAloudVoiceOpenAi = "marin";
        s.ReadAloudSpeed = 1.5;
        s.ReadAloudSmartRewrite = false;
        s.Save();

        var json = File.ReadAllText(path);
        foreach (var name in new[] { "readAloudHotkey", "readAloudHotkeyEnabled", "readAloudModel", "readAloudVoiceGemini",
                     "readAloudVoiceOpenAi", "readAloudSpeed", "readAloudSmartRewrite" })
            Assert.Contains($"\"{name}\":", json);

        var t = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal("Alt+8", t.ReadAloudHotkey);
        Assert.False(t.ReadAloudHotkeyEnabled);
        Assert.Equal("gpt-4o-mini-tts", t.ReadAloudModel);
        Assert.Equal(TtsProvider.OpenAI, t.ReadAloudTtsModel.Provider);
        Assert.Equal("Kore", t.ReadAloudVoiceGemini);
        Assert.Equal("marin", t.ReadAloudVoiceOpenAi);
        Assert.Equal(1.5, t.ReadAloudSpeed);
        Assert.False(t.ReadAloudSmartRewrite);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void ReadAloud_BadValuesFallBackToDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{\"readAloudModel\":\"nope\",\"readAloudVoiceGemini\":\"charon\",\"readAloudVoiceOpenAi\":\"Kore\","
            + "\"readAloudSpeed\":3.3}");
        var s = Settings.Load(path, new PlainSecretProtector());
        Assert.Equal(ReadAloud.DefaultModel, s.ReadAloudModel);
        Assert.Equal("Charon", s.ReadAloudVoiceGemini);   // ids are case-sensitive
        Assert.Equal("alloy", s.ReadAloudVoiceOpenAi);
        Assert.Equal(1.0, s.ReadAloudSpeed);

        File.WriteAllText(path, "{\"readAloudModel\":\"gemini-3.1-flash-tts-preview\"}");
        Assert.Equal(ReadAloud.DefaultModel, Settings.Load(path, new PlainSecretProtector()).ReadAloudModel);
        Directory.Delete(dir, true);
    }
}
