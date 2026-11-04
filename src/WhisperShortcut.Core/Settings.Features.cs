using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

// Settings added with step 10 of docs/plan-mac-angleich.md (Vorlesen, Meetings, Chat). In their own file so the main settings
// file only carries two hook lines: LoadFeatures in Load and SaveFeatures in SaveCore.
public sealed partial class Settings
{
    /// <summary>Chosen Windows voice for Read Aloud (its name); "" = the voice of the Windows language.</summary>
    public string ReadAloudVoiceSystem { get; set; } = "";

    /// <summary>The user's own Smart Rewrite instruction (Mac: Rewrite Prompt). Empty = <see cref="ReadAloud.DefaultRewritePrompt"/>.</summary>
    public string ReadAloudRewritePrompt { get; set; } = "";

    /// <summary>The rewrite instruction actually sent.</summary>
    [JsonIgnore]
    public string EffectiveReadAloudRewritePrompt =>
        ReadAloudRewritePrompt.Trim().Length > 0 ? ReadAloudRewritePrompt : ReadAloud.DefaultRewritePrompt;

    /// <summary>The model Vorlesen speaks with: offline always the Windows voices (Mac: automatic in Offline Mode).</summary>
    [JsonIgnore]
    public TtsModel EffectiveReadAloudModel => ReadAloud.Effective(ReadAloudModel, OfflineMode);

    private void LoadFeatures(Dto dto)
    {
        ReadAloudVoiceSystem = ReadAloud.NormalizeVoice(TtsProvider.System, dto.ReadAloudVoiceSystem);
        ReadAloudRewritePrompt = CustomOrEmpty(dto.ReadAloudRewritePrompt, ReadAloud.DefaultRewritePrompt);
    }

    private void SaveFeatures(Dto dto)
    {
        dto.ReadAloudVoiceSystem = ReadAloudVoiceSystem;
        dto.ReadAloudRewritePrompt = ReadAloudRewritePrompt;
    }

    private sealed partial class Dto
    {
        [JsonPropertyName("readAloudVoiceSystem")] public string? ReadAloudVoiceSystem { get; set; }
        [JsonPropertyName("readAloudRewritePrompt")] public string? ReadAloudRewritePrompt { get; set; }
    }
}
