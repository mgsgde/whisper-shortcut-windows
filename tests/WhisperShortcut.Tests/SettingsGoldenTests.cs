using System.Reflection;
using System.Runtime.CompilerServices;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// R10b: settings.json is a contract with every older and newer version. The golden file pins the exact bytes written for a
/// Settings with non-default values everywhere; the snapshots pin every property after loading no file, an empty file and
/// old files (the missing-field rules). All were captured from the code before the descriptor refactor. To re-capture on
/// purpose: set WS_UPDATE_GOLDEN=1, run these tests, then review the diff.
/// </summary>
public class SettingsGoldenTests
{
    private static string GoldenDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden");

    private static string Dir() => Path.Combine(Path.GetTempPath(), "wsw-golden-" + Guid.NewGuid().ToString("N"));

    private static string Other(IReadOnlyList<ModelInfo> list, string current) => list.First(m => m.Id != current).Id;

    private static ModelSelection Different(ModelStep step, ModelSelection d) =>
        new(Engine.OpenAI, Other(step.GeminiCatalog(), d.Gemini), Other(step.OpenAiCatalog(), d.OpenAi));

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    private static void Compare(string name, string actual)
    {
        var file = Path.Combine(GoldenDir(), name);
        if (Environment.GetEnvironmentVariable("WS_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(file, Normalize(actual));
            return;
        }
        Assert.Equal(Normalize(File.ReadAllText(file)), Normalize(actual));
    }

    /// <summary>A Settings whose every property differs from a fresh install.</summary>
    private static Settings NonDefault(string path)
    {
        var s = Settings.Load(path, new PlainSecretProtector());
        s.ApiKey = "gemini-key";
        s.OpenAiKey = "openai-key";
        s.Dictate = Different(ModelStep.Transcription, s.Dictate);
        s.Prompt = Different(ModelStep.Text, s.Prompt);
        s.PunctuationMode = PunctuationMode.SpokenOnly;
        s.DictateMode = DictateMode.Live;
        s.TranscriptionLanguage = "de";
        s.TranscriptionTemperature = 0.5;
        s.TranscriptionThinking = "high";
        s.Glossary = new() { "Alpha", "Beta" };
        s.DictatePromptTemplate = "Vorlage-Text";
        s.DictateSystemPrompt = "Eigene Diktat-Anweisung";
        s.PromptModeSystemPrompt = "Eigene Prompt-Anweisung";
        s.Microphone = "Mikrofon X";
        s.DictateHotkey = "Ctrl+Alt+F1"; s.DictateHotkeyEnabled = false;
        s.PromptHotkey = "Ctrl+Alt+F2"; s.PromptHotkeyEnabled = false;
        s.GlossaryHotkey = "Ctrl+Alt+F3"; s.GlossaryHotkeyEnabled = true;
        s.OpenSettingsHotkey = "Ctrl+Alt+F4"; s.OpenSettingsHotkeyEnabled = false;
        s.ReadAloudHotkey = "Ctrl+Alt+F5"; s.ReadAloudHotkeyEnabled = false;
        s.ScreenshotHotkey = "Ctrl+Alt+F6"; s.ScreenshotHotkeyEnabled = false;
        s.LiveDictateHotkey = "Ctrl+Alt+F7"; s.LiveDictateHotkeyEnabled = true;
        s.ChatHotkey = "Ctrl+Alt+F8"; s.ChatHotkeyEnabled = false;
        s.MeetingHotkey = "Ctrl+Alt+F9";
        s.TemplateHotkey = "Ctrl+Alt+F10";
        s.ReadAloudModel = ReadAloud.Models.First(m => m.Id != s.ReadAloudModel).Id;
        s.ReadAloudVoiceGemini = ReadAloud.Voices(TtsProvider.Gemini).First(v => v.Id != s.ReadAloudVoiceGemini).Id;
        s.ReadAloudVoiceOpenAi = ReadAloud.Voices(TtsProvider.OpenAI).First(v => v.Id != s.ReadAloudVoiceOpenAi).Id;
        s.ReadAloudSpeed = ReadAloud.Speeds.First(x => Math.Abs(x - s.ReadAloudSpeed) > 0.001);
        s.ReadAloudSmartRewrite = false;
        s.ReadAloudVoiceSystem = "Microsoft Katja Desktop";
        s.ReadAloudRewritePrompt = "Eigene Umschreib-Anweisung";
        s.RightCtrlDictation = false;
        s.Templates = new()
        {
            new() { Id = "11111111-1111-1111-1111-111111111111", Name = "Name", Instructions = "Anweisung", Hotkey = "Ctrl+Alt+F11" },
        };
        s.SendScreenshots = false;
        s.LastTemplateId = "11111111-1111-1111-1111-111111111111";
        s.MeetingAudioMode = "mic";
        s.MeetingSummary = false;
        s.OfflineMode = true;
        s.OfflineLlmId = "llm-x";
        s.QuickActions = new() { "Kürzer", "Höflicher" };
        s.AutoPaste = false;
        s.Sounds = true;
        s.AutoUpdateCheck = false;
        s.ClipboardAfterPaste = ClipboardAfterPaste.RestorePrevious;
        s.WelcomeCompleted = true;
        s.MoveOfferDismissed = new() { @"C:\x\WhisperShortcut.exe" };
        s.WelcomeStep = 3;
        s.MeetingTranscription = Different(ModelStep.Transcription, s.MeetingTranscription);
        s.MeetingSpeechEngineExplicit = true;
        s.MeetingText = Different(ModelStep.Text, s.MeetingText);
        s.MeetingChunkSeconds = 30;
        s.MeetingSafeguardMinutes = 120;
        s.ChatSystemPrompt = "Eigene Chat-Anweisung";
        s.ChatWebSearch = false;
        s.ChatCloseOnFocusLoss = false;
        s.Chat = Different(ModelStep.Text, s.Chat);
        return s;
    }

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0);

    [Fact]
    public void NonDefaultSettings_AreAllDifferentFromFresh()
    {
        var dir = Dir();
        try
        {
            var fresh = Settings.Load(Path.Combine(dir, "a.json"), new PlainSecretProtector());
            var s = NonDefault(Path.Combine(dir, "b.json"));
            foreach (var p in SettableProperties().Where(p => p.Name != nameof(Settings.RestoreClipboard)))
                Assert.True(Dump(p.GetValue(fresh)) != Dump(p.GetValue(s)), $"{p.Name} is still the default in the golden Settings");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SettingsJson_IsByteIdenticalToGolden()
    {
        var dir = Dir();
        try
        {
            var path = Path.Combine(dir, "settings.json");
            NonDefault(path).Save();
            Compare("settings.golden.json", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void FreshInstall_DefaultsSnapshot()
    {
        var dir = Dir();
        try
        {
            Compare("load-fresh.txt", Dump(Settings.Load(Path.Combine(dir, "settings.json"), new PlainSecretProtector())));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmptyFile_LoadSnapshot() => Compare("load-empty.txt", LoadText("{}"));

    [Fact]
    public void LegacyFile_LoadSnapshot()
    {
        // An install from before the Mac digits, the Meetings page, Chat, Read Aloud extras and the clipboard choice.
        Compare("load-legacy.txt", LoadText("""
            {
              "apiKey": "plain-key",
              "speechEngine": "openai",
              "openAiTranscriptionModel": "whisper-1",
              "promptSystemPrompt": "Alte Vorlage",
              "dictateHotkey": "",
              "restoreClipboard": false,
              "offlineMode": true,
              "quickActions": [ "Kürzer", "Formeller" ],
              "templates": [ null, { "id": "t1", "name": "N", "instructions": "a\r\nb" } ],
              "glossary": [ "A", null ],
              "meetingTextEngine": "gemini"
            }
            """));
    }

    [Fact]
    public void FileWithPinFlagButNoValues_LoadSnapshot() =>
        Compare("load-pinned-empty.txt", LoadText("""{ "shortcutDefaultsAltV1": true }"""));

    [Fact]
    public void LegacyMeetingEngineAndClashes_LoadSnapshot() =>
        Compare("load-legacy-meeting.txt", LoadText("""
            { "speechEngine": "local", "meetingSpeechEngine": "openai", "meetingTranscriptionModel": "x", "chatEngine": "openai",
              "readAloudHotkey": "Ctrl+Shift+D", "clipboardAfterPaste": "clear", "welcomeStep": 2 }
            """));

    [Fact]
    public void LegacyFileWithKey_ClashesDisableAddedKeys_LoadSnapshot() =>
        Compare("load-clash.txt", LoadText("""
            { "dictateHotkey": "Alt+6", "promptHotkey": "Alt+4", "welcomeCompleted": false, "sounds": false, "sendScreenshots": true }
            """));

    private static string LoadText(string json)
    {
        var dir = Dir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);
            return Dump(Settings.Load(path, new PlainSecretProtector()));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static string Dump(object? v) => v switch
    {
        null => "null",
        string t => "\"" + t.Replace("\n", "\\n").Replace("\r", "\\r") + "\"",
        Settings s => string.Join("\n", typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + " = " + Dump(p.GetValue(s)))) + "\n",
        DictationTemplate t => $"Template({Dump(t.Id)}, {Dump(t.Name)}, {Dump(t.Instructions)}, {Dump(t.Hotkey)})",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Dump)) + "]",
        _ => v.ToString()!,
    };
}
