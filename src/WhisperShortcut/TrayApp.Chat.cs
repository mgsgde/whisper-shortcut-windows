namespace WhisperShortcut;

/// <summary>
/// The chat (Mac ChatWindowManager): the tray row „Chat“, its shortcut, and the one window it opens. The window is made on first
/// use and lives until the app quits; /meeting in it starts and stops the Live-Meeting of <see cref="MeetingController"/>.
/// </summary>
internal sealed partial class TrayApp
{
    private ToolStripMenuItem? _chatItem;
    private ChatWindow? _chatWindow;
    private Core.ChatClient? _chatClient;

    /// <summary>The tray row „Chat“ (Mac menu: „Chat“ ⌥Space, above Settings).</summary>
    private ToolStripMenuItem ChatMenuItem() => _chatItem ??= new ToolStripMenuItem("Chat", null, (_, _) => ShowChat());

    private ChatWindow ChatWindow()
    {
        if (_chatWindow is { IsDisposed: false } w) return w;
        _chatClient ??= new Core.ChatClient(() => _settings.ApiKey, () => _settings.OpenAiKey);
        return _chatWindow = new ChatWindow(_settings, _chatClient, _offline, _meeting.Toggle, () => _meeting.IsActive, ShowSettings, TrySave);
    }

    /// <summary>The chat shortcut: opens the chat in front, or closes it when it is in front (Mac toggleChat).</summary>
    private void ToggleChat()
    {
        Core.Log.Info("Hotkey Chat");
        ChatWindow().Toggle();
    }

    private void ShowChat() => ChatWindow().ShowInFront();

    private void DisposeChat()
    {
        _chatWindow?.Dispose();
        _chatWindow = null;
    }
}
