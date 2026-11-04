using System.Text;

namespace WhisperShortcut.Core;

/// <summary>A file, clipboard image or screenshot attached to a chat message. The content is kept as Base64.</summary>
public sealed record ChatAttachment(string FileName, string MimeType, string DataBase64);

/// <summary>Limits, file types and loading of chat attachments (Mac maxFileAttachments / maxScreenshots / 20 MB).</summary>
public static class ChatAttachments
{
    /// <summary>Most files per message (Mac maxFileAttachments).</summary>
    public const int MaxFiles = 10;

    /// <summary>Most screenshots per message (Mac maxScreenshots).</summary>
    public const int MaxScreenshots = 10;

    /// <summary>Largest single attachment in bytes (20 MB).</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>Filter string for the WinForms OpenFileDialog.</summary>
    public const string DialogFilter =
        "Unterstützte Dateien|*.pdf;*.png;*.jpg;*.jpeg;*.gif;*.webp;*.txt;*.md;*.csv;*.log|Alle Dateien|*.*";

    /// <summary>The MIME type for the file's extension (case-insensitive), or null when the type is not supported.</summary>
    public static string? MimeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" or ".md" or ".csv" or ".log" => "text/plain",
            _ => null,
        };
    }

    /// <summary>True for image attachments.</summary>
    public static bool IsImage(ChatAttachment a) => a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for plain-text attachments.</summary>
    public static bool IsText(ChatAttachment a) => a.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <paramref name="path"/>. On failure the attachment is null and <paramref name="error"/> holds a German message
    /// (unsupported type, larger than 20 MB, or unreadable).
    /// </summary>
    public static ChatAttachment? FromFile(string path, out string? error)
    {
        error = null;
        var name = Path.GetFileName(path);
        var mime = MimeFor(name);
        if (mime == null)
        {
            error = $"„{name}“ wird nicht unterstützt. Möglich sind PDF, Bilder (PNG, JPEG, GIF, WebP) und Text.";
            return null;
        }
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                error = $"„{name}“ ist größer als 20 MB und wurde nicht angehängt.";
                return null;
            }
            return new ChatAttachment(name, mime, Convert.ToBase64String(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Konnte nicht gelesen werden: {name}";
            return null;
        }
    }

    /// <summary>A PNG from the clipboard or a screenshot, named by the caller.</summary>
    public static ChatAttachment FromPng(byte[] png, string fileName) =>
        new(fileName, "image/png", Convert.ToBase64String(png));

    /// <summary>The text of a text attachment (UTF-8 decode of the Base64 content), for OpenAI and the offline engine.</summary>
    public static string DecodeText(ChatAttachment a)
    {
        try { return new UTF8Encoding(false, false).GetString(Convert.FromBase64String(a.DataBase64)); }
        catch (FormatException) { return ""; }
    }
}
