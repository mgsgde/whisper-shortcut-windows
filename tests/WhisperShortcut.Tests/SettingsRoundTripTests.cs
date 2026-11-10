using System.Reflection;
using System.Text.Json;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Every public read/write property of <see cref="Settings"/> must reach settings.json and come back, and must be part of
/// <see cref="Settings.Fingerprint"/>. A new property is picked up by reflection: forget its Load/ToDto line and a case fails.
/// </summary>
public class SettingsRoundTripTests
{
    private sealed class Reverse : ISecretProtector
    {
        public string Protect(string plain) => new(plain.Reverse().ToArray());
        public string? Unprotect(string stored) => new(stored.Reverse().ToArray());
    }

    private const string Text = "x-roundtrip";
    private const string Hotkey = "Ctrl+Alt+F9";

    /// <summary>Properties that legitimately do not persist or cannot round-trip, each with the reason. Keep it empty if possible.</summary>
    private static readonly Dictionary<string, string> Excluded = new()
    {
    };

    private static string Other(IReadOnlyList<ModelInfo> list, string current) => list.First(m => m.Id != current).Id;

    private static ModelSelection Different(ModelStep step, ModelSelection d) =>
        new(Engine.OpenAI, Other(step.GeminiCatalog(), d.Gemini), Other(step.OpenAiCatalog(), d.OpenAi));

    // Values for properties the generic rule (bool flip, enum other value, string Text, one-element string list) cannot or
    // should not guess: normalised or range-limited fields. Gets the fresh default so the value always differs from it.
    private static readonly Dictionary<string, Func<Settings, object>> Overrides = new()
    {
        // Provider and both model ids change together, so one case covers the three flat keys of a step.
        [nameof(Settings.Dictate)] = d => Different(ModelStep.Transcription, d.Dictate),
        [nameof(Settings.Prompt)] = d => Different(ModelStep.Text, d.Prompt),
        [nameof(Settings.MeetingTranscription)] = d => Different(ModelStep.Transcription, d.MeetingTranscription),
        [nameof(Settings.MeetingText)] = d => Different(ModelStep.Text, d.MeetingText),
        [nameof(Settings.Chat)] = d => Different(ModelStep.Text, d.Chat),
        [nameof(Settings.ReadAloudModel)] = d => ReadAloud.Models.First(m => m.Id != d.ReadAloudModel).Id,
        [nameof(Settings.ReadAloudVoiceGemini)] = d => ReadAloud.Voices(TtsProvider.Gemini).First(v => v.Id != d.ReadAloudVoiceGemini).Id,
        [nameof(Settings.ReadAloudVoiceOpenAi)] = d => ReadAloud.Voices(TtsProvider.OpenAI).First(v => v.Id != d.ReadAloudVoiceOpenAi).Id,
        [nameof(Settings.ReadAloudSpeed)] = d => ReadAloud.Speeds.First(s => Math.Abs(s - d.ReadAloudSpeed) > 0.001),
        [nameof(Settings.TranscriptionLanguage)] = _ => "de",
        [nameof(Settings.TranscriptionTemperature)] = _ => 0.5,
        [nameof(Settings.TranscriptionThinking)] = _ => "high",
        [nameof(Settings.MeetingAudioMode)] = _ => "mic",
        [nameof(Settings.MeetingChunkSeconds)] = _ => 30,
        [nameof(Settings.MeetingSafeguardMinutes)] = _ => 120,
        [nameof(Settings.WelcomeStep)] = _ => 3,
        [nameof(Settings.Templates)] = _ => new List<DictationTemplate>
        {
            new() { Id = "11111111-1111-1111-1111-111111111111", Name = "Name", Instructions = "Anweisung", Hotkey = Hotkey },
        },
        [nameof(Settings.RestoreClipboard)] = _ => true, // view of ClipboardAfterPaste (RestorePrevious), not persisted by itself
    };

    private static bool IsSettable(PropertyInfo p) =>
        p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0;

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(IsSettable);

    public static IEnumerable<object[]> PropertyNames() =>
        SettableProperties().Where(p => !Excluded.ContainsKey(p.Name)).Select(p => new object[] { p.Name });

    private static object ValueFor(PropertyInfo p, Settings fresh)
    {
        if (Overrides.TryGetValue(p.Name, out var make)) return make(fresh);
        var t = p.PropertyType;
        var current = p.GetValue(fresh);
        if (t == typeof(bool)) return !(bool)current!;
        if (t.IsEnum) return Enum.GetValues(t).Cast<object>().First(v => !Equals(v, current));
        if (t == typeof(string)) return p.Name.EndsWith("Hotkey", StringComparison.Ordinal) ? Hotkey : Text;
        if (t == typeof(List<string>)) return new List<string> { Text };
        throw new InvalidOperationException($"Settings.{p.Name} ({t.Name}) needs an entry in Overrides.");
    }

    private static string Json(object? v) => JsonSerializer.Serialize(v);

    [Theory]
    [MemberData(nameof(PropertyNames))]
    public void SettableProperty_SurvivesSaveAndLoad_AndChangesFingerprint(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var prop = typeof(Settings).GetProperty(name)!;
            var s = Settings.Load(path, new Reverse());
            var before = s.Fingerprint();
            var value = ValueFor(prop, s);
            Assert.NotEqual(Json(prop.GetValue(s)), Json(value)); // the test value must differ from the default

            prop.SetValue(s, value);
            Assert.NotEqual(before, s.Fingerprint()); // part of the fingerprint (ToDto)

            s.Save();
            var t = Settings.Load(path, new Reverse());
            Assert.Equal(Json(value), Json(prop.GetValue(t))); // part of Load
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Enumeration_SkipsNothing_AndTablesHaveNoStaleEntries()
    {
        var all = SettableProperties().Select(p => p.Name).ToHashSet();
        Assert.True(all.Count > 60, $"only {all.Count} settable properties found: enumeration looks broken");
        Assert.Equal(all.Count, PropertyNames().Count() + Excluded.Count);
        Assert.All(Excluded, e => { Assert.Contains(e.Key, all); Assert.False(string.IsNullOrWhiteSpace(e.Value)); });
        Assert.All(Overrides.Keys, k => Assert.Contains(k, all));
        // every settable property has a value: the generic rule or an override (throws otherwise)
        var fresh = Settings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"), new Reverse());
        foreach (var p in SettableProperties()) Assert.NotNull(ValueFor(p, fresh));
    }
}
