using System.Text.Json.Nodes;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ChatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsw-chats-" + Guid.NewGuid().ToString("N"));
    private readonly ChatSessionStore _store;

    public ChatTests() => _store = new ChatSessionStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private static ChatMessage U(string text, int min = 0) => new(ChatRole.User, text, T0.AddMinutes(min));
    private static ChatMessage M(string text, int min = 0) => new(ChatRole.Model, text, T0.AddMinutes(min));

    private static ChatSession Session(string text, DateTimeOffset updated)
    {
        var s = ChatSession.New(updated);
        s.Add(new ChatMessage(ChatRole.User, text, updated));
        return s;
    }

    // ---- title ----

    [Theory]
    [InlineData("Hallo Welt", "Hallo Welt")]
    [InlineData("  Erste Zeile  \nzweite Zeile", "Erste Zeile")]
    [InlineData("viel    Platz\tdazwischen", "viel Platz dazwischen")]
    [InlineData("", "Neuer Chat")]
    [InlineData("   \n  ", "Neuer Chat")]
    public void TitleFrom_takes_first_line_and_collapses_whitespace(string input, string expected) =>
        Assert.Equal(expected, ChatText.TitleFrom(input));

    [Fact]
    public void TitleFrom_cuts_at_50_chars_with_ellipsis()
    {
        var fifty = new string('a', 50);
        Assert.Equal(fifty, ChatText.TitleFrom(fifty));
        Assert.Equal(fifty + "…", ChatText.TitleFrom(fifty + "b"));
    }

    [Fact]
    public void TitleFrom_does_not_split_a_surrogate_pair()
    {
        var text = new string('a', 49) + "😀 mehr";
        var title = ChatText.TitleFrom(text);
        Assert.EndsWith("…", title);
        Assert.DoesNotContain('\uD83D', title);
    }

    [Fact]
    public void Add_names_the_chat_after_the_first_user_message_only()
    {
        var s = ChatSession.New(T0);
        Assert.Equal("Neuer Chat", s.Title);
        s.Add(U("Wie spät ist es?\nBitte genau", 1));
        s.Add(M("Kurz nach zehn.", 2));
        s.Add(U("Danke", 3));
        Assert.Equal("Wie spät ist es?", s.Title);
        Assert.Equal(T0.AddMinutes(3), s.Updated);
        Assert.Equal(3, s.Messages.Count);
    }

    // ---- markdown ----

    [Fact]
    public void ToMarkdown_matches_the_mac_format()
    {
        var s = ChatSession.New(T0);
        s.Add(U("Hallo", 0));
        s.Add(M("Hi!", 5));
        var md = ChatText.ToMarkdown(s);
        Assert.Equal("# Hallo\n_2026-11-04T10:05:00Z_\n\n## User\nHallo\n\n## Assistant\nHi!\n", md);
    }

    [Fact]
    public void ToMarkdown_falls_back_to_chat_for_a_blank_title()
    {
        var s = ChatSession.New(T0);
        s.Title = "  ";
        Assert.StartsWith("# Chat\n", ChatText.ToMarkdown(s));
    }

    // ---- flatten ----

    [Fact]
    public void Flatten_puts_history_before_the_last_user_message()
    {
        var text = ChatText.Flatten("SYS", new[] { U("Frage 1"), M("Antwort 1"), U("Frage 2") });
        Assert.Equal("SYS\n\nBisheriger Verlauf:\nNutzer: Frage 1\nAssistent: Antwort 1\n\nAktuelle Nachricht:\nFrage 2", text);
    }

    [Fact]
    public void Flatten_without_history_is_system_prompt_and_question()
    {
        Assert.Equal("SYS\n\nHallo", ChatText.Flatten("SYS", new[] { U("Hallo") }));
        Assert.Equal("Hallo", ChatText.Flatten("", new[] { U("Hallo") }));
    }

    [Fact]
    public void Flatten_with_empty_system_prompt_has_no_blank_system_block()
    {
        var text = ChatText.Flatten("", new[] { U("Frage 1"), M("Antwort 1"), U("Frage 2") });
        Assert.StartsWith("Bisheriger Verlauf:", text);
    }

    // ---- window ----

    [Fact]
    public void Window_with_local_budget_keeps_last_user_message_and_fits()
    {
        var all = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            all.Add(U(new string('u', 5_000)));
            all.Add(M(new string('m', 5_000)));
        }
        all.Add(U("letzte Frage"));
        var w = ChatText.Window(all, ChatText.LocalWindowChars);
        Assert.Equal(all[^1], w[^1]);
        Assert.Equal(ChatRole.User, w[0].Role);
        Assert.True(w.Sum(m => m.Text.Length) <= ChatText.LocalWindowChars);
        Assert.True(w.Count < all.Count);
    }

    [Fact]
    public void Window_returns_everything_when_it_fits()
    {
        var all = new[] { U("a"), M("b"), U("c") };
        Assert.Equal(all, ChatText.Window(all));
    }

    [Fact]
    public void Window_keeps_newest_and_starts_with_a_user_message()
    {
        var all = new[] { U("1111"), M("2222"), U("3333"), M("4444"), U("5555") };
        // 12 chars fit the last three; they already start with a user message.
        Assert.Equal(new[] { all[2], all[3], all[4] }, ChatText.Window(all, 12));
        // 9 chars fit the last two (M, U): the leading model message is dropped.
        Assert.Equal(new[] { all[4] }, ChatText.Window(all, 9));
    }

    [Fact]
    public void Window_always_keeps_the_last_message_even_if_too_long()
    {
        var all = new[] { U("kurz"), M("antwort"), U(new string('x', 500)) };
        var w = ChatText.Window(all, 10);
        Assert.Single(w);
        Assert.Equal(all[2], w[0]);
    }

    [Fact]
    public void Window_of_nothing_is_nothing() => Assert.Empty(ChatText.Window(Array.Empty<ChatMessage>()));

    // ---- store ----

    [Fact]
    public void Store_round_trips_a_session_with_sources_and_model()
    {
        var s = ChatSession.New(T0);
        s.Add(U("Frage mit Umlaut äöü"));
        s.Add(new ChatMessage(ChatRole.Model, "Antwort", T0.AddSeconds(3),
            new[] { new ChatSource("Titel", "https://example.com/a") }, "Gemini 3.8 Flash"));
        _store.Save(s);

        var back = _store.Load(s.Id)!;
        Assert.Equal(s.Id, back.Id);
        Assert.Equal(s.Title, back.Title);
        Assert.Equal(s.Created, back.Created);
        Assert.Equal(s.Updated, back.Updated);
        Assert.Equal(2, back.Messages.Count);
        Assert.Equal(ChatRole.Model, back.Messages[1].Role);
        Assert.Equal("Gemini 3.8 Flash", back.Messages[1].Model);
        Assert.Equal("https://example.com/a", back.Messages[1].Sources![0].Url);
        Assert.Null(back.Messages[0].Sources);
    }

    [Fact]
    public void Store_round_trips_the_thinking_level_and_old_files_load_as_default()
    {
        var s = Session("Hallo", T0);
        s.ThinkingLevel = "high";
        _store.Save(s);
        Assert.Equal("high", _store.Load(s.Id)!.ThinkingLevel);
        var path = Path.Combine(_root, s.Id.ToString("D") + ".json");
        Assert.Equal("high", (string?)JsonNode.Parse(File.ReadAllText(path))!["thinkingLevel"]);

        var old = Session("alt", T0);
        _store.Save(old);
        var oldPath = Path.Combine(_root, old.Id.ToString("D") + ".json");
        var node = JsonNode.Parse(File.ReadAllText(oldPath))!.AsObject();
        node.Remove("thinkingLevel");
        File.WriteAllText(oldPath, node.ToJsonString());
        Assert.Equal("default", _store.Load(old.Id)!.ThinkingLevel);
    }

    [Fact]
    public void Store_round_trips_attachments()
    {
        var s = ChatSession.New(T0);
        s.Add(new ChatMessage(ChatRole.User, "Bild", T0, null, null,
            new[] { new ChatAttachment("a.png", "image/png", "QUJD"), new ChatAttachment("n.txt", "text/plain", "eA==") }));
        _store.Save(s);
        var back = _store.Load(s.Id)!.Messages[0].Attachments!;
        Assert.Equal(2, back.Count);
        Assert.Equal(new ChatAttachment("a.png", "image/png", "QUJD"), back[0]);
        Assert.Equal("n.txt", back[1].FileName);
    }

    [Fact]
    public void Window_counts_attachment_data_toward_the_limit()
    {
        var big = new ChatMessage(ChatRole.User, "a", T0, null, null, new[] { new ChatAttachment("x.png", "image/png", new string('A', 500)) });
        var msgs = new[] { big, M("b"), U("c") };
        Assert.Single(ChatText.Window(msgs, 100)); // the image message no longer fits; only the last user message stays
        Assert.Equal(3, ChatText.Window(msgs, 1000).Count);
        // The last message is kept even when it alone is over the limit.
        var last = new[] { U("x"), big };
        Assert.Single(ChatText.Window(last, 100));
    }

    [Fact]
    public void Flatten_appends_text_files_and_ignores_images()
    {
        var txt = new ChatAttachment("n.txt", "text/plain", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Inhalt")));
        var png = new ChatAttachment("a.png", "image/png", "QUJD");
        var msgs = new[] { new ChatMessage(ChatRole.User, "Lies das", T0, null, null, new[] { txt, png }) };
        Assert.Equal("Lies das\n\nDatei „n.txt“:\nInhalt", ChatText.Flatten("", msgs));
    }

    [Fact]
    public void ToMarkdown_lists_attachment_names()
    {
        var s = ChatSession.New(T0);
        s.Add(new ChatMessage(ChatRole.User, "Hi", T0, null, null,
            new[] { new ChatAttachment("a.png", "image/png", "QQ=="), new ChatAttachment("b.pdf", "application/pdf", "QQ==") }));
        Assert.Contains("## User\nHi\n_Anhänge: a.png, b.pdf_\n", ChatText.ToMarkdown(s));
    }

    [Fact]
    public void Store_writes_camelCase_json_named_after_the_id()
    {
        var s = Session("Hallo", T0);
        _store.Save(s);
        var path = Path.Combine(_root, s.Id.ToString("D") + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(s.Id.ToString(), (string?)json["id"]);
        Assert.Equal("user", (string?)json["messages"]![0]!["role"]);
        Assert.Equal("Hallo", (string?)json["messages"]![0]!["text"]);
    }

    [Fact]
    public void Store_lists_newest_updated_first()
    {
        var old = Session("alt", T0);
        var mid = Session("mitte", T0.AddHours(1));
        var young = Session("neu", T0.AddHours(2));
        _store.Save(mid);
        _store.Save(young);
        _store.Save(old);
        Assert.Equal(new[] { young.Id, mid.Id, old.Id }, _store.List().Select(s => s.Id));
    }

    [Fact]
    public void Store_does_not_write_sessions_without_messages()
    {
        _store.Save(ChatSession.New(T0));
        Assert.Empty(_store.List());
        Assert.False(Directory.Exists(_root) && Directory.GetFiles(_root).Length > 0);
    }

    [Fact]
    public void Store_save_overwrites_and_leaves_no_temp_file()
    {
        var s = Session("eins", T0);
        _store.Save(s);
        s.Add(M("zwei", 1));
        _store.Save(s);
        Assert.Equal(2, _store.Load(s.Id)!.Messages.Count);
        Assert.Single(Directory.GetFiles(_root));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Store_delete_removes_the_file_and_tolerates_a_missing_one()
    {
        var s = Session("x", T0);
        _store.Save(s);
        _store.Delete(s.Id);
        Assert.Null(_store.Load(s.Id));
        _store.Delete(s.Id);
        _store.Delete(Guid.NewGuid());
    }

    [Fact]
    public void Store_skips_corrupt_files()
    {
        var good = Session("gut", T0);
        _store.Save(good);
        File.WriteAllText(Path.Combine(_root, Guid.NewGuid().ToString("D") + ".json"), "{ not json");
        File.WriteAllText(Path.Combine(_root, Guid.NewGuid().ToString("D") + ".json"), "{}");
        var list = _store.List();
        Assert.Single(list);
        Assert.Equal(good.Id, list[0].Id);
    }

    [Fact]
    public void Store_load_of_unknown_or_corrupt_id_is_null()
    {
        Assert.Null(_store.Load(Guid.NewGuid()));
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, id.ToString("D") + ".json"), "garbage");
        Assert.Null(_store.Load(id));
    }

    [Fact]
    public void Store_list_of_a_missing_folder_is_empty() => Assert.Empty(_store.List());

    [Fact]
    public void Default_root_is_a_chats_folder_next_to_the_settings() =>
        Assert.Equal("Chats", Path.GetFileName(ChatSessionStore.DefaultRoot));

    // ---- commands ----

    [Fact]
    public void Commands_are_listed_in_the_mac_order()
    {
        Assert.Equal(new[]
            {
                "/new", "/screenshot", "/attach", "/model", "/gemini",
                "/gemini35flashlite", "/gemini31flashlite", "/gemini4argon",
                "/gpt", "/gpt6luna", "/gpt54mini",
                "/think", "/settings", "/pin", "/unpin", "/meeting", "/copy", "/feedback",
            },
            ChatCommands.All.Select(c => c.Command));
        Assert.Equal("Zu Gemini 3.5 Flash-Lite wechseln", ChatCommands.All.Single(c => c.Command == "/gemini35flashlite").Description);
        Assert.Equal("Zu GPT-6 Luna wechseln", ChatCommands.All.Single(c => c.Command == "/gpt6luna").Description);
        Assert.Equal("Denktiefe für diesen Chat festlegen: minimal | low | medium | high | default",
            ChatCommands.All.Single(c => c.Command == "/think").Description);
        Assert.All(ChatCommands.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void Suggestions_filter_by_prefix_case_insensitively()
    {
        Assert.Equal(ChatCommands.All.Count, ChatCommands.Suggestions("/").Count);
        Assert.Equal(new[] { "/pin" }, ChatCommands.Suggestions("/pi").Select(c => c.Command));
        Assert.Equal(new[] { "/model", "/meeting" }, ChatCommands.Suggestions("/M").Select(c => c.Command));
        Assert.Equal(new[] { "/unpin" }, ChatCommands.Suggestions("/UN").Select(c => c.Command));
    }

    [Fact]
    public void Suggestions_list_custom_commands_first()
    {
        var custom = new[]
        {
            new UserCommand("protokoll", "Protokoll", "Das Protokoll", UserCommandScope.Meeting, "b", "p"),
            new UserCommand("neu-x", "Neu x", "Eigener", UserCommandScope.Both, "b", "p"),
        };
        Assert.Equal(ChatCommands.All.Count + 2, ChatCommands.Suggestions("/", custom).Count);
        var n = ChatCommands.Suggestions("/n", custom);
        Assert.Equal(new[] { "/neu-x", "/new" }, n.Select(c => c.Command));
        Assert.Equal("Eigener", n[0].Description);
        Assert.Equal("/protokoll", ChatCommands.Suggestions("/", custom)[0].Command);
        Assert.Equal(ChatCommands.All.Count, ChatCommands.Suggestions("/", null).Count);
    }

    [Fact]
    public void ReservedNames_cover_all_built_ins_and_the_silent_alias()
    {
        Assert.All(ChatCommands.All, c => Assert.Contains(c.Command[1..], ChatCommands.ReservedNames));
        Assert.Contains("openai", ChatCommands.ReservedNames);
        Assert.Contains("gpt6luna", ChatCommands.ReservedNames);
        Assert.DoesNotContain("protokoll", ChatCommands.ReservedNames);
    }

    [Theory]
    [InlineData("/gpt6luna", "gpt-6-luna")]
    [InlineData("/GPT54MINI ignoriert", "gpt-5.4-mini")]
    [InlineData("/gemini35flashlite", "gemini-3.5-flash-lite")]
    [InlineData("/gemini4argon", "gemini-4-argon")]
    public void TryParse_model_aliases_carry_the_model_id(string input, string modelId)
    {
        Assert.True(ChatCommands.TryParse(input, out var kind, out var arg));
        Assert.Equal(ChatCommandKind.ModelAlias, kind);
        Assert.Equal(modelId, arg);
    }

    [Theory]
    [InlineData("/screenshot", ChatCommandKind.Screenshot, "")]
    [InlineData("/attach", ChatCommandKind.Attach, "")]
    [InlineData("/think high", ChatCommandKind.Think, "high")]
    [InlineData("/feedback", ChatCommandKind.Feedback, "")]
    public void TryParse_new_commands(string input, ChatCommandKind kind, string argument)
    {
        Assert.True(ChatCommands.TryParse(input, out var k, out var arg));
        Assert.Equal(kind, k);
        Assert.Equal(argument, arg);
    }

    [Fact]
    public void TryParseUser_finds_custom_commands_case_insensitively_with_arguments()
    {
        var custom = new[] { new UserCommand("protokoll", "Protokoll", "d", UserCommandScope.Meeting, "b", "p") };
        Assert.True(ChatCommands.TryParseUser(" /Protokoll  kurz bitte ", custom, out var cmd, out var args));
        Assert.Equal("protokoll", cmd.Name);
        Assert.Equal("kurz bitte", args);
        Assert.False(ChatCommands.TryParseUser("/protokollx", custom, out _, out _));
        Assert.False(ChatCommands.TryParseUser("protokoll", custom, out _, out _));
        Assert.False(ChatCommands.TryParseUser("/new", custom, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hallo")]
    [InlineData("/new chat")]
    [InlineData("/xyz")]
    public void Suggestions_are_empty_for_non_command_input(string input) =>
        Assert.Empty(ChatCommands.Suggestions(input));

    [Theory]
    [InlineData("/new", ChatCommandKind.New, "")]
    [InlineData("  /NEW  ", ChatCommandKind.New, "")]
    [InlineData("/model gpt-5", ChatCommandKind.Model, "gpt-5")]
    [InlineData("/gemini", ChatCommandKind.Gemini, "")]
    [InlineData("/gpt", ChatCommandKind.Gpt, "")]
    [InlineData("/openai", ChatCommandKind.Gpt, "")]
    [InlineData("/Settings", ChatCommandKind.Settings, "")]
    [InlineData("/pin", ChatCommandKind.Pin, "")]
    [InlineData("/unpin", ChatCommandKind.Unpin, "")]
    [InlineData("/meeting", ChatCommandKind.Meeting, "")]
    [InlineData("/copy   alles  ", ChatCommandKind.Copy, "alles")]
    public void TryParse_recognises_commands(string input, ChatCommandKind kind, string argument)
    {
        Assert.True(ChatCommands.TryParse(input, out var k, out var arg));
        Assert.Equal(kind, k);
        Assert.Equal(argument, arg);
    }

    [Theory]
    [InlineData("/xyz")]
    [InlineData("/xyz abc")]
    [InlineData("/newish")]
    [InlineData("new")]
    [InlineData("Hallo /new")]
    [InlineData("")]
    public void TryParse_rejects_unknown_input(string input)
    {
        Assert.False(ChatCommands.TryParse(input, out _, out var arg));
        Assert.Equal("", arg);
    }

    // ---- prompts ----

    [Fact]
    public void Effective_prefers_a_custom_prompt_and_ignores_blank_ones()
    {
        Assert.Equal("Eigener", ChatPrompts.Effective("Eigener", offline: false));
        Assert.Equal("Eigener", ChatPrompts.Effective("Eigener", offline: true));
        Assert.Equal(ChatPrompts.DefaultSystemPrompt, ChatPrompts.Effective(null, offline: false));
        Assert.Equal(ChatPrompts.DefaultSystemPrompt, ChatPrompts.Effective("   ", offline: false));
        Assert.Equal(ChatPrompts.DefaultOfflineSystemPrompt, ChatPrompts.Effective("", offline: true));
    }

    [Fact]
    public void Default_prompts_drop_images_and_connected_services_and_say_pc()
    {
        var p = ChatPrompts.DefaultSystemPrompt;
        Assert.Contains("web search tool", p);
        Assert.StartsWith("Language rule", p);
        Assert.DoesNotContain("Attached images", p);
        Assert.DoesNotContain("Gmail", p);
        Assert.DoesNotContain("Google Calendar", p);
        Assert.DoesNotContain("Google Tasks", p);
        Assert.Contains("offline on the user's PC", ChatPrompts.DefaultOfflineSystemPrompt);
        Assert.DoesNotContain("Mac", ChatPrompts.DefaultOfflineSystemPrompt);
    }

    [Fact]
    public void A_named_chat_keeps_its_name_after_the_first_question()
    {
        var session = ChatSession.New(DateTimeOffset.Now);
        session.MeetingId = "m1";
        session.Title = "📝 Meeting 04.11.";
        session.Add(new ChatMessage(ChatRole.User, "Was wurde entschieden?", DateTimeOffset.Now));
        Assert.Equal("📝 Meeting 04.11.", session.Title);

        var plain = ChatSession.New(DateTimeOffset.Now);
        plain.Add(new ChatMessage(ChatRole.User, "Hallo Welt", DateTimeOffset.Now));
        Assert.Equal("Hallo Welt", plain.Title);
    }
}
