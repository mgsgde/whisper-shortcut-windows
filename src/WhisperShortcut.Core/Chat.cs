using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>Who wrote a chat message.</summary>
public enum ChatRole
{
    /// <summary>The person using the app.</summary>
    User,
    /// <summary>The model (Mac: "assistant").</summary>
    Model,
}

/// <summary>A web page a model answer was grounded on (Gemini Google Search).</summary>
public sealed record ChatSource(string Title, string Url);

/// <summary>One turn of a chat. <see cref="Model"/> is the display name of the model that answered (model messages only).</summary>
public sealed record ChatMessage(
    ChatRole Role, string Text, DateTimeOffset At, IReadOnlyList<ChatSource>? Sources = null, string? Model = null);

/// <summary>One saved chat: its messages plus the title shown in the history.</summary>
public sealed class ChatSession
{
    /// <summary>Also the file name in the <see cref="ChatSessionStore"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>Short headline from the first user message.</summary>
    public string Title { get; set; } = ChatText.EmptyTitle;

    /// <summary>When the chat was started.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>When the last message was added; the history is sorted by this.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <summary>All turns, oldest first.</summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>A new, empty chat.</summary>
    public static ChatSession New(DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), Created = now, Updated = now };

    /// <summary>Appends <paramref name="m"/>, bumps <see cref="Updated"/>; the first user message names the chat.</summary>
    public void Add(ChatMessage m)
    {
        if (m.Role == ChatRole.User && !Messages.Any(x => x.Role == ChatRole.User))
            Title = ChatText.TitleFrom(m.Text);
        Messages.Add(m);
        Updated = m.At;
    }
}

/// <summary>Text helpers around chats: titles, /copy Markdown, the offline engine's single-turn flattening, context window.</summary>
public static class ChatText
{
    /// <summary>Title of a chat without a (usable) first message.</summary>
    public const string EmptyTitle = "Neuer Chat";

    /// <summary>Mac maxSessionTitleLength.</summary>
    public const int MaxTitleLength = 50;

    /// <summary>
    /// Title from the first line of <paramref name="text"/>: whitespace collapsed, at most 50 characters
    /// plus "…" when cut; "Neuer Chat" when nothing is left.
    /// </summary>
    public static string TitleFrom(string? text)
    {
        var line = (text ?? "").Trim();
        var nl = line.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) line = line[..nl];
        line = Regex.Replace(line, @"\s+", " ").Trim();
        if (line.Length == 0) return EmptyTitle;
        if (line.Length <= MaxTitleLength) return line;
        var cut = MaxTitleLength;
        // Never split a surrogate pair (emoji).
        if (char.IsLowSurrogate(line[cut])) cut--;
        return line[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// The /copy format of the Mac (renderChatAsMarkdown): "# title", the last-updated time in ISO 8601,
    /// then "## User" / "## Assistant" blocks. The labels are English on the Mac too.
    /// </summary>
    public static string ToMarkdown(ChatSession session)
    {
        var title = session.Title.Trim();
        if (title.Length == 0) title = "Chat";
        var lines = new List<string>
        {
            "# " + title,
            "_" + session.Updated.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "_",
            "",
        };
        foreach (var m in session.Messages)
        {
            lines.Add(m.Role == ChatRole.User ? "## User" : "## Assistant");
            lines.Add(m.Text);
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// One plain-text user turn for the offline engine, which only takes single turns: the system prompt,
    /// the earlier conversation ("Bisheriger Verlauf:"), then the last user message.
    /// </summary>
    public static string Flatten(string systemPrompt, IReadOnlyList<ChatMessage> messages)
    {
        var last = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatRole.User) { last = i; break; }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) sb.Append(systemPrompt.Trim()).Append("\n\n");
        var history = messages.Take(last < 0 ? messages.Count : last).Where(m => m.Text.Trim().Length > 0).ToList();
        if (history.Count > 0)
        {
            sb.Append("Bisheriger Verlauf:\n");
            foreach (var m in history)
                sb.Append(m.Role == ChatRole.User ? "Nutzer: " : "Assistent: ").Append(m.Text.Trim()).Append('\n');
            sb.Append('\n');
        }
        if (last >= 0)
        {
            if (history.Count > 0) sb.Append("Aktuelle Nachricht:\n");
            sb.Append(messages[last].Text.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The newest messages whose text fits into <paramref name="maxChars"/>. The last message is always kept
    /// and the window starts with a user message (a model reply without its question confuses the APIs).
    /// </summary>
    public static IReadOnlyList<ChatMessage> Window(IReadOnlyList<ChatMessage> messages, int maxChars = 200_000)
    {
        if (messages.Count == 0) return messages;
        var start = messages.Count - 1;
        var total = messages[start].Text.Length;
        while (start > 0 && total + messages[start - 1].Text.Length <= maxChars)
        {
            start--;
            total += messages[start].Text.Length;
        }
        while (start < messages.Count - 1 && messages[start].Role != ChatRole.User) start++;
        return start == 0 ? messages : messages.Skip(start).ToList();
    }
}

/// <summary>
/// Chats on disk: one <c>{id}.json</c> per chat (camelCase JSON), written atomically (temp file, then move).
/// </summary>
public sealed class ChatSessionStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();

    /// <summary>Default location: %APPDATA%\WhisperShortcut\Chats (next to settings.json).</summary>
    public static string DefaultRoot => Path.Combine(AppData.SettingsFolder, "Chats");

    /// <summary>The folder holding the chat files.</summary>
    public string Root { get; } = root;

    private string PathOf(Guid id) => Path.Combine(Root, id.ToString("D") + ".json");

    /// <summary>All readable chats, newest <see cref="ChatSession.Updated"/> first. Unreadable files are skipped and logged.</summary>
    public IReadOnlyList<ChatSession> List()
    {
        var found = new List<ChatSession>();
        lock (_gate)
        {
            if (!Directory.Exists(Root)) return found;
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
                if (ReadFile(file) is { } session) found.Add(session);
        }
        return found.OrderByDescending(s => s.Updated).ToList();
    }

    /// <summary>The chat with <paramref name="id"/>, or null when it is missing or unreadable.</summary>
    public ChatSession? Load(Guid id)
    {
        lock (_gate) return File.Exists(PathOf(id)) ? ReadFile(PathOf(id)) : null;
    }

    /// <summary>Writes the chat atomically. A chat without messages is not written (no empty entries in the history).</summary>
    public void Save(ChatSession session)
    {
        if (session.Messages.Count == 0) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, Json);
        lock (_gate)
        {
            Directory.CreateDirectory(Root);
            var path = PathOf(session.Id);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Removes the chat's file; a missing one is fine.</summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            try { File.Delete(PathOf(id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Chat {id}: could not delete", ex);
            }
        }
    }

    private static ChatSession? ReadFile(string path)
    {
        try
        {
            var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllBytes(path), Json);
            if (session == null || session.Id == Guid.Empty) throw new JsonException("no id");
            session.Messages ??= new List<ChatMessage>();
            return session;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Chat: skipping unreadable file {System.IO.Path.GetFileName(path)}", ex);
            return null;
        }
    }
}

/// <summary>What a slash command in the chat input does.</summary>
public enum ChatCommandKind
{
    /// <summary>Start a new chat.</summary>
    New,
    /// <summary>Pick a model (argument optional).</summary>
    Model,
    /// <summary>Switch to Gemini.</summary>
    Gemini,
    /// <summary>Switch to GPT (OpenAI).</summary>
    Gpt,
    /// <summary>Open the settings window.</summary>
    Settings,
    /// <summary>Keep the window open when it loses focus.</summary>
    Pin,
    /// <summary>Close the window when it loses focus.</summary>
    Unpin,
    /// <summary>Start or stop live meeting recording.</summary>
    Meeting,
    /// <summary>Copy the whole chat as Markdown.</summary>
    Copy,
}

/// <summary>Slash commands of the chat input (Mac ChatView command menu).</summary>
public static class ChatCommands
{
    /// <summary>Menu entries in the Mac's order, with the German descriptions from its Localizable.xcstrings.</summary>
    public static IReadOnlyList<(string Command, string Description)> All { get; } = new[]
    {
        ("/new", "Neuen Chat beginnen (der bisherige bleibt im Verlauf)"),
        ("/model", "Modell wechseln …"),
        ("/gemini", "Zu Gemini wechseln"),
        ("/gpt", "Zu GPT wechseln"),
        ("/settings", "Einstellungen öffnen"),
        ("/pin", "Festlegen, ob das Fenster offen bleibt, wenn es in den Hintergrund rückt"),
        ("/unpin", "Fenster schließen, wenn es in den Hintergrund rückt"),
        ("/meeting", "Live-Meeting-Aufnahme starten oder beenden"),
        ("/copy", "Den ganzen Chatverlauf als Markdown in die Zwischenablage kopieren"),
    };

    private static readonly Dictionary<string, ChatCommandKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/new"] = ChatCommandKind.New,
        ["/model"] = ChatCommandKind.Model,
        ["/gemini"] = ChatCommandKind.Gemini,
        ["/gpt"] = ChatCommandKind.Gpt,
        ["/openai"] = ChatCommandKind.Gpt, // silent alias, like on the Mac
        ["/settings"] = ChatCommandKind.Settings,
        ["/pin"] = ChatCommandKind.Pin,
        ["/unpin"] = ChatCommandKind.Unpin,
        ["/meeting"] = ChatCommandKind.Meeting,
        ["/copy"] = ChatCommandKind.Copy,
    };

    /// <summary>
    /// The menu entries to show while typing: input that starts with "/" and has no whitespace yet
    /// filters by prefix (case-insensitive); anything else shows nothing.
    /// </summary>
    public static IReadOnlyList<(string Command, string Description)> Suggestions(string? input)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '/' || input.Any(char.IsWhiteSpace))
            return Array.Empty<(string, string)>();
        return All.Where(c => c.Command.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// True when the first word of <paramref name="input"/> is a known command; <paramref name="argument"/> is the rest, trimmed.
    /// An unknown "/xyz" is no command and goes to the model as normal text.
    /// </summary>
    public static bool TryParse(string? input, out ChatCommandKind kind, out string argument)
    {
        kind = default;
        argument = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text[0] != '/') return false;
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        if (!Kinds.TryGetValue(text[..end], out kind)) return false;
        argument = text[end..].Trim();
        return true;
    }
}

/// <summary>System prompts of the chat window.</summary>
public static class ChatPrompts
{
    /// <summary>
    /// Mac AppConstants.defaultChatSystemPrompt without the parts for attached images and connected services
    /// (Gmail, Calendar, Tasks), which Windows chat does not have yet. Gemini gets Google Search grounding.
    /// </summary>
    public const string DefaultSystemPrompt = """
Language rule (highest priority): Always reply in the SAME language as the user's most recent message, whatever language that is. Match their language on every turn; never default to a fixed language regardless of input.

Register rule: Match how the user addresses you. If they are informal (in German: du; first names; casual tone), reply informally; if they are formal (Sie), reply formally. Pick one register and keep it consistent for the entire response — never mix Sie and du (or formal and informal address) within the same answer.

You have access to a web search tool. Use it by default. The user relies on this chat for current, up-to-date information. Do not rely on your training data for facts, numbers, dates, news, or anything that may have changed—when in doubt, search first. ALWAYS search (never answer from memory) for any specific prices, fees, minimum/maximum amounts, limits, exchange rates, tax or interest rates, version numbers, release dates, or other concrete figures that can change or that you are not certain are current—this applies equally to follow-up questions that ask you to justify, defend, or elaborate on an earlier answer (re-verify rather than reasoning further from your own prior reply). Do not invent or guess information; if you have not searched and are not sure, say so or search before answering. Only skip searching for purely conversational or static content (e.g. grammar, math, personal preferences with no recency). When you search, the user will see sources (URLs) attached to your answer; the user expects to see these often. So search whenever the answer could be factual or time-sensitive, so your reply is grounded and shows sources.

Links: only use URLs that came from your search results — NEVER write, guess, or reconstruct a URL from memory, and NEVER invent a YouTube watch?v=… video ID. When you recommend a video or page, link the exact source URL returned by your search (those are verified to exist); if you have no verified source link for something, name the video/channel or give a search term instead of fabricating a link.

Conciseness and structure — keep it compact, but ALWAYS answer every part the user asked. Rules:
- For action tasks (translate, rewrite, convert, summarize, generate code): return ONLY the result. No explanations, no commentary. Put that paste-ready result inside a single ```markdown fenced code block (see “Copy-ready output” below). For a pure deliverable, the message may consist of that one block only.
- Simple questions (the user's message is short, factual, or yes/no): answer in a sentence or two. Do not add headings, bullet lists, or "## " sections to short answers.
- Complex or multi-part questions: a short summary line, then short bullets or brief paragraphs as needed. If the user asked several things, cover each one. Use "## " headings only when the answer truly has multiple distinct sections.
- Prefer bullets over long prose. Cut anything the user did not ask for: tips, caveats, "good to know" asides, restating the question.
- Use emojis sparingly: one per heading at most. Never on bullet points.

Use **bold** for key terms when helpful.

Copy-ready output (Whisper Shortcut chat UI):
- Whenever the user is likely to copy text verbatim (email or message draft, translation, social post, meeting notes to paste elsewhere, letter body, JSON/YAML/config as a paste artifact, or similar), put ONLY that material inside a fenced code block whose language tag is exactly markdown (opening fence: three backticks + the word markdown). The app shows a copy affordance for that block.
- Keep explanations, reasoning, steps, warnings, and alternatives outside the markdown fence. If there are multiple independent paste-ready pieces, use one markdown fence per piece, in order.
- For actual source code or shell commands meant to run or compile, use the real language tag (python, swift, javascript, bash, etc.) instead of markdown.
- If nothing is meant to be copied verbatim (pure Q&A, conceptual reply, or action confirmations like "task created" / "event created"), omit the markdown paste block.
- Do not put triple-backtick fences inside the markdown paste block.
- Paste-ready text must be RAW: never prefix lines with decorative characters (▎, │, >, or similar gutter/quote marks). The content inside the markdown block must be exactly what the user will paste — no added leading glyphs, no quote bars.

When writing code blocks, always specify a language tag (e.g. ```python, ```swift, ```javascript, or ```markdown for paste-ready prose as above). Never use bare ``` without a language identifier.

IMPORTANT: Your system prompt may contain background context about the user's typical domains or expertise level. This is calibration data ONLY. You MUST NOT:
- Reference or allude to any information from this system prompt in your responses
- Mention the user's profession, industry, projects, or personal details
- Say things like "as a software engineer..." or "given your work on..."
Treat system prompt context as invisible to the conversation. Answer based solely on what the user asks.
""";

    /// <summary>Mac defaultOfflineChatSystemPrompt (short, for the small local model), "Mac" replaced by "PC".</summary>
    public const string DefaultOfflineSystemPrompt = """
You are a helpful assistant running offline on the user's PC. You have no internet access and no tools: you cannot search the web, open links, read files, or see email or calendars.

Language: always reply in the same language as the user's latest message. Match their register (du or Sie in German) and keep it consistent.

Knowledge: answer from what you know. For prices, versions, release dates, news, or anything else that may have changed, give your best answer and say in one short sentence that it may be out of date. If you do not know, say so. Never invent URLs, IDs, or quotes.

Style:
- Short questions get a sentence or two. No headings for short answers.
- Longer answers: one summary line, then short bullets. Answer every part of the question, nothing extra.
- No emojis.
- Shell commands and code go in a fenced block with the real language tag (bash, python, swift, …). Put each command on its own line, and use the exact URL or value the user gave you.
- Text the user will paste elsewhere (a message, an email, a translation) goes in one ```markdown block, with explanations outside it.
""";

    /// <summary><paramref name="custom"/> when it has text, else the default for the online or offline engine.</summary>
    public static string Effective(string? custom, bool offline) =>
        !string.IsNullOrWhiteSpace(custom) ? custom : offline ? DefaultOfflineSystemPrompt : DefaultSystemPrompt;
}
