namespace WhisperShortcut.Core;

/// <summary>
/// What a settings change touched, for the follow-up steps the tray runs after the settings window, the welcome
/// tour or an AI app (MCP) wrote into <see cref="Settings"/>. Compare a <see cref="Snapshot"/> taken before
/// with the settings after; a step that is expensive or not repeatable runs only on its flag.
/// </summary>
public readonly record struct SettingsDelta(bool GlossaryChanged, bool OfflineModeChanged, bool HotkeysChanged)
{
    /// <summary>The few values the follow-up steps depend on, copied so later writes into Settings cannot alter them.</summary>
    public sealed record Snapshot(string Glossary, bool OfflineMode, string Hotkeys)
    {
        public static Snapshot Of(Settings s) => new(
            string.Join("\n", s.Glossary),
            s.OfflineMode,
            // Everything RegisterHotkeys reads; Active* is "" for a switched-off key.
            string.Join("\u0001", new[]
            {
                s.ActiveDictateHotkey, s.ActivePromptHotkey, s.ActiveGlossaryHotkey, s.MeetingHotkey,
                s.ActiveLiveDictateHotkey, s.ActiveScreenshotHotkey, s.ActiveReadAloudHotkey, s.ActiveChatHotkey,
                s.ActiveOpenSettingsHotkey, s.TemplateHotkey, s.RightCtrlDictation ? "1" : "0",
            }.Concat(s.Templates.Select(t => t.Id + "=" + t.Hotkey))));
    }

    public static SettingsDelta Between(Snapshot before, Snapshot after) => new(
        before.Glossary != after.Glossary,
        before.OfflineMode != after.OfflineMode,
        before.Hotkeys != after.Hotkeys);

    public static SettingsDelta Between(Snapshot before, Settings after) => Between(before, Snapshot.Of(after));
}
