using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ResultReportTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Local);
    private static readonly string[] NoTerms = Array.Empty<string>();

    private static DictationResult D(string text, int minute = 0, string? raw = null, string? hash = null,
        string? template = null, string mode = "Dictate", IReadOnlyList<string>? echo = null, double? secs = null, string? model = null) =>
        new(T0.AddMinutes(minute), text, mode, model, template, secs, raw, "1.2.3", hash, echo);

    private static string Body(DictationResult d, string[]? terms = null, bool offline = false, string? hash = null) =>
        ResultReport.DictationBody(d, terms ?? NoTerms, offline, hash);

    [Fact]
    public void Candidates_are_newest_first_with_general_last()
    {
        var list = ResultReport.Candidates(
            new[] { D("neu", 5), D("alt", 1) }, NoTerms, false,
            new PromptResult(T0.AddMinutes(3), "frage", null, "antwort", null, null, false, 0, null),
            new ReadAloudResult(T0.AddMinutes(9), "vorlesen", null, null), null, T0.AddHours(1));
        Assert.Equal(new[] { ResultKind.ReadAloud, ResultKind.Dictation, ResultKind.Prompt, ResultKind.Dictation, ResultKind.General },
            list.Select(c => c.Kind));
        Assert.Equal("general", list[^1].Id);
        Assert.Equal("Offline-Modus: aus", list[^1].Details);
        Assert.Equal("", list[^1].Preview);
        Assert.EndsWith("|dictation|0", list[1].Id);
        Assert.EndsWith("|prompt", list[2].Id);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\|", list[0].Id);
    }

    [Fact]
    public void Candidates_take_at_most_ten_dictations_and_name_templates()
    {
        var many = Enumerable.Range(0, 12).Select(i => D("t" + i, 100 - i)).ToList();
        Assert.Equal(11, ResultReport.Candidates(many, NoTerms, true, null, null, null, T0).Count);
        var t = ResultReport.Candidates(new[] { D("x", template: "Brief", mode: "Template") }, NoTerms, true, null, null, null, T0);
        Assert.Equal("Brief", t[0].MenuLabel);
        Assert.Equal("Offline-Modus: an", t[^1].Details);
        Assert.Null(ResultReport.Candidates(new[] { D("x", template: "Brief") }, NoTerms, true, null, null, null, T0)[0].MenuLabel);
    }

    [Fact]
    public void Preview_is_one_line_cut_at_forty()
    {
        var c = ResultReport.Candidates(new[] { D("a\nb\r\nc " + new string('x', 60)) }, NoTerms, false, null, null, null, T0)[0];
        Assert.Equal(41, c.Preview.Length);
        Assert.StartsWith("a b c ", c.Preview);
        Assert.EndsWith("…", c.Preview);
    }

    [Fact]
    public void DictationBody_lists_the_facts()
    {
        var body = Body(D("Hallo Welt", secs: 3.456, template: "Brief", model: "m1"), offline: true);
        var lines = body.Split('\n');
        Assert.Equal("Eingefügter Text:", lines[0]);
        Assert.Equal("Hallo Welt", lines[1]);
        Assert.Equal("", lines[2]);
        Assert.Matches(@"^Diktiert: 2026-11-04 10:00:00 [+-]\d{4}$", lines[3]);
        Assert.Equal(new[]
        {
            "Modus: Dictate", "Transkriptionsmodell: m1", "Glossar: leer", "Offline-Modus: an", "Vorlage: Brief",
            "Audiolänge: 3.5 s", "App-Version beim Diktat: 1.2.3",
        }, lines[4..]);
    }

    [Fact]
    public void Glossary_summary_none_some_and_empty()
    {
        Assert.Contains("Glossar: leer", Body(D("Hallo")));
        Assert.Contains("Glossar: 2 Begriffe, keiner davon in diesem Text.", Body(D("Hallo"), new[] { "Müller", "Qwen" }));
        var some = Body(D("Ich habe mit MÜLLER und Dr. Meier gesprochen"), new[] { "Müller", "Dr Meier", "müller", "Qwen" });
        Assert.Contains("Glossar: 4 Begriffe, in diesem Text: Müller, Dr Meier", some);
        Assert.DoesNotContain("Qwen,", some);
        Assert.Contains("Glossar: 1 Begriffe, keiner davon in diesem Text.", Body(D("Mueller"), new[] { "Müller" }));
    }

    [Fact]
    public void Config_changed_note_needs_both_hashes_and_a_difference()
    {
        Assert.Contains("Hinweis: Einstellungen wurden nach diesem Diktat geändert", Body(D("a", hash: "aaa"), hash: "bbb"));
        Assert.DoesNotContain("Hinweis", Body(D("a", hash: "aaa"), hash: "aaa"));
        Assert.DoesNotContain("Hinweis", Body(D("a", hash: null), hash: "bbb"));
        Assert.DoesNotContain("Hinweis", Body(D("a", hash: "aaa"), hash: null));
    }

    [Fact]
    public void Raw_output_line_only_when_it_differs()
    {
        Assert.EndsWith("Modellausgabe vor der Bereinigung:\nroh", Body(D("sauber", raw: "roh")));
        Assert.DoesNotContain("Modellausgabe", Body(D("gleich", raw: "gleich")));
    }

    [Fact]
    public void TechnicalLines_cases()
    {
        Assert.Equal("", ResultReport.TechnicalLines(D("a")));
        Assert.Equal("Entferntes Glossar-Echo: keins", ResultReport.TechnicalLines(D("a", echo: Array.Empty<string>())));
        Assert.Equal("Entferntes Glossar-Echo: \"a\", \"b\"", ResultReport.TechnicalLines(D("a", echo: new[] { "a", "b" })));
    }

    [Fact]
    public void PromptBody_and_ReadAloudBody()
    {
        var p = new PromptResult(T0, "mach kürzer", "  ", "kurz", "m", "Gemini", true, 2, "Kürzen");
        var lines = ResultReport.PromptBody(p, false).Split('\n');
        Assert.Equal(new[] { "Instruction:", "mach kürzer", "", "Answer:", "kurz", "" }, lines[..6]);
        Assert.Equal(new[] { "Mode: Prompt", "Quick instruction: Kürzen", "Model: m", "Provider: Gemini", "Screenshot: yes", "Earlier turns sent: 2", "Offline Mode: off" }, lines[7..]);
        Assert.Contains("Selected text (excerpt):\nauswahl\n\nAnswer:", ResultReport.PromptBody(p with { SelectedText = "auswahl" }, true));
        var r = ResultReport.ReadAloudBody(new ReadAloudResult(T0, "text", "tts", "Kore")).Split('\n');
        Assert.Equal(new[] { "Text read aloud:", "text", "" }, r[..3]);
        Assert.Equal(new[] { "Mode: Read Aloud", "Voice model: tts", "Voice: Kore" }, r[4..]);
    }

    [Fact]
    public void ApplyingExtraDetails_and_InitialSelection()
    {
        var list = ResultReport.Candidates(new[] { D("x") }, NoTerms, false, null, null, null, T0);
        var applied = ResultReport.ApplyingExtraDetails("  Chat  ", list);
        Assert.Equal("Chat\n\nOffline-Modus: aus", applied[^1].Details);
        Assert.Equal(list[0], applied[0]);
        Assert.Same(list, ResultReport.ApplyingExtraDetails(" ", list));
        Assert.Equal(list[0].Id, ResultReport.InitialSelection(list, false));
        Assert.Equal("general", ResultReport.InitialSelection(list, true));
        Assert.Equal("", ResultReport.InitialSelection(Array.Empty<ResultCandidate>(), true));
    }

    [Fact]
    public void Labels_and_menu_title()
    {
        var list = ResultReport.Candidates(new[] { D(new string('w', 100), template: "Brief", mode: "Template") }, NoTerms, false, null, null, null, T0);
        Assert.Equal("Brief · 10:00 · " + new string('w', 40) + "…", ResultReport.MenuTitle(list[0]));
        Assert.Equal("Allgemeines Feedback (kein bestimmtes Ergebnis)", ResultReport.MenuTitle(list[1]));
        Assert.Equal("Diktat", ResultReport.KindLabel(ResultKind.Dictation));
        Assert.Equal("Deine Nachricht", ResultReport.MessageLabel(ResultKind.General));
        Assert.Equal("Was ist schiefgelaufen?", ResultReport.MessageLabel(ResultKind.Prompt));
        Assert.Contains("Ein Wort wurde falsch ausgesprochen", ResultReport.Placeholder(ResultKind.ReadAloud));
        Assert.StartsWith("Enthält nur deine Nachricht", ResultReport.PrivacyNote(ResultKind.General, false));
        Assert.StartsWith("Enthält den Text dieses Ergebnisses", ResultReport.PrivacyNote(ResultKind.Prompt, true));
        Assert.StartsWith("Enthält den vorgelesenen Text", ResultReport.PrivacyNote(ResultKind.ReadAloud, false));
    }

    [Fact]
    public void Context_cases()
    {
        Assert.Equal("details", ResultReport.Context(ResultKind.Dictation, "  ", " details "));
        Assert.Equal("Was ist schiefgelaufen: kaputt\n\ndetails", ResultReport.Context(ResultKind.Dictation, " kaputt ", "details"));
        Assert.Equal("Nachricht: idee\n\nOffline-Modus: aus", ResultReport.Context(ResultKind.General, "idee", "Offline-Modus: aus"));
    }

    [Fact]
    public void FileText_drops_blank_parts()
    {
        Assert.Equal("body\n\ntech\n\nconfig", ResultReport.FileText("body", " tech ", "config\n"));
        Assert.Equal("body\n\nconfig", ResultReport.FileText("body", "", "config"));
        Assert.Equal("body", ResultReport.FileText("body", "  ", null));
        Assert.Equal("Bericht als Datei: r.txt\n\nctx", ResultReport.WithFileNote("ctx", "r.txt"));
    }

    [Fact]
    public void MailtoUrlWithin_keeps_the_link_short_and_the_start_of_the_text()
    {
        var context = ResultReport.WithFileNote(string.Concat(Enumerable.Repeat("Größe über Ärztin\n", 400)), "r.txt");
        var url = FeedbackLinks.MailtoUrlWithin(context, "1.0", "10.0").OriginalString;
        Assert.True(url.Length <= FeedbackLinks.MaxMailtoLength, $"{url.Length} chars");
        var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=") + 6)..]);
        Assert.Contains("Bericht als Datei: r.txt", body); // the line that must arrive comes first
        Assert.Contains("…", body);
        Assert.EndsWith("App-Version: 1.0\nWindows: 10.0", body);
        // A short text is not touched.
        Assert.Equal(FeedbackLinks.MailtoUrl("kurz", "1", "2"), FeedbackLinks.MailtoUrlWithin("kurz", "1", "2"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", "\"C:\\Program Files\\Microsoft Office\\root\\Office16\\OUTLOOK.EXE\" /c ipm.note", true)]
    [InlineData(@"C:\Program Files\Mozilla Thunderbird\thunderbird.exe", @"C:\Program Files\Mozilla Thunderbird\thunderbird.exe -mail", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "\"C:\\Program Files\\Microsoft Office\\root\\Office16\\OUTLOOK.EXE\"", false)]
    [InlineData(null, "\"C:\\x\\OUTLOOK.EXE\"", false)]
    [InlineData(@"C:\x\OUTLOOK.EXE", "", false)]
    public void PrefersMapi_only_when_the_mail_handler_is_the_MAPI_client(string? mailto, string command, bool expected) =>
        Assert.Equal(expected, MailClientChoice.PrefersMapi(mailto, MailClientChoice.ExecutableOf(command)));

    [Fact]
    public void MachineMatches_refuses_a_32_bit_client_in_the_64_bit_app()
    {
        Assert.True(MailClientChoice.MachineMatches(0x8664, System.Runtime.InteropServices.Architecture.X64));
        Assert.False(MailClientChoice.MachineMatches(0x14C, System.Runtime.InteropServices.Architecture.X64));
        Assert.True(MailClientChoice.MachineMatches(0xAA64, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.False(MailClientChoice.MachineMatches(0x8664, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(MailClientChoice.PeMachine(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")));
        // kernel32.dll in this process's system folder is a native image of this process's machine (the test host exe
        // is not reliable: CI may run it through another host). Windows only: the Linux CI job has no kernel32.dll.
        if (!OperatingSystem.IsWindows()) return;
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var machine = MailClientChoice.PeMachine(kernel32);
        Assert.True(machine.HasValue, $"no PE header read from {kernel32}");
        Assert.True(MailClientChoice.MachineMatches(machine!.Value, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture),
            $"0x{machine:X4} vs {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    [Fact]
    public void McpProblemReport_message_stops_at_the_environment_block_not_at_a_rule_in_the_text()
    {
        var text = "# Titel\n\n## Was passiert\n\nerst\n---\ndann\n\n---\n\n- Version: 1\n\nFooter";
        Assert.Equal("# Titel\n\n## Was passiert\n\nerst\n---\ndann", WhisperShortcut.Core.Mcp.McpProblemReport.MessageFor(text));
    }

    private sealed class Plain : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string? Unprotect(string protectedValue) => protectedValue;
    }

    private static Settings Fresh() =>
        Settings.Load(Path.Combine(Path.GetTempPath(), "ws-report-fp-" + Guid.NewGuid().ToString("N") + ".json"), new Plain());

    [Fact]
    public void Fingerprint_is_stable_and_changes_with_a_template()
    {
        var a = Fresh();
        var b = Fresh();
        Assert.Equal(ReportConfigFingerprint.Current(a), ReportConfigFingerprint.Current(b));
        Assert.Matches("^[0-9a-f]{12}$", ReportConfigFingerprint.Current(a));
        b.Templates = new List<DictationTemplate> { new() { Name = "T", Instructions = "i" } };
        Assert.NotEqual(ReportConfigFingerprint.Current(a), ReportConfigFingerprint.Current(b));
        Assert.Equal(12, ReportConfigFingerprint.ShortHash("x").Length);
    }

    [Fact]
    public void Keys_never_reach_the_report_file_or_the_mail_body()
    {
        const string gemini = "AIzaSyD-1234567890abcdefghijklmnopqrstu";
        const string openAi = "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789";
        var context = ResultReport.Context(ResultKind.General, $"mein Schlüssel {gemini}", $"Details {openAi}");
        var body = FeedbackLinks.MessageBody(context, "1.0", "10.0");
        var file = ResultReport.FileText(body, $"Bearer {openAi}", $"## Log\nx-goog-api-key {gemini}\nprompt: {gemini}");
        foreach (var text in new[] { body, file, FeedbackLinks.MailtoUrl(context, "1.0", "10.0").OriginalString })
        {
            Assert.DoesNotContain(gemini, text);
            Assert.DoesNotContain(openAi, text);
            Assert.DoesNotContain(Uri.EscapeDataString(gemini), text);
        }
        // The rest of the report is still there.
        Assert.Contains("mein Schlüssel AIza…", file);
        Assert.Contains("Details sk-…", file);
    }
}
