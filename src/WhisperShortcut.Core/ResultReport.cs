using System.Globalization;

namespace WhisperShortcut.Core;

/// <summary>What a report is about.</summary>
public enum ResultKind { Dictation, Prompt, ReadAloud, General }

/// <summary>One row of the report window: a result that can be reported, with its editable details.</summary>
/// <param name="Technical">Glossary-echo lines. They go in the report file, not in the editable details.</param>
/// <param name="MenuLabel">Replaces the kind name in the list: a template's own name.</param>
public sealed record ResultCandidate(string Id, ResultKind Kind, DateTime Ts, string Preview, string Details,
    string Technical, string? MenuLabel);

/// <summary>
/// Report texts for recent dictations, the last Dictate Prompt answer and the last Read Aloud
/// (Mac ResultReport and DictationReport). App version and Windows are appended by <see cref="FeedbackLinks.MessageBody"/>.
/// </summary>
public static class ResultReport
{
    private const string GeneralId = "general";

    /// <summary>Newest first: one candidate per dictation (at most 10), the prompt, the read aloud; the general candidate last.</summary>
    public static IReadOnlyList<ResultCandidate> Candidates(IReadOnlyList<DictationResult> dictations,
        IReadOnlyList<string> glossaryTerms, bool offlineMode, PromptResult? prompt, ReadAloudResult? readAloud,
        string? currentConfigHash, DateTime now)
    {
        var list = new List<ResultCandidate>();
        for (var i = 0; i < Math.Min(10, dictations.Count); i++)
        {
            var e = dictations[i];
            list.Add(new ResultCandidate(
                CandidateId(ResultKind.Dictation, e.Ts, i.ToString(CultureInfo.InvariantCulture)),
                ResultKind.Dictation, e.Ts, Preview(e.Text),
                DictationBody(e, glossaryTerms, offlineMode, currentConfigHash),
                TechnicalLines(e),
                e.Mode == "Template" ? e.TemplateName : null));
        }
        if (prompt is not null)
            list.Add(new ResultCandidate(CandidateId(ResultKind.Prompt, prompt.Ts, ""), ResultKind.Prompt, prompt.Ts,
                Preview(prompt.Instruction), PromptBody(prompt, offlineMode), "", null));
        if (readAloud is not null)
            list.Add(new ResultCandidate(CandidateId(ResultKind.ReadAloud, readAloud.Ts, ""), ResultKind.ReadAloud, readAloud.Ts,
                Preview(readAloud.Text), ReadAloudBody(readAloud), "", null));

        var sorted = list.OrderByDescending(c => c.Ts).ToList();
        sorted.Add(new ResultCandidate(GeneralId, ResultKind.General, now, "",
            offlineMode ? "Offline-Modus: an" : "Offline-Modus: aus", "", null));
        return sorted;
    }

    /// <summary>The editable details of a dictation.</summary>
    public static string DictationBody(DictationResult e, IReadOnlyList<string> glossaryTerms, bool offlineMode,
        string? currentConfigHash)
    {
        var lines = new List<string>
        {
            "Eingefügter Text:", e.Text, "",
            "Diktiert: " + Stamp(e.Ts),
            "Modus: " + e.Mode,
        };
        if (!string.IsNullOrEmpty(e.Model)) lines.Add("Transkriptionsmodell: " + e.Model);
        lines.Add("Glossar: " + GlossarySummary(glossaryTerms, e.Text));
        lines.Add(offlineMode ? "Offline-Modus: an" : "Offline-Modus: aus");
        if (!string.IsNullOrEmpty(e.TemplateName)) lines.Add("Vorlage: " + e.TemplateName);
        if (e.AudioSeconds is { } seconds)
            lines.Add("Audiolänge: " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        lines.Add("App-Version beim Diktat: " + e.AppVersion);
        if (!string.IsNullOrEmpty(e.ConfigHash) && !string.IsNullOrEmpty(currentConfigHash) && e.ConfigHash != currentConfigHash)
            lines.Add("Hinweis: Einstellungen wurden nach diesem Diktat geändert");
        if (e.RawOutput is { } raw && raw != e.Text)
        {
            lines.Add("Modellausgabe vor der Bereinigung:");
            lines.Add(raw);
        }
        return string.Join("\n", lines);
    }

    /// <summary>Glossary-echo line; it goes in the report file, not in the editable details. "" when unknown.</summary>
    public static string TechnicalLines(DictationResult e)
    {
        if (e.GlossaryEchoRemoved is not { } removed) return "";
        return "Entferntes Glossar-Echo: " + (removed.Count == 0
            ? "keins"
            : string.Join(", ", removed.Select(t => "\"" + t + "\"")));
    }

    /// <summary>The details of the last Dictate Prompt answer (English, like the Mac).</summary>
    public static string PromptBody(PromptResult r, bool offlineMode)
    {
        var lines = new List<string> { "Instruction:", r.Instruction, "" };
        if (!string.IsNullOrWhiteSpace(r.SelectedText))
        {
            lines.Add("Selected text (excerpt):");
            lines.Add(r.SelectedText);
            lines.Add("");
        }
        lines.Add("Answer:");
        lines.Add(r.Response);
        lines.Add("");
        lines.Add("Answered: " + Stamp(r.Ts));
        lines.Add("Mode: Prompt");
        if (!string.IsNullOrEmpty(r.QuickInstruction)) lines.Add("Quick instruction: " + r.QuickInstruction);
        if (r.Model is not null) lines.Add("Model: " + r.Model);
        if (r.Provider is not null) lines.Add("Provider: " + r.Provider);
        lines.Add("Screenshot: " + (r.HadScreenshot ? "yes" : "no"));
        lines.Add("Earlier turns sent: " + r.HistoryTurns.ToString(CultureInfo.InvariantCulture));
        lines.Add("Offline Mode: " + (offlineMode ? "on" : "off"));
        return string.Join("\n", lines);
    }

    /// <summary>The details of the last Read Aloud (English, like the Mac).</summary>
    public static string ReadAloudBody(ReadAloudResult r)
    {
        var lines = new List<string>
        {
            "Text read aloud:", r.Text, "",
            "Read: " + Stamp(r.Ts),
            "Mode: Read Aloud",
        };
        if (r.Model is not null) lines.Add("Voice model: " + r.Model);
        if (r.Voice is not null) lines.Add("Voice: " + r.Voice);
        return string.Join("\n", lines);
    }

    /// <summary>Puts <paramref name="extra"/> above the general candidate's details, a blank line between. Blank changes nothing.</summary>
    public static IReadOnlyList<ResultCandidate> ApplyingExtraDetails(string? extra, IReadOnlyList<ResultCandidate> list)
    {
        var e = extra?.Trim();
        if (string.IsNullOrEmpty(e)) return list;
        return list.Select(c =>
        {
            if (c.Id != GeneralId) return c;
            var baseText = c.Details.Trim();
            return c with { Details = baseText.Length == 0 ? e : e + "\n\n" + baseText };
        }).ToList();
    }

    /// <summary>The row the window selects: general when asked (and present), else the first (newest) row.</summary>
    public static string InitialSelection(IReadOnlyList<ResultCandidate> list, bool startOnGeneral)
    {
        if (startOnGeneral && list.Any(c => c.Id == GeneralId)) return GeneralId;
        return list.Count > 0 ? list[0].Id : "";
    }

    /// <summary>Name of a kind in the list.</summary>
    public static string KindLabel(ResultKind kind) => kind switch
    {
        ResultKind.Dictation => "Diktat",
        ResultKind.Prompt => "Prompt-Antwort",
        ResultKind.ReadAloud => "Vorlesen",
        _ => "Allgemeines Feedback (kein bestimmtes Ergebnis)",
    };

    /// <summary>The list entry: kind (or template name), time and a preview.</summary>
    public static string MenuTitle(ResultCandidate c)
    {
        if (c.Kind == ResultKind.General) return KindLabel(ResultKind.General);
        var time = LocalTime(c.Ts).ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"{c.MenuLabel ?? KindLabel(c.Kind)} · {time} · {FeedbackLinks.Truncated(c.Preview, 48)}";
    }

    /// <summary>The report text from the user's message and the (edited) details.</summary>
    public static string Context(ResultKind kind, string message, string details)
    {
        var note = message.Trim();
        var body = details.Trim();
        if (note.Length == 0) return body;
        return (kind == ResultKind.General ? "Nachricht: " : "Was ist schiefgelaufen: ") + note + "\n\n" + body;
    }

    /// <summary>The report file: the message body, technical lines, configuration; blank parts dropped.</summary>
    public static string FileText(string messageBody, string technical, string? configuration)
    {
        var parts = new[] { messageBody, technical, configuration ?? "" }
            .Select(p => p.Trim())
            .Where(p => p.Length > 0);
        return string.Join("\n\n", parts);
    }

    /// <summary>The context with a pointer to the attached configuration file.</summary>
    public static string WithFileNote(string context, string fileName) =>
        context + "\n\nKonfiguration als Datei: " + fileName;

    /// <summary>Label above the message box.</summary>
    public static string MessageLabel(ResultKind kind) =>
        kind == ResultKind.General ? "Deine Nachricht" : "Was ist schiefgelaufen?";

    /// <summary>Placeholder of the message box.</summary>
    public static string Placeholder(ResultKind kind) => kind switch
    {
        ResultKind.Dictation => "Zum Beispiel: Am Ende stand ein Wort, das ich nicht gesagt habe",
        ResultKind.Prompt => "Zum Beispiel: Die Antwort hat meine Anweisung ignoriert",
        ResultKind.ReadAloud => "Zum Beispiel: Ein Wort wurde falsch ausgesprochen",
        _ => "Zum Beispiel: eine Idee, eine Frage oder was nicht funktioniert hat",
    };

    /// <summary>What the report contains, for the line under the buttons.</summary>
    public static string PrivacyNote(ResultKind kind, bool includeConfiguration)
    {
        if (includeConfiguration)
        {
            return kind switch
            {
                ResultKind.General => "Enthält deine Nachricht, deine Konfiguration samt Glossar und die letzten Zeilen des Protokolls.",
                ResultKind.Dictation => "Enthält deinen diktierten Text, deine Konfiguration samt Glossar und die letzten Zeilen des Protokolls. Namen im Text kannst du unter „Details“ entfernen.",
                _ => "Enthält den Text dieses Ergebnisses, deine Konfiguration samt Glossar und die letzten Zeilen des Protokolls. Namen im Text kannst du unter „Details“ entfernen.",
            };
        }
        return kind switch
        {
            ResultKind.General => "Enthält nur deine Nachricht, die App-Version und die Windows-Version.",
            ResultKind.Dictation => "Enthält deinen diktierten Text. Entferne Namen und alles andere Private, bevor du ihn sendest.",
            ResultKind.Prompt => "Enthält deine Anweisung, einen Auszug des markierten Texts und die Antwort. Entferne Namen und alles andere Private, bevor du sie sendest.",
            _ => "Enthält den vorgelesenen Text. Entferne Namen und alles andere Private, bevor du ihn sendest.",
        };
    }

    private static string GlossarySummary(IReadOnlyList<string> terms, string text)
    {
        if (terms.Count == 0) return "leer";
        var textWords = Words(text);
        var seen = new HashSet<string>();
        var present = new List<string>();
        foreach (var term in terms)
        {
            var termWords = Words(term);
            if (termWords.Count == 0 || !seen.Add(string.Join(" ", termWords)) || !ContainsSequence(textWords, termWords)) continue;
            present.Add(term);
        }
        return present.Count == 0
            ? $"{terms.Count} Begriffe, keiner davon in diesem Text."
            : $"{terms.Count} Begriffe, in diesem Text: {string.Join(", ", present)}";
    }

    private static List<string> Words(string s)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) current.Append(char.ToLowerInvariant(ch));
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    private static bool ContainsSequence(List<string> haystack, List<string> needle)
    {
        for (var start = 0; start + needle.Count <= haystack.Count; start++)
        {
            var ok = true;
            for (var i = 0; i < needle.Count && ok; i++) ok = haystack[start + i] == needle[i];
            if (ok) return true;
        }
        return false;
    }

    private static string Preview(string text)
    {
        var line = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        const int limit = 40;
        if (line.Length <= limit) return line;
        var cut = limit;
        if (char.IsLowSurrogate(line[cut]) && char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut] + "…";
    }

    private static DateTime LocalTime(DateTime ts) => ts.Kind == DateTimeKind.Utc ? ts.ToLocalTime() : ts;

    /// <summary>"yyyy-MM-dd HH:mm:ss +0100" in local time.</summary>
    private static string Stamp(DateTime ts)
    {
        var local = LocalTime(ts);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + $" {sign}{abs.Hours:00}{abs.Minutes:00}";
    }

    private static string CandidateId(ResultKind kind, DateTime ts, string extra)
    {
        var stamp = ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var name = kind switch
        {
            ResultKind.Dictation => "dictation",
            ResultKind.Prompt => "prompt",
            ResultKind.ReadAloud => "readAloud",
            _ => "general",
        };
        return extra.Length == 0 ? $"{stamp}|{name}" : $"{stamp}|{name}|{extra}";
    }
}
