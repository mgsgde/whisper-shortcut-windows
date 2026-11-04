using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>Where a custom command is offered.</summary>
public enum UserCommandScope
{
    /// <summary>Meeting chats only.</summary>
    Meeting,
    /// <summary>Normal chats only.</summary>
    Chat,
    /// <summary>Both.</summary>
    Both,
}

/// <summary>A custom chat command, loaded from one Markdown file (Mac UserCommand).</summary>
/// <param name="Name">Lower-case, without the slash.</param>
/// <param name="Label">Button text above the meeting chat input.</param>
/// <param name="Description">The line in the command list.</param>
/// <param name="Scope">Where the command is offered.</param>
/// <param name="Body">The instruction sent to the model.</param>
/// <param name="FilePath">The file it was loaded from.</param>
public sealed record UserCommand(
    string Name, string Label, string Description, UserCommandScope Scope, string Body, string FilePath)
{
    /// <summary>The command as typed: "/" + <see cref="Name"/>.</summary>
    public string Slash => "/" + Name;

    /// <summary>True when the scope is meeting or both.</summary>
    public bool ShowsInMeeting => Scope is UserCommandScope.Meeting or UserCommandScope.Both;

    /// <summary>True when the scope is chat or both.</summary>
    public bool ShowsInChat => Scope is UserCommandScope.Chat or UserCommandScope.Both;
}

/// <summary>A problem with a command file. A warning means the command was loaded anyway.</summary>
public sealed record UserCommandIssue(string FileName, string Message, bool IsWarning);

/// <summary>Parses command files: optional front matter between "---" lines, then the instruction (Mac UserCommandParser).</summary>
public static class UserCommandParser
{
    /// <summary>Largest command file in bytes.</summary>
    public const int MaxFileBytes = 32 * 1024;

    /// <summary>Longest automatic description in characters.</summary>
    public const int MaxDescriptionLength = 80;

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>Lower-case letters, digits and dashes, at most 40 characters, not starting with a dash.</summary>
    public static bool IsValidName(string name) => NamePattern.IsMatch(name ?? "");

    /// <summary>Message when a command that needs the transcript is used outside a meeting chat.</summary>
    public static string NeedsMeeting(string slash) =>
        $"`{slash}` braucht einen Meeting-Chat, der Befehl arbeitet mit dem Transkript.";

    /// <summary>
    /// Parses one file. The command name is the file name without extension, lower-cased. Returns the command (null on
    /// failure) and its issues; an unknown scope is only a warning.
    /// </summary>
    public static (UserCommand? Command, IReadOnlyList<UserCommandIssue> Issues) Parse(
        string fileName, string contents, IReadOnlySet<string> reservedNames, string? filePath = null)
    {
        (UserCommand?, IReadOnlyList<UserCommandIssue>) Fail(string message) =>
            (null, new[] { new UserCommandIssue(fileName, message, false) });

        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (!IsValidName(stem))
            return Fail("Ungültiger Name. Erlaubt sind Kleinbuchstaben, Ziffern und Bindestriche (höchstens 40 Zeichen).");
        if (reservedNames.Contains(stem)) return Fail($"/{stem} ist ein eingebauter Befehl");
        if (Encoding.UTF8.GetByteCount(contents) > MaxFileBytes) return Fail("Die Datei ist größer als 32 KB");

        var text = contents;
        if (text.StartsWith('﻿')) text = text[1..];
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var issues = new List<UserCommandIssue>();
        var meta = new Dictionary<string, string>();
        var body = text;

        var lines = text.Split('\n');
        if (lines[0].Trim(' ', '\t') == "---")
        {
            var close = -1;
            for (var i = 1; i < lines.Length; i++)
                if (lines[i].Trim(' ', '\t') == "---") { close = i; break; }
            if (close > 0)
            {
                for (var i = 1; i < close; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon < 0) continue;
                    var key = lines[i][..colon].Trim(' ', '\t').ToLowerInvariant();
                    if (key.Length == 0 || key.StartsWith('#')) continue;
                    meta[key] = Unquote(lines[i][(colon + 1)..].Trim(' ', '\t'));
                }
                body = string.Join("\n", lines.Skip(close + 1));
            }
        }
        body = body.Trim();
        if (body.Length == 0) return Fail("Der Befehlstext ist leer");

        var scope = UserCommandScope.Both;
        if (meta.TryGetValue("scope", out var raw) && raw.Length > 0)
        {
            var parsed = raw.ToLowerInvariant() switch
            {
                "meeting" => UserCommandScope.Meeting,
                "chat" => UserCommandScope.Chat,
                "both" => UserCommandScope.Both,
                _ => (UserCommandScope?)null,
            };
            if (parsed is { } known)
                scope = known;
            else
                issues.Add(new UserCommandIssue(fileName,
                    $"Unbekannter Bereich „{raw}“ (meeting, chat oder both); es wird both genutzt", true));
        }

        string label;
        if (meta.TryGetValue("label", out var l) && l.Length > 0) label = l;
        else
        {
            var spaced = stem.Replace('-', ' ');
            label = char.ToUpperInvariant(spaced[0]) + spaced[1..];
        }

        string description;
        if (meta.TryGetValue("description", out var d) && d.Length > 0) description = d;
        else
        {
            var first = body.Split('\n').Select(x => x.Trim(' ', '\t')).FirstOrDefault(x => x.Length > 0) ?? "";
            var cleaned = first.TrimStart('#', ' ');
            description = cleaned.Length > MaxDescriptionLength ? cleaned[..MaxDescriptionLength] + "…" : cleaned;
        }

        return (new UserCommand(stem, label, description, scope, body, filePath ?? "/" + fileName), issues);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    /// <summary>The body with <paramref name="arguments"/> typed after the command appended as extra instructions.</summary>
    public static string AppendingArguments(string body, string? arguments)
    {
        var args = (arguments ?? "").Trim();
        return args.Length == 0 ? body : body + "\n\nAdditional instructions: " + args;
    }
}

/// <summary>The folder with the command files (<c>UserContext\commands</c>) and the template for a new one.</summary>
public sealed class UserCommandStore(string root)
{
    /// <summary>A new example command: German version of the Mac template.</summary>
    public static string TemplateContents { get; } = """
---
# label: der Text der Taste über dem Eingabefeld des Meeting-Chats
label: Protokoll
# description: die Zeile in der Befehlsliste
description: Das Protokoll des Meetings in einem festen Format schreiben
# scope: meeting (nur Meeting-Chats), chat (nur normale Chats) oder both
scope: meeting
---
Schreibe aus dem Transkript das Protokoll dieses Meetings. Nutze genau diese Abschnitte:

## Teilnehmende
## Themen
## Entscheidungen
## Aufgaben (wer, was, bis wann)

Kurz und sachlich. Nichts ergänzen, was nicht gesagt wurde.
""".ReplaceLineEndings("\n");

    /// <summary>Default location: %APPDATA%\WhisperShortcut\UserContext\commands.</summary>
    public static string DefaultRoot => Path.Combine(AppData.SettingsFolder, "UserContext", "commands");

    /// <summary>The folder holding the command files.</summary>
    public string Root { get; } = root;

    /// <summary>
    /// Reads all <c>*.md</c> files (creating the folder first), sorted by file name. Commands are de-duplicated by name
    /// (first wins) and sorted by name. Never throws; problems are returned as issues.
    /// </summary>
    public (IReadOnlyList<UserCommand> Commands, IReadOnlyList<UserCommandIssue> Issues) Load(IReadOnlySet<string> reserved)
    {
        var found = new List<UserCommand>();
        var problems = new List<UserCommandIssue>();
        string[] files;
        try
        {
            Directory.CreateDirectory(Root);
            files = Directory.GetFiles(Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Befehle: Ordner nicht lesbar", ex);
            return (found, new[] { new UserCommandIssue(Path.GetFileName(Root), ex.Message, false) });
        }

        var strict = new UTF8Encoding(false, true);
        foreach (var path in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Hidden) != 0) continue;
                var data = File.ReadAllBytes(path);
                if (data.Length > UserCommandParser.MaxFileBytes)
                {
                    problems.Add(new UserCommandIssue(name, "Die Datei ist größer als 32 KB", false));
                    continue;
                }
                string text;
                try { text = strict.GetString(data); }
                catch (DecoderFallbackException)
                {
                    problems.Add(new UserCommandIssue(name, "Die Datei ist kein gültiger UTF-8-Text", false));
                    continue;
                }
                var (command, issues) = UserCommandParser.Parse(name, text, reserved, path);
                problems.AddRange(issues);
                if (command != null) found.Add(command);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(new UserCommandIssue(name, ex.Message, false));
            }
        }

        var seen = new HashSet<string>();
        var unique = found.Where(c => seen.Add(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        return (unique, problems);
    }

    /// <summary>Writes <see cref="TemplateContents"/> to the first free of protokoll.md, protokoll-2.md, … and returns its path.</summary>
    public string CreateFromTemplate()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "protokoll.md");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(Root, $"protokoll-{n}.md");
        File.WriteAllText(path, TemplateContents, new UTF8Encoding(false));
        return path;
    }

    /// <summary>The command called <paramref name="name"/> (case-insensitive, a leading "/" is ignored), or null.</summary>
    public static UserCommand? Find(IReadOnlyList<UserCommand> commands, string name)
    {
        var key = (name ?? "").TrimStart('/');
        return commands.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
    }
}
