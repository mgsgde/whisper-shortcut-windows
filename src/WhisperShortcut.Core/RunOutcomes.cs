namespace WhisperShortcut.Core;

/// <summary>What the user is told when a run ends: a done pill, the "words nobody dictated" card, a toast, or nothing.</summary>
public enum PresentationKind { Done, Warning, Toast, Silent }

/// <summary>Toast icon, kept free of WinForms so the decision tables stay testable.</summary>
public enum Severity { Info, Warning, Error }

/// <summary>What a toast offers to do on click.</summary>
public enum FollowUp
{
    None,
    /// <summary>Run the kept recording again.</summary>
    Retry,
    /// <summary>Open the settings window.</summary>
    OpenSettings,
    /// <summary>Open the settings window on the accounts page (no key / invalid key).</summary>
    OpenAccounts,
    /// <summary>Open <see cref="Presentation.Url"/>, the provider's billing page.</summary>
    TopUp,
}

/// <summary>Where a failure is shown; the texts differ on purpose (a toast with a title, a read-aloud toast, a chat status line).</summary>
public enum FailureContext { Dictation, Live, ReadAloud, Chat }

/// <summary>
/// One ending of a run, decided by <see cref="RunOutcomes"/> and shown by whoever owns the UI.
/// <see cref="Pasted"/> belongs to <see cref="PresentationKind.Warning"/> (the card says "eingefügt" or not);
/// <see cref="KeepAudio"/> says the recording stays for "Letzte Aufnahme erneut verarbeiten" even when no retry is offered.
/// </summary>
public sealed record Presentation(PresentationKind Kind, string Title, string Text, Severity Icon = Severity.Info,
    bool ErrorSound = false, FollowUp Action = FollowUp.None, string? Url = null, bool Pasted = false, bool KeepAudio = false)
{
    public static readonly Presentation Silent = new(PresentationKind.Silent, "", "");

    /// <summary>The green "done" pill with <paramref name="text"/>.</summary>
    public static Presentation DonePill(string text) => new(PresentationKind.Done, "", text);

    /// <summary>A toast without sound. <paramref name="keepAudio"/> with <see cref="FollowUp.Retry"/>: the run is kept for the retry.</summary>
    public static Presentation Notice(string title, string text, Severity icon, FollowUp action = FollowUp.None, bool keepAudio = false) =>
        new(PresentationKind.Toast, title, text, icon, Action: action, KeepAudio: keepAudio);
}

/// <summary>
/// The decision tables of a run's end: what each <see cref="PasteOutcome"/> and each failure tells the user, with which
/// icon, sound and follow-up. Pure; the German texts are the ones the app has always shown.
/// </summary>
public static class RunOutcomes
{
    private const string PasteYourself = "Bitte selbst einfügen";
    private const string Unexpected = "Etwas ist schiefgelaufen. Details stehen im Protokoll (Rechtsklick auf das Symbol → Protokoll öffnen).";

    /// <summary>The ending of a finished paste. Words nobody dictated turn a clean ending into the warning card.</summary>
    public static Presentation ForPaste(PasteOutcome outcome, bool hasUnsupported) => outcome switch
    {
        PasteOutcome.ClipboardOnlyByChoice when hasUnsupported => Card(pasted: false),
        PasteOutcome.ClipboardOnlyByChoice => new(PresentationKind.Done, "", "In der Zwischenablage"),
        PasteOutcome.Pasted when hasUnsupported => Card(pasted: true),
        PasteOutcome.Pasted => new(PresentationKind.Done, "", "Eingefügt"),
        // The balloon would bury the warning; the card says both.
        PasteOutcome.Failed => new(PresentationKind.Toast, "Einfügen fehlgeschlagen", "Die Zwischenablage war blockiert. Bitte noch einmal versuchen.",
            Severity.Warning, ErrorSound: true),
        _ when hasUnsupported => Card(pasted: false),
        PasteOutcome.ClipboardOnlyElevatedTarget => Hint(PasteYourself,
            "Das Zielprogramm läuft als Administrator, dorthin darf WhisperShortcut nicht tippen. Der Text liegt in der Zwischenablage: Strg+V drücken."),
        PasteOutcome.ClipboardOnlyKeysHeld => Hint(PasteYourself,
            "Strg, Umschalt oder Alt war noch gedrückt. Der Text liegt in der Zwischenablage: Strg+V drücken."),
        PasteOutcome.ClipboardOnlyTargetChanged => Hint(PasteYourself,
            "Du hast das Fenster gewechselt. Der Text liegt in der Zwischenablage: Strg+V drücken."),
        PasteOutcome.ClipboardOnlyTargetGone => Hint(PasteYourself,
            "Das Fenster, in dem die Aufnahme begann, ist geschlossen. Der Text liegt in der Zwischenablage: Strg+V drücken."),
        PasteOutcome.ClipboardOnlyClipboardChanged => Hint("Nicht eingefügt",
            "Die Zwischenablage hat sich kurz vor dem Einfügen geändert. Den Text gibt es im Menü am Symbol unter „Letzte Transkriptionen“."),
        PasteOutcome.ClipboardOnlyOwnWindow => Hint(PasteYourself,
            "Vorn war ein Fenster von WhisperShortcut. Der Text liegt in der Zwischenablage: Strg+V drücken."),
        _ => Presentation.Silent,
    };

    private static Presentation Card(bool pasted) => new(PresentationKind.Warning, "", "", Pasted: pasted);
    private static Presentation Hint(string title, string text) => new(PresentationKind.Toast, title, text, Severity.Info);

    /// <summary>
    /// What a failed run tells the user. <paramref name="audioKept"/>: the recording is still there, so a retry is offered
    /// where a second try can help. Not for cancellation (that is no failure). <see cref="FailureContext.ReadAloud"/> and
    /// <see cref="FailureContext.Chat"/> have no recording to retry and show their own texts.
    /// </summary>
    public static Presentation ForFailure(Exception e, bool audioKept, FailureContext context = FailureContext.Dictation) => context switch
    {
        FailureContext.ReadAloud => ForReadAloud(e),
        FailureContext.Chat => ForChat(e),
        _ => ForRun(e, audioKept, context),
    };

    /// <summary>The log line of a failure, without the run's label (null: not logged); <paramref name="withException"/> when the exception is logged too.</summary>
    public static string? LogLine(Exception e, out bool withException)
    {
        withException = false;
        switch (e)
        {
            case SelectionUnreadableException: return null;
            case OfflineNotReadyException: return "offline model missing";
            case LocalLlmException: withException = true; return "local LLM failed";
            case EmptyModelReplyException: return "empty model reply";
            case GeminiException g: return $"Gemini {g.Kind}";
            case AudioSilentException or MicrophoneUnavailableException: return $"microphone problem {e.GetType().Name}";
            default: withException = true; return "unexpected error";
        }
    }

    private static Presentation ForRun(Exception e, bool audioKept, FailureContext context)
    {
        Presentation Toast(string title, string text, Severity icon, FollowUp action = FollowUp.None, string? url = null, bool keep = false) =>
            new(PresentationKind.Toast, title, text, icon, ErrorSound: true, action, url, KeepAudio: keep);
        // A retry needs the audio; when it is lost the toast is a plain error.
        Presentation Retryable(string title, string text) =>
            Toast(title, text, Severity.Error, audioKept ? FollowUp.Retry : FollowUp.None, keep: audioKept);

        switch (e)
        {
            case SelectionUnreadableException when context == FailureContext.Dictation:
                // Writing a new text now would overwrite the marked one.
                return Toast("Nichts geändert", "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", Severity.Warning);
            case OfflineNotReadyException when context == FailureContext.Dictation:
                return Toast(UserCopy.ModelNotDownloadedTitle, e.Message, Severity.Warning, FollowUp.OpenSettings);
            case LocalLlmException when context == FailureContext.Dictation:
                return Retryable(UserCopy.OfflineModelErrorTitle, e.Message);
            case EmptyModelReplyException when context == FailureContext.Dictation:
                return Retryable(UserCopy.NoReplyTitle, e.Message);
            case GeminiException g when context == FailureContext.Dictation:
            {
                var title = UserCopy.ProviderErrorTitle(g.Kind, g.Provider, g.Message);
                switch (g.Kind)
                {
                    case GeminiErrorKind.Billing:
                        // Retrying cannot fill an empty account, but the audio stays for "Letzte Aufnahme erneut verarbeiten" after topping up.
                        return g.BillingUrl is { } url
                            ? Toast(title, g.Message, Severity.Error, FollowUp.TopUp, url, keep: true)
                            : Toast(title, g.Message, Severity.Error, FollowUp.OpenSettings, keep: true);
                    case GeminiErrorKind.NoKey or GeminiErrorKind.InvalidKey:
                        return Toast(title, g.Message, Severity.Error, FollowUp.OpenAccounts);
                    case GeminiErrorKind.Network or GeminiErrorKind.Timeout or GeminiErrorKind.Quota or GeminiErrorKind.Server:
                        return Retryable(title, g.Message);
                    default:
                        return Toast(title, g.Message, Severity.Error);
                }
            }
            case AudioSilentException or MicrophoneUnavailableException:
                return Toast("Problem mit dem Mikrofon", e.Message, Severity.Warning);
            default:
                // The live path has always titled this with the Mac's "❌ Fehler"; dictation and prompt runs with "Fehler".
                return Retryable(context == FailureContext.Live ? UserCopy.ErrorTitle : "Fehler", Unexpected);
        }
    }

    private static Presentation ForReadAloud(Exception e)
    {
        const string Title = "Vorlesen fehlgeschlagen";
        return e switch
        {
            SelectionUnreadableException => new(PresentationKind.Toast, "Vorlesen",
                "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", Severity.Warning, ErrorSound: true),
            // Only a missing or rejected key opens the settings; every other provider error is read, not acted on.
            GeminiException g => new(PresentationKind.Toast, Title, g.Message, Severity.Error, ErrorSound: true,
                g.Kind is GeminiErrorKind.NoKey or GeminiErrorKind.InvalidKey ? FollowUp.OpenAccounts : FollowUp.None),
            _ => new(PresentationKind.Toast, Title, "Der Text konnte nicht vorgelesen werden. Details stehen im Protokoll.",
                Severity.Error, ErrorSound: true),
        };
    }

    private static Presentation ForChat(Exception e) => e switch
    {
        GeminiException g => new(PresentationKind.Toast, "", g.Message, Severity.Error),
        OfflineNotReadyException or LocalLlmException => new(PresentationKind.Toast, "",
            e.Message + " Unter Einstellungen → Dictate Prompt lässt es sich laden.", Severity.Error),
        _ => new(PresentationKind.Toast, "", "Die Antwort konnte nicht geladen werden. Details stehen im Protokoll.", Severity.Error),
    };
}
