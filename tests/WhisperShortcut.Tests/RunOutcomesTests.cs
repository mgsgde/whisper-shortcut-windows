using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class RunOutcomesTests
{
    private const string Hint = "Der Text liegt in der Zwischenablage: Strg+V drücken.";

    [Theory]
    [InlineData(PasteOutcome.Pasted, "Eingefügt")]
    [InlineData(PasteOutcome.ClipboardOnlyByChoice, "In der Zwischenablage")]
    public void ForPaste_CleanEndings_AreDonePills(PasteOutcome outcome, string text)
    {
        var p = RunOutcomes.ForPaste(outcome, hasUnsupported: false);
        Assert.Equal(new Presentation(PresentationKind.Done, "", text), p);
    }

    [Theory]
    [InlineData(PasteOutcome.Pasted, true)]
    [InlineData(PasteOutcome.ClipboardOnlyByChoice, false)]
    [InlineData(PasteOutcome.ClipboardOnlyElevatedTarget, false)]
    [InlineData(PasteOutcome.ClipboardOnlyKeysHeld, false)]
    [InlineData(PasteOutcome.ClipboardOnlyTargetChanged, false)]
    [InlineData(PasteOutcome.ClipboardOnlyTargetGone, false)]
    [InlineData(PasteOutcome.ClipboardOnlyOwnWindow, false)]
    [InlineData(PasteOutcome.ClipboardOnlyClipboardChanged, false)]
    public void ForPaste_WithUnsupportedWords_IsTheWarningCard(PasteOutcome outcome, bool pasted)
    {
        var p = RunOutcomes.ForPaste(outcome, hasUnsupported: true);
        Assert.Equal(PresentationKind.Warning, p.Kind);
        Assert.Equal(pasted, p.Pasted);
        Assert.False(p.ErrorSound);
    }

    [Theory]
    [InlineData(PasteOutcome.ClipboardOnlyElevatedTarget, "Bitte selbst einfügen",
        "Das Zielprogramm läuft als Administrator, dorthin darf WhisperShortcut nicht tippen. " + Hint)]
    [InlineData(PasteOutcome.ClipboardOnlyKeysHeld, "Bitte selbst einfügen", "Strg, Umschalt oder Alt war noch gedrückt. " + Hint)]
    [InlineData(PasteOutcome.ClipboardOnlyTargetChanged, "Bitte selbst einfügen", "Du hast das Fenster gewechselt. " + Hint)]
    [InlineData(PasteOutcome.ClipboardOnlyTargetGone, "Bitte selbst einfügen",
        "Das Fenster, in dem die Aufnahme begann, ist geschlossen. " + Hint)]
    [InlineData(PasteOutcome.ClipboardOnlyOwnWindow, "Bitte selbst einfügen", "Vorn war ein Fenster von WhisperShortcut. " + Hint)]
    [InlineData(PasteOutcome.ClipboardOnlyClipboardChanged, "Nicht eingefügt",
        "Die Zwischenablage hat sich kurz vor dem Einfügen geändert. Den Text gibt es im Menü am Symbol unter „Letzte Transkriptionen“.")]
    public void ForPaste_ClipboardOnly_IsAnInfoToastWithoutSound(PasteOutcome outcome, string title, string text)
    {
        Assert.Equal(new Presentation(PresentationKind.Toast, title, text, Severity.Info), RunOutcomes.ForPaste(outcome, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForPaste_Failed_IsAWarningWithErrorSound_EvenWithUnsupportedWords(bool unsupported)
    {
        var p = RunOutcomes.ForPaste(PasteOutcome.Failed, unsupported);
        Assert.Equal(new Presentation(PresentationKind.Toast, "Einfügen fehlgeschlagen",
            "Die Zwischenablage war blockiert. Bitte noch einmal versuchen.", Severity.Warning, ErrorSound: true), p);
    }

    [Fact]
    public void ForPaste_CoversEveryOutcome()
    {
        foreach (var o in Enum.GetValues<PasteOutcome>())
            foreach (var u in new[] { false, true })
                Assert.NotEqual(PresentationKind.Silent, RunOutcomes.ForPaste(o, u).Kind);
    }

    [Theory]
    [InlineData(GeminiErrorKind.NoKey, "⚠️ Kein Google-API-Schlüssel", FollowUp.OpenAccounts)]
    [InlineData(GeminiErrorKind.InvalidKey, "❌ Ungültiger API-Schlüssel", FollowUp.OpenAccounts)]
    [InlineData(GeminiErrorKind.Network, "⏰ Netzwerkfehler", FollowUp.Retry)]
    [InlineData(GeminiErrorKind.Timeout, "⏰ Zeitüberschreitung", FollowUp.Retry)]
    [InlineData(GeminiErrorKind.Quota, "⏳ Limit erreicht", FollowUp.Retry)]
    [InlineData(GeminiErrorKind.Server, "❌ Serverfehler", FollowUp.Retry)]
    [InlineData(GeminiErrorKind.Truncated, "⚠️ Antwort abgeschnitten", FollowUp.None)]
    public void ForFailure_Gemini_ByKind(GeminiErrorKind kind, string title, FollowUp action)
    {
        var p = RunOutcomes.ForFailure(new GeminiException(kind, "Text"), audioKept: true);
        Assert.Equal(PresentationKind.Toast, p.Kind);
        Assert.Equal(title, p.Title);
        Assert.Equal("Text", p.Text);
        Assert.Equal(Severity.Error, p.Icon);
        Assert.True(p.ErrorSound);
        Assert.Equal(action, p.Action);
        Assert.Equal(action == FollowUp.Retry, p.KeepAudio);
    }

    [Theory]
    [InlineData(GeminiErrorKind.Network)]
    [InlineData(GeminiErrorKind.Timeout)]
    [InlineData(GeminiErrorKind.Quota)]
    [InlineData(GeminiErrorKind.Server)]
    public void ForFailure_RetryableKinds_OfferNothingWhenTheAudioIsLost(GeminiErrorKind kind)
    {
        var p = RunOutcomes.ForFailure(new GeminiException(kind, "Text"), audioKept: false);
        Assert.Equal(FollowUp.None, p.Action);
        Assert.False(p.KeepAudio);
    }

    [Fact]
    public void ForFailure_Billing_TopsUpAndKeepsTheAudioWithoutRetry()
    {
        var withUrl = RunOutcomes.ForFailure(new GeminiException("OpenAI", GeminiErrorKind.Billing, "Kein Guthaben", "https://x.test/b"), audioKept: true);
        Assert.Equal("💳 Abrechnung erforderlich", withUrl.Title);
        Assert.Equal(FollowUp.TopUp, withUrl.Action);
        Assert.Equal("https://x.test/b", withUrl.Url);
        Assert.True(withUrl.KeepAudio);
        Assert.Equal(Severity.Error, withUrl.Icon);

        var noUrl = RunOutcomes.ForFailure(new GeminiException(GeminiErrorKind.Billing, "Kein Guthaben"), audioKept: false);
        Assert.Equal(FollowUp.OpenSettings, noUrl.Action);
        Assert.Null(noUrl.Url);
        Assert.True(noUrl.KeepAudio); // today: _lastFailed = Failed(), whatever it is
    }

    [Fact]
    public void ForFailure_ServerTitleFollowsTheMessage_AndOtherProviderIsNamed()
    {
        Assert.Equal("🔄 Dienst nicht erreichbar",
            RunOutcomes.ForFailure(new GeminiException(GeminiErrorKind.Server, UserCopy.ServerBusy("Gemini")), true).Title);
        Assert.Equal("⚠️ Kein OpenAI-API-Schlüssel",
            RunOutcomes.ForFailure(new GeminiException("OpenAI", GeminiErrorKind.NoKey, "x"), true).Title);
    }

    [Fact]
    public void ForFailure_OfflineNotReady_OpensSettingsWithoutRetry()
    {
        var p = RunOutcomes.ForFailure(new OfflineNotReadyException("Noch nicht geladen."), audioKept: true);
        Assert.Equal(new Presentation(PresentationKind.Toast, "📥 Modell nicht geladen", "Noch nicht geladen.", Severity.Warning,
            ErrorSound: true, FollowUp.OpenSettings), p);
    }

    [Fact]
    public void ForFailure_LocalLlmAndEmptyReply_Retry()
    {
        Assert.Equal(new Presentation(PresentationKind.Toast, "⚠️ Fehler beim Offline-Modell", "kaputt", Severity.Error,
            ErrorSound: true, FollowUp.Retry, KeepAudio: true), RunOutcomes.ForFailure(new LocalLlmException("kaputt"), true));
        Assert.Equal(new Presentation(PresentationKind.Toast, "⚠️ Keine Antwort", "leer", Severity.Error,
            ErrorSound: true, FollowUp.Retry, KeepAudio: true), RunOutcomes.ForFailure(new EmptyModelReplyException("leer"), true));
        Assert.Equal(FollowUp.None, RunOutcomes.ForFailure(new LocalLlmException("kaputt"), false).Action);
    }

    [Fact]
    public void ForFailure_Microphone_IsAWarningWithTheExceptionText()
    {
        foreach (Exception e in new Exception[] { new AudioSilentException(), new MicrophoneUnavailableException(new Exception()) })
        {
            var p = RunOutcomes.ForFailure(e, audioKept: true);
            Assert.Equal(new Presentation(PresentationKind.Toast, "Problem mit dem Mikrofon", e.Message, Severity.Warning, ErrorSound: true), p);
        }
    }

    [Fact]
    public void ForFailure_SelectionUnreadable_NothingChanged()
    {
        Assert.Equal(new Presentation(PresentationKind.Toast, "Nichts geändert",
            "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", Severity.Warning, ErrorSound: true),
            RunOutcomes.ForFailure(new SelectionUnreadableException(), true));
    }

    [Theory]
    [InlineData(FailureContext.Dictation, "Fehler")]
    [InlineData(FailureContext.Live, "❌ Fehler")]
    public void ForFailure_Generic_KeepsEachPathsTitle(FailureContext context, string title)
    {
        const string text = "Etwas ist schiefgelaufen. Details stehen im Protokoll (Rechtsklick auf das Symbol → Protokoll öffnen).";
        Assert.Equal(new Presentation(PresentationKind.Toast, title, text, Severity.Error, ErrorSound: true, FollowUp.Retry, KeepAudio: true),
            RunOutcomes.ForFailure(new InvalidOperationException("x"), true, context));
        var lost = RunOutcomes.ForFailure(new InvalidOperationException("x"), false, context);
        Assert.Equal(FollowUp.None, lost.Action);
        Assert.False(lost.KeepAudio);
    }

    [Fact]
    public void ForFailure_Live_TreatsEverythingButMicrophoneAsUnexpected()
    {
        // The live path never had the provider ladder: a stray Gemini error keeps the generic live toast.
        var p = RunOutcomes.ForFailure(new GeminiException(GeminiErrorKind.Quota, "x"), true, FailureContext.Live);
        Assert.Equal("❌ Fehler", p.Title);
        Assert.Equal("Problem mit dem Mikrofon", RunOutcomes.ForFailure(new AudioSilentException(), true, FailureContext.Live).Title);
    }

    [Theory]
    [InlineData(GeminiErrorKind.NoKey, FollowUp.OpenAccounts)]
    [InlineData(GeminiErrorKind.InvalidKey, FollowUp.OpenAccounts)]
    [InlineData(GeminiErrorKind.Billing, FollowUp.None)]
    [InlineData(GeminiErrorKind.Quota, FollowUp.None)]
    [InlineData(GeminiErrorKind.Network, FollowUp.None)]
    public void ForFailure_ReadAloud_OnlyKeyProblemsOpenSettings(GeminiErrorKind kind, FollowUp action)
    {
        var p = RunOutcomes.ForFailure(new GeminiException(kind, "Meldung"), audioKept: false, FailureContext.ReadAloud);
        Assert.Equal(new Presentation(PresentationKind.Toast, "Vorlesen fehlgeschlagen", "Meldung", Severity.Error, ErrorSound: true, action), p);
    }

    [Fact]
    public void ForFailure_ReadAloud_OtherExceptions()
    {
        Assert.Equal(new Presentation(PresentationKind.Toast, "Vorlesen",
            "Markierter Text konnte nicht gelesen werden. Bitte noch einmal versuchen.", Severity.Warning, ErrorSound: true),
            RunOutcomes.ForFailure(new SelectionUnreadableException(), false, FailureContext.ReadAloud));
        Assert.Equal(new Presentation(PresentationKind.Toast, "Vorlesen fehlgeschlagen",
            "Der Text konnte nicht vorgelesen werden. Details stehen im Protokoll.", Severity.Error, ErrorSound: true),
            RunOutcomes.ForFailure(new LocalLlmException("egal"), false, FailureContext.ReadAloud));
    }

    [Fact]
    public void ForFailure_Chat_IsOnlyAStatusText()
    {
        Assert.Equal("Meldung", RunOutcomes.ForFailure(new GeminiException(GeminiErrorKind.Billing, "Meldung"), false, FailureContext.Chat).Text);
        Assert.Equal("Fehlt. Unter Einstellungen → Dictate Prompt lässt es sich laden.",
            RunOutcomes.ForFailure(new OfflineNotReadyException("Fehlt."), false, FailureContext.Chat).Text);
        Assert.Equal("Kaputt. Unter Einstellungen → Dictate Prompt lässt es sich laden.",
            RunOutcomes.ForFailure(new LocalLlmException("Kaputt."), false, FailureContext.Chat).Text);
        var generic = RunOutcomes.ForFailure(new InvalidOperationException(), false, FailureContext.Chat);
        Assert.Equal("Die Antwort konnte nicht geladen werden. Details stehen im Protokoll.", generic.Text);
        Assert.Equal(FollowUp.None, generic.Action);
        Assert.False(generic.ErrorSound);
    }

    [Fact]
    public void LogLine_NamesTheFailure()
    {
        Assert.Equal("Gemini Quota", RunOutcomes.LogLine(new GeminiException(GeminiErrorKind.Quota, "x"), out var w));
        Assert.False(w);
        Assert.Equal("unexpected error", RunOutcomes.LogLine(new InvalidOperationException(), out w));
        Assert.True(w);
        Assert.Equal("microphone problem AudioSilentException", RunOutcomes.LogLine(new AudioSilentException(), out _));
    }
}
