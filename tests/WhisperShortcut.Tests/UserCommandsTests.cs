using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UserCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-cmds-" + Guid.NewGuid().ToString("N"), "commands");
    private static readonly IReadOnlySet<string> Reserved = new HashSet<string> { "new", "model" };

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch { }
    }

    private static (UserCommand? Command, IReadOnlyList<UserCommandIssue> Issues) Parse(string file, string text) =>
        UserCommandParser.Parse(file, text, Reserved);

    [Theory]
    [InlineData("a", true)]
    [InlineData("protokoll-2", true)]
    [InlineData("1x", true)]
    [InlineData("-x", false)]
    [InlineData("Gross", false)]
    [InlineData("mit leer", false)]
    [InlineData("", false)]
    public void IsValidName(string name, bool ok) => Assert.Equal(ok, UserCommandParser.IsValidName(name));

    [Fact]
    public void IsValidName_allows_at_most_40_characters()
    {
        Assert.True(UserCommandParser.IsValidName(new string('a', 40)));
        Assert.False(UserCommandParser.IsValidName(new string('a', 41)));
    }

    [Fact]
    public void Parse_reads_front_matter_ignores_comments_and_unquotes()
    {
        var (c, issues) = Parse("Prot-Okoll.md",
            "﻿---\r\n# label: ignoriert\r\nlabel: \"Mein Label\"\r\ndescription: 'Kurz'\r\nScope: Chat\r\n---\r\n\r\n  Mach das.\r\n\r\n");
        Assert.Empty(issues);
        Assert.Equal("prot-okoll", c!.Name);
        Assert.Equal("/prot-okoll", c.Slash);
        Assert.Equal("Mein Label", c.Label);
        Assert.Equal("Kurz", c.Description);
        Assert.Equal(UserCommandScope.Chat, c.Scope);
        Assert.Equal("Mach das.", c.Body);
        Assert.True(c.ShowsInChat);
        Assert.False(c.ShowsInMeeting);
    }

    [Fact]
    public void Parse_defaults_label_description_and_scope()
    {
        var (c, issues) = Parse("wichtige-zusammenfassung.md", "\n## Fasse alles zusammen\nMehr.");
        Assert.Empty(issues);
        Assert.Equal("Wichtige zusammenfassung", c!.Label);
        Assert.Equal("Fasse alles zusammen", c.Description);
        Assert.Equal(UserCommandScope.Both, c.Scope);
        Assert.True(c.ShowsInChat && c.ShowsInMeeting);
        Assert.Equal("## Fasse alles zusammen\nMehr.", c.Body);
    }

    [Fact]
    public void Parse_cuts_a_long_default_description_at_80()
    {
        var (c, _) = Parse("x.md", new string('a', 100));
        Assert.Equal(new string('a', 80) + "…", c!.Description);
    }

    [Fact]
    public void Parse_without_closing_dashes_treats_everything_as_body()
    {
        var (c, _) = Parse("x.md", "---\nlabel: A\nText");
        Assert.Equal("---\nlabel: A\nText", c!.Body);
    }

    [Fact]
    public void Parse_unknown_scope_is_a_warning_and_loads_as_both()
    {
        var (c, issues) = Parse("x.md", "---\nscope: Team\n---\nText");
        Assert.Equal(UserCommandScope.Both, c!.Scope);
        var issue = Assert.Single(issues);
        Assert.True(issue.IsWarning);
        Assert.Equal("Unbekannter Bereich „Team“ (meeting, chat oder both); es wird both genutzt", issue.Message);
    }

    [Fact]
    public void Parse_numeric_scope_is_unknown()
    {
        var (c, issues) = Parse("x.md", "---\nscope: 1\n---\nText");
        Assert.Equal(UserCommandScope.Both, c!.Scope);
        Assert.True(Assert.Single(issues).IsWarning);
    }

    [Theory]
    [InlineData("Großbuchstabe!.md", "Ungültiger Name. Erlaubt sind Kleinbuchstaben, Ziffern und Bindestriche (höchstens 40 Zeichen).")]
    [InlineData("new.md", "/new ist ein eingebauter Befehl")]
    public void Parse_rejects_bad_names(string file, string message)
    {
        var (c, issues) = Parse(file, "Text");
        Assert.Null(c);
        var issue = Assert.Single(issues);
        Assert.False(issue.IsWarning);
        Assert.Equal(message, issue.Message);
        Assert.Equal(file, issue.FileName);
    }

    [Fact]
    public void Parse_rejects_big_and_empty_files()
    {
        Assert.Equal("Die Datei ist größer als 32 KB", Parse("x.md", new string('a', UserCommandParser.MaxFileBytes + 1)).Issues[0].Message);
        Assert.NotNull(Parse("x.md", new string('a', UserCommandParser.MaxFileBytes)).Command);
        Assert.Equal("Der Befehlstext ist leer", Parse("x.md", "---\nlabel: A\n---\n  \n").Issues[0].Message);
    }

    [Fact]
    public void AppendingArguments_adds_extra_instructions()
    {
        Assert.Equal("B", UserCommandParser.AppendingArguments("B", "  "));
        Assert.Equal("B\n\nAdditional instructions: kürzer", UserCommandParser.AppendingArguments("B", " kürzer "));
    }

    [Fact]
    public void NeedsMeeting_message() =>
        Assert.Equal("`/protokoll` braucht einen Meeting-Chat, der Befehl arbeitet mit dem Transkript.",
            UserCommandParser.NeedsMeeting("/protokoll"));

    [Fact]
    public void Load_creates_the_folder_and_reads_sorted_valid_files_only()
    {
        var store = new UserCommandStore(_root);
        Assert.Empty(store.Load(Reserved).Commands);
        Assert.True(Directory.Exists(_root));

        File.WriteAllText(Path.Combine(_root, "b.md"), "B");
        File.WriteAllText(Path.Combine(_root, "a.md"), "A");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "ignored");
        File.WriteAllText(Path.Combine(_root, ".hidden.md"), "ignored");
        Directory.CreateDirectory(Path.Combine(_root, "dir.md"));
        File.WriteAllText(Path.Combine(_root, "new.md"), "reserved");
        File.WriteAllBytes(Path.Combine(_root, "bad.md"), new byte[] { 0xFF, 0xFE, 0xFA });
        File.WriteAllText(Path.Combine(_root, "big.md"), new string('x', UserCommandParser.MaxFileBytes + 1));

        var (commands, issues) = store.Load(Reserved);
        Assert.Equal(new[] { "a", "b" }, commands.Select(c => c.Name));
        Assert.Equal(Path.Combine(_root, "a.md"), commands[0].FilePath);
        Assert.Contains(issues, i => i.FileName == "new.md" && i.Message == "/new ist ein eingebauter Befehl");
        Assert.Contains(issues, i => i.FileName == "bad.md" && i.Message == "Die Datei ist kein gültiger UTF-8-Text");
        Assert.Contains(issues, i => i.FileName == "big.md" && i.Message == "Die Datei ist größer als 32 KB");
        Assert.Equal(3, issues.Count);
    }

    [Fact]
    public void CreateFromTemplate_uses_the_first_free_name_and_the_result_parses()
    {
        var store = new UserCommandStore(_root);
        var first = store.CreateFromTemplate();
        var second = store.CreateFromTemplate();
        Assert.Equal(Path.Combine(_root, "protokoll.md"), first);
        Assert.Equal(Path.Combine(_root, "protokoll-2.md"), second);
        Assert.Equal(UserCommandStore.TemplateContents, File.ReadAllText(first, Encoding.UTF8));

        var (commands, issues) = store.Load(Reserved);
        Assert.Empty(issues);
        Assert.Equal(new[] { "protokoll", "protokoll-2" }, commands.Select(c => c.Name));
        Assert.Equal("Protokoll", commands[0].Label);
        Assert.Equal(UserCommandScope.Meeting, commands[0].Scope);
        Assert.StartsWith("Schreibe aus dem Transkript", commands[0].Body);
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        var list = new[] { new UserCommand("abc", "Abc", "d", UserCommandScope.Both, "b", "p") };
        Assert.NotNull(UserCommandStore.Find(list, "ABC"));
        Assert.NotNull(UserCommandStore.Find(list, "/abc"));
        Assert.Null(UserCommandStore.Find(list, "abd"));
    }

    [Fact]
    public void DefaultRoot_is_under_user_context() =>
        Assert.EndsWith(Path.Combine("UserContext", "commands"), UserCommandStore.DefaultRoot);
}
