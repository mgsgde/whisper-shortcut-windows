namespace WhisperShortcut.Core;

/// <summary>
/// German user-facing sentences shared by the tray, the overlay, the settings and the error balloons.
/// Keep wording here so screen readers, toasts and the settings stay consistent.
/// </summary>
public static class UserCopy
{
    /// <summary>Blue box on the first settings page, and again from Über → Einführung.</summary>
    public static string Welcome(string dictateHotkeyDisplay) =>
        "Willkommen – so geht’s:\n"
        + "1. Unter „Konten“ einen Schlüssel von Google (Gemini) oder OpenAI eintragen, oder unter „Datenschutz & "
        + "Berechtigungen“ offline arbeiten; die Modelle dafür werden unter „Diktieren“ einmal geladen.\n"
        + $"2. In einem beliebigen Programm {dictateHotkeyDisplay} drücken, sprechen, noch einmal drücken.\n\n"
        + "WhisperShortcut bleibt danach als Symbol unten rechts im Infobereich (oft hinter dem Pfeil ^). "
        + "Ein Klick auf das X beendet die App nicht. Mit Strg+Tab lassen sich die Seiten wechseln.";

    public const string WelcomeAccessibleName = "Einführung";

    public const string WelcomeTourHint =
        "Die Schritte vom ersten Start: Schlüssel eintragen oder offline arbeiten, Kürzel drücken. Erklärt auch, wo das Symbol zu finden ist.";

    public const string MicTestHint =
        "„Testen“ zeigt den Pegel beim Sprechen. Es wird nichts abgespielt.";

    public const string MicAccessibleName = "Mikrofon";
    public const string MicTestAccessibleName = "Mikrofon testen";
    public const string MicTestStopAccessibleName = "Mikrofontest beenden";

    public const string SettingsAccessibleDescription =
        "Mit Strg+Tab die Seiten wechseln. Jede Änderung gilt sofort.";

    public const string OverlayAccessibleName = "WhisperShortcut-Anzeige";

    public const string Transcribing = "Sprache wird erkannt …";
    public const string RecognizingInstruction = "Anweisung wird erkannt …";
    public const string EditingText = "Text wird bearbeitet …";
    public const string LoadingLlm = "Sprachmodell wird geladen …";
    public const string Processing = "Wird verarbeitet …";

    public static string RecordingStatus(string stopKey) => $"Aufnahme läuft – {stopKey} beendet sie";

    public const string OverlayDictateTitle = "Diktat läuft";
    public const string LiveTitle = "Live diktieren";
    public const string LiveFinishing = "Rest wird eingefügt …";
    public const string LiveInserted = "Am Cursor eingefügt";
    public const string OverlayPromptTitle = "Anweisung sprechen";
    public static string OverlayTemplateTitle(string templateName) => $"Vorlage „{templateName}“";
    public static string OverlayStopHint(string stopKey) => $"{stopKey} beendet · Esc bricht ab";
    public const string OverlayCancelHint = "Kürzel oder Esc bricht ab";

    public static string QuickActionBusy(string action) => action.Trim().Length == 0 ? Processing : action.Trim() + " …";
    public static string TemplateBusy(string templateName) => $"Vorlage „{templateName}“ …";

    /// <summary>Status bar / screen reader text for the recording pill.</summary>
    public static string OverlayAnnouncement(string title, string hint, string? context)
    {
        var parts = new[] { title, context, hint }.Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(". ", parts);
    }

    /// <summary>Next page index, wrapping at both ends. <paramref name="step"/> is +1 or −1.</summary>
    public static int NextPage(int current, int count, int step)
    {
        if (count <= 0) return 0;
        return ((current + step) % count + count) % count;
    }

    // ---- Popups (Mac PopupNotificationWindow + SpeechErrorFormatter, German from Localizable.xcstrings, du-form) ----

    /// <summary>Action button: run the failed recording again (Mac "Retry").</summary>
    public const string ActionRetry = "Erneut versuchen";

    /// <summary>Action button: open the provider's billing page (Mac "Top up").</summary>
    public const string ActionTopUp = "Aufladen";

    /// <summary>Action button: open the settings window (Mac "Open Settings").</summary>
    public const string ActionOpenSettings = "Einstellungen öffnen";

    /// <summary>Action button for a finished or interrupted meeting.</summary>
    public const string ActionOpenFolder = "Ordner öffnen";

    /// <summary>Mac "Didn't catch that".</summary>
    public const string NoSpeechTitle = "Nichts verstanden";

    /// <summary>Mac "Nothing to transcribe. Check the microphone and try again.", du-form.</summary>
    public const string NoSpeechText = "Nichts zu transkribieren. Prüf das Mikrofon und versuch es noch einmal.";

    /// <summary>Mac "📝 Nothing Selected".</summary>
    public const string NothingSelectedTitle = "📝 Nichts markiert";

    /// <summary>Mac noSelectedText, du-form.</summary>
    public const string NothingSelectedText =
        "Dictate Prompt bearbeitet markierten Text, und es war nichts markiert. Markiere zuerst den Text, den du ändern "
        + "möchtest, drück dann das Kürzel und sag, was damit geschehen soll.";

    /// <summary>Mac "⚠️ No Reply".</summary>
    public const string NoReplyTitle = "⚠️ Keine Antwort";

    /// <summary>Mac "⚠️ Offline Model Error".</summary>
    public const string OfflineModelErrorTitle = "⚠️ Fehler beim Offline-Modell";

    /// <summary>Mac "📥 Model Not Downloaded".</summary>
    public const string ModelNotDownloadedTitle = "📥 Modell nicht geladen";

    /// <summary>Mac "❌ Error".</summary>
    public const string ErrorTitle = "❌ Fehler";

    /// <summary>
    /// Popup title for a failed cloud call, Mac SpeechErrorFormatter.shortStatus where Windows has the same error.
    /// <paramref name="message"/> tells the two server cases apart (model gone vs. service busy).
    /// </summary>
    public static string ProviderErrorTitle(GeminiErrorKind kind, string provider, string? message = null) => kind switch
    {
        GeminiErrorKind.NoKey => provider == "Gemini" ? "⚠️ Kein Google-API-Schlüssel" : $"⚠️ Kein {provider}-API-Schlüssel",
        GeminiErrorKind.InvalidKey => "❌ Ungültiger API-Schlüssel",
        GeminiErrorKind.Network => "⏰ Netzwerkfehler",
        GeminiErrorKind.Timeout => "⏰ Zeitüberschreitung",
        GeminiErrorKind.Quota => "⏳ Limit erreicht",
        GeminiErrorKind.Billing => "💳 Abrechnung erforderlich",
        GeminiErrorKind.Truncated => "⚠️ Antwort abgeschnitten",
        GeminiErrorKind.Server when message == ModelGone(provider) => "❌ Modell nicht mehr verfügbar",
        GeminiErrorKind.Server when message == ServerBusy(provider) => "🔄 Dienst nicht erreichbar",
        GeminiErrorKind.Server => "❌ Serverfehler",
        _ => ErrorTitle,
    };

    public static string ServerBusy(string provider) =>
        $"{provider} antwortet gerade nicht. Bitte später erneut versuchen.";

    public static string ModelGone(string provider) =>
        $"{provider} kennt dieses Modell nicht mehr. Bitte in den Einstellungen ein anderes wählen.";

    public const string UnsupportedCpu =
        "Der Prozessor dieses PCs kann das Offline-Sprachmodell nicht ausführen. Diktieren geht offline weiter; "
        + "für „Text bearbeiten“ bitte in den Einstellungen unter Anbieter Gemini oder OpenAI wählen.";

    /// <summary>
    /// Keep a German detail, drop .NET/Win32 noise. Always ends with a next step when the detail is unusable.
    /// </summary>
    public static string TechnicalFallback(string lead, string? detail)
    {
        lead = lead.Trim();
        if (UsableDetail(detail))
            return lead.EndsWith('.') || lead.EndsWith('?') || lead.EndsWith('!')
                ? lead + " " + detail!.Trim()
                : lead + ": " + detail!.Trim();
        var suffix = " Details stehen im Protokoll.";
        if (lead.EndsWith(suffix, StringComparison.Ordinal)) return lead;
        return lead.TrimEnd('.') + "." + suffix;
    }

    public static bool UsableDetail(string? detail) =>
        !string.IsNullOrWhiteSpace(detail) && !LooksTechnical(detail);

    /// <summary>True when a message looks like a stack, HRESULT or English runtime text, not a sentence for the user.</summary>
    public static bool LooksTechnical(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("HRESULT", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("System.", StringComparison.Ordinal)) return true;
        if (text.TrimStart().StartsWith("at ", StringComparison.Ordinal)) return true;
        if (text.Contains(" at ", StringComparison.Ordinal) && text.Contains('.')) return true;
        if (ContainsEnglishOsNoise(text)) return true;
        return ContainsHexCode(text);
    }

    private static bool ContainsEnglishOsNoise(string text)
    {
        ReadOnlySpan<string> noise =
        [
            "access is denied", "cannot access", "is not a valid", "the process cannot", "unable to",
        ];
        foreach (var n in noise)
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ContainsHexCode(string text)
    {
        for (int i = 0; i < text.Length - 9; i++)
        {
            if (text[i] != '0' || (text[i + 1] is not ('x' or 'X'))) continue;
            int hex = 0;
            for (int j = 2; j < 10 && i + j < text.Length; j++)
            {
                char c = text[i + j];
                if (c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f')) hex++;
                else break;
            }
            if (hex == 8) return true;
        }
        return false;
    }
}
