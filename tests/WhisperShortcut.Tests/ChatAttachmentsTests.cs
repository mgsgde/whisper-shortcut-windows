using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ChatAttachmentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-attach-" + Guid.NewGuid().ToString("N"));

    public ChatAttachmentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("A.PNG", "image/png")]
    [InlineData("b.jpg", "image/jpeg")]
    [InlineData("b.JPEG", "image/jpeg")]
    [InlineData("c.gif", "image/gif")]
    [InlineData("d.webp", "image/webp")]
    [InlineData("n.txt", "text/plain")]
    [InlineData("n.md", "text/plain")]
    [InlineData("n.csv", "text/plain")]
    [InlineData("n.log", "text/plain")]
    [InlineData("x.exe", null)]
    [InlineData("noext", null)]
    public void MimeFor_maps_extensions(string name, string? expected) => Assert.Equal(expected, ChatAttachments.MimeFor(name));

    [Fact]
    public void Limits_and_filter_match_the_mac()
    {
        Assert.Equal(10, ChatAttachments.MaxFiles);
        Assert.Equal(10, ChatAttachments.MaxScreenshots);
        Assert.Equal(20 * 1024 * 1024, ChatAttachments.MaxBytes);
        Assert.StartsWith("Unterstützte Dateien|*.pdf;", ChatAttachments.DialogFilter);
        Assert.EndsWith("|Alle Dateien|*.*", ChatAttachments.DialogFilter);
    }

    [Fact]
    public void FromFile_reads_a_supported_file()
    {
        var path = Path.Combine(_dir, "Notiz.TXT");
        File.WriteAllText(path, "Grüße", new UTF8Encoding(false));
        var a = ChatAttachments.FromFile(path, out var error);
        Assert.Null(error);
        Assert.Equal("Notiz.TXT", a!.FileName);
        Assert.Equal("text/plain", a.MimeType);
        Assert.Equal("Grüße", ChatAttachments.DecodeText(a));
        Assert.False(ChatAttachments.IsImage(a));
    }

    [Fact]
    public void FromFile_rejects_unsupported_types_with_a_german_message()
    {
        var path = Path.Combine(_dir, "tool.exe");
        File.WriteAllText(path, "x");
        Assert.Null(ChatAttachments.FromFile(path, out var error));
        Assert.Equal("„tool.exe“ wird nicht unterstützt. Möglich sind PDF, Bilder (PNG, JPEG, GIF, WebP) und Text.", error);
    }

    [Fact]
    public void FromFile_rejects_files_over_20_MB()
    {
        var path = Path.Combine(_dir, "gross.pdf");
        using (var fs = File.Create(path)) fs.SetLength(ChatAttachments.MaxBytes + 1);
        Assert.Null(ChatAttachments.FromFile(path, out var error));
        Assert.Equal("„gross.pdf“ ist größer als 20 MB und wurde nicht angehängt.", error);
    }

    [Fact]
    public void FromFile_reports_a_missing_file()
    {
        Assert.Null(ChatAttachments.FromFile(Path.Combine(_dir, "weg.png"), out var error));
        Assert.Equal("Konnte nicht gelesen werden: weg.png", error);
    }

    [Fact]
    public void FromPng_is_an_image()
    {
        var a = ChatAttachments.FromPng(new byte[] { 1, 2, 3 }, "Bildschirmfoto 2026-11-04 10.15.03.png");
        Assert.Equal("image/png", a.MimeType);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), a.DataBase64);
        Assert.True(ChatAttachments.IsImage(a));
    }
}
