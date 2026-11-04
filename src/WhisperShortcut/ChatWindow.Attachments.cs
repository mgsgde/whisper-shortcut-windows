using System.Drawing.Imaging;

namespace WhisperShortcut;

/// <summary>
/// Chat attachments like the Mac (ChatView attachFile / captureScreenshot, ChatComposerTextView paste): /attach opens a file
/// picker (PDF, images, text), Strg+V in the input attaches a copied image or copied files, /screenshot takes the screen the
/// chat is on, without the chat window. They wait as chips above the input (✕ removes one) and go with the next message;
/// like the Mac, every earlier attachment of the chat is sent again with each request.
/// </summary>
internal sealed partial class ChatWindow
{
    private readonly List<Core.ChatAttachment> _pending = new();
    private readonly FlowLayoutPanel _chips = new()
    {
        AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), Visible = false,
        AccessibleName = "Anhänge für die nächste Nachricht",
    };
    private string? _lastAttachFolder;

    /// <summary>The rows between the command list and the input: attachment chips, then the meeting chat's command buttons.</summary>
    private Control BuildComposerExtras()
    {
        var extras = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0),
        };
        extras.Controls.Add(_chips);
        extras.Controls.Add(_meetingButtons);
        extras.SizeChanged += (_, _) =>
        {
            _chips.MaximumSize = _meetingButtons.MaximumSize = new Size(Math.Max(200, extras.Parent?.ClientSize.Width - 40 ?? 600), 0);
        };
        return extras;
    }

    /// <summary>The attachments for the message being sent; the chips go away.</summary>
    private IReadOnlyList<Core.ChatAttachment> TakePending()
    {
        var taken = _pending.ToList();
        _pending.Clear();
        RefreshChips();
        return taken;
    }

    /// <summary>Adds attachments up to the Mac limits (10 files, 10 screenshots); says what did not fit.</summary>
    private void AddPending(IReadOnlyList<Core.ChatAttachment> found, bool screenshot = false)
    {
        int limit = screenshot ? Core.ChatAttachments.MaxScreenshots : Core.ChatAttachments.MaxFiles;
        int have = _pending.Count(a => IsScreenshot(a) == screenshot);
        if (have >= limit)
        {
            SetStatus(screenshot ? $"Höchstzahl an Bildschirmfotos erreicht ({limit})." : $"Höchstzahl an Dateianhängen erreicht ({limit}).", Tone.Bad);
            return;
        }
        var take = found.Take(limit - have).ToList();
        _pending.AddRange(take);
        RefreshChips();
        if (take.Count < found.Count) SetStatus($"Nur {take.Count} von {found.Count} Dateien angehängt (Grenze: {limit}).", Tone.Bad);
        else SetStatus(take.Count == 1 ? $"„{take[0].FileName}“ geht mit der nächsten Nachricht mit." : $"{take.Count} Dateien gehen mit der nächsten Nachricht mit.", Tone.Secondary);
        Core.Log.Info($"Chat: {take.Count} attachment(s) added ({string.Join(", ", take.Select(a => a.MimeType))})");
        _input.Focus();
    }

    private static bool IsScreenshot(Core.ChatAttachment a) => a.FileName.StartsWith(ScreenshotPrefix, StringComparison.Ordinal);
    private const string ScreenshotPrefix = "Bildschirmfoto ";

    /// <summary>Reads files; unsupported or too large ones are named in the status line, the rest are attached.</summary>
    private void AttachFiles(IEnumerable<string> paths)
    {
        var ok = new List<Core.ChatAttachment>();
        string? firstError = null;
        foreach (var path in paths)
        {
            if (Core.ChatAttachments.FromFile(path, out var error) is { } a) ok.Add(a);
            else firstError ??= error;
        }
        if (ok.Count > 0) AddPending(ok);
        if (firstError != null) SetStatus(firstError, Tone.Bad);
    }

    /// <summary>/attach (Mac NSOpenPanel „Choose files for your next message“): several files, the last folder is remembered.</summary>
    private void PickFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Dateien für die nächste Nachricht wählen",
            Filter = Core.ChatAttachments.DialogFilter,
            Multiselect = true,
            InitialDirectory = _lastAttachFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        _holdOpen = true;
        DialogResult answer;
        try { answer = dialog.ShowDialog(this); }
        finally { _holdOpen = false; }
        if (answer != DialogResult.OK || dialog.FileNames.Length == 0) return;
        _lastAttachFolder = Path.GetDirectoryName(dialog.FileNames[0]);
        AttachFiles(dialog.FileNames);
    }

    /// <summary>Strg+V: a copied image or copied files become attachments; plain text pastes as usual (false).</summary>
    private bool TryPasteAttachments()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList();
                if (files.Count == 0) return false;
                AttachFiles(files);
                return true;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                using (image)
                    AddPending(new[] { Core.ChatAttachments.FromPng(Png(image), $"Eingefügtes Bild {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png") });
                return true;
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException)
        {
            Core.Log.Error("Chat: clipboard could not be read", e);
        }
        return false;
    }

    /// <summary>/screenshot (Mac captureScreenshot): the screen the chat is on, taken while the chat window is hidden.</summary>
    private async Task CaptureScreenshotAsync()
    {
        if (_pending.Count(IsScreenshot) >= Core.ChatAttachments.MaxScreenshots)
        {
            SetStatus($"Höchstzahl an Bildschirmfotos erreicht ({Core.ChatAttachments.MaxScreenshots}).", Tone.Bad);
            return;
        }
        var bounds = Screen.FromControl(this).Bounds;
        _holdOpen = true;
        byte[]? png = null;
        try
        {
            Opacity = 0; // Hide() would count as a focus loss and close the chat
            await Task.Delay(250); // let the desktop repaint without us
            using var shot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            png = Png(shot);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            Core.Log.Error("Chat: screenshot failed", e);
            SetStatus("Das Bildschirmfoto hat nicht geklappt. Details stehen im Protokoll.", Tone.Bad);
        }
        finally
        {
            Opacity = 1;
            _holdOpen = false;
            Activate();
        }
        if (png != null) AddPending(new[] { Core.ChatAttachments.FromPng(png, $"{ScreenshotPrefix}{DateTime.Now:yyyy-MM-dd HH.mm.ss}.png") }, screenshot: true);
    }

    private static byte[] Png(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private void RefreshChips()
    {
        _chips.SuspendLayout();
        foreach (Control c in _chips.Controls) c.Dispose();
        _chips.Controls.Clear();
        foreach (var a in _pending)
        {
            var attachment = a;
            var icon = Core.ChatAttachments.IsImage(a) ? "🖼" : Core.ChatAttachments.IsText(a) ? "📄" : "📎";
            var chip = new FluentButton($"{icon} {Shorten(a.FileName)}   ✕")
            {
                AutoSize = true, Height = 28, Margin = new Padding(0, 0, 6, 4), AccessibleName = $"Anhang {a.FileName} entfernen",
            };
            new ToolTip().SetToolTip(chip, $"{a.FileName} – klicken entfernt den Anhang");
            chip.Click += (_, _) =>
            {
                _pending.Remove(attachment);
                RefreshChips();
                _input.Focus();
            };
            _chips.Controls.Add(chip);
        }
        _chips.Visible = _pending.Count > 0;
        _chips.ResumeLayout(true);
        Theme.Apply(_chips);
    }

    private static string Shorten(string name) => name.Length <= 32 ? name : name[..20] + "…" + name[^10..];
}
