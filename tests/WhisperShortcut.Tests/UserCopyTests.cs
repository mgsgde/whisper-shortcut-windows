using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UserCopyTests
{
    [Fact]
    public void StatusLines_StayGermanAndSpecific()
    {
        Assert.Equal("🔴 Diktat läuft … (Strg+Umschalt+D beendet)", UserCopy.RecordingStatus("Strg+Umschalt+D"));
        Assert.Equal("🔴 Dictate Prompt wird aufgenommen … (Alt+2 beendet)", UserCopy.RecordingStatus("Alt+2", prompt: true));
        // The Mac pill labels (Transcribing, Thinking, Listening, Prompting).
        Assert.Equal("Wird transkribiert …", UserCopy.Transcribing);
        Assert.Equal("Denkt nach …", UserCopy.RecognizingInstruction);
        Assert.Equal("Denkt nach …", UserCopy.EditingText);
        Assert.Equal("Hört zu", UserCopy.OverlayDictateTitle);
        Assert.Equal("Prompt läuft", UserCopy.OverlayPromptTitle);
        Assert.Equal("Kürzer fassen …", UserCopy.QuickActionBusy("Kürzer fassen"));
        Assert.Equal("Wird verarbeitet …", UserCopy.QuickActionBusy("  "));
        Assert.Contains("Esc", UserCopy.OverlayStopHint("Strg+Umschalt+P"));
        Assert.Contains("Esc", UserCopy.OverlayCancelHint);
    }

    [Fact]
    public void OverlayAnnouncement_JoinsVisibleLines()
    {
        Assert.Equal("Diktat läuft. Strg+Umschalt+D beendet · Esc bricht ab",
            UserCopy.OverlayAnnouncement("Diktat läuft", "Strg+Umschalt+D beendet · Esc bricht ab", null));
        Assert.Equal("Anweisung sprechen. Nichts markiert. Esc bricht ab",
            UserCopy.OverlayAnnouncement("Anweisung sprechen", "Esc bricht ab", "Nichts markiert"));
        Assert.Equal("Eingefügt", UserCopy.OverlayAnnouncement("Eingefügt", "", null));
    }

    [Theory]
    [InlineData(0, 6, 1, 1)]
    [InlineData(5, 6, 1, 0)]
    [InlineData(0, 6, -1, 5)]
    [InlineData(2, 6, -1, 1)]
    [InlineData(0, 0, 1, 0)]
    public void NextPage_Wraps(int current, int count, int step, int expected) =>
        Assert.Equal(expected, UserCopy.NextPage(current, count, step));

    [Fact]
    public void ServerMessages_ExplainTheNextStep_WithoutHttpCodes()
    {
        Assert.Equal("Gemini antwortet gerade nicht. Bitte später erneut versuchen.", UserCopy.ServerBusy("Gemini"));
        Assert.Equal("OpenAI kennt dieses Modell nicht mehr. Bitte in den Einstellungen ein anderes wählen.",
            UserCopy.ModelGone("OpenAI"));
        Assert.DoesNotContain("HTTP", UserCopy.ServerBusy("Gemini"));
        Assert.DoesNotContain("HTTP", UserCopy.ModelGone("Gemini"));
        Assert.Contains("unter Dictate Prompt", UserCopy.UnsupportedCpu);
        Assert.DoesNotContain("Online mit Gemini", UserCopy.UnsupportedCpu);
    }

    [Theory]
    [InlineData("Access is denied.", true)]
    [InlineData("System.IO.IOException: disk full", true)]
    [InlineData("   ", true)]
    [InlineData("HRESULT: 0x80070005", true)]
    [InlineData("at WhisperShortcut.Settings.Save()", true)]
    [InlineData("Kein Mikrofon gefunden oder Zugriff gesperrt.", false)]
    [InlineData("Die Datei ist beschädigt oder nicht von Magnus signiert. Nichts wurde installiert.", false)]
    public void LooksTechnical_DetectsRuntimeNoise(string text, bool technical) =>
        Assert.Equal(technical, UserCopy.LooksTechnical(text));

    [Fact]
    public void TechnicalFallback_KeepsGerman_HidesStacks()
    {
        Assert.Equal("Die Einstellungen konnten nicht gespeichert werden: Der Datenträger ist voll.",
            UserCopy.TechnicalFallback("Die Einstellungen konnten nicht gespeichert werden", "Der Datenträger ist voll."));
        Assert.Equal("Die Aufnahme konnte nicht starten. Details stehen im Protokoll.",
            UserCopy.TechnicalFallback("Die Aufnahme konnte nicht starten.", "System.InvalidOperationException: boom"));
        Assert.Equal("Das Modell konnte nicht gelöscht werden. Details stehen im Protokoll.",
            UserCopy.TechnicalFallback("Das Modell konnte nicht gelöscht werden", "HRESULT: 0x80070020"));
    }

    [Theory]
    [InlineData(GeminiErrorKind.NoKey, "Gemini", "⚠️ Kein Google-API-Schlüssel")]
    [InlineData(GeminiErrorKind.NoKey, "OpenAI", "⚠️ Kein OpenAI-API-Schlüssel")]
    [InlineData(GeminiErrorKind.InvalidKey, "OpenAI", "❌ Ungültiger API-Schlüssel")]
    [InlineData(GeminiErrorKind.Network, "Gemini", "⏰ Netzwerkfehler")]
    [InlineData(GeminiErrorKind.Timeout, "Gemini", "⏰ Zeitüberschreitung")]
    [InlineData(GeminiErrorKind.Quota, "Gemini", "⏳ Limit erreicht")]
    [InlineData(GeminiErrorKind.Billing, "OpenAI", "💳 Abrechnung erforderlich")]
    [InlineData(GeminiErrorKind.Server, "Gemini", "❌ Serverfehler")]
    public void ProviderErrorTitle_UsesTheMacTitles(GeminiErrorKind kind, string provider, string title) =>
        Assert.Equal(title, UserCopy.ProviderErrorTitle(kind, provider));

    [Fact]
    public void ProviderErrorTitle_TellsTheServerCasesApart()
    {
        Assert.Equal("❌ Modell nicht mehr verfügbar", UserCopy.ProviderErrorTitle(GeminiErrorKind.Server, "OpenAI", UserCopy.ModelGone("OpenAI")));
        Assert.Equal("🔄 Dienst nicht erreichbar", UserCopy.ProviderErrorTitle(GeminiErrorKind.Server, "Gemini", UserCopy.ServerBusy("Gemini")));
    }

    [Fact]
    public void PopupTexts_AreDuForm()
    {
        foreach (var t in new[] { UserCopy.NoSpeechText, UserCopy.NothingSelectedText })
        {
            Assert.DoesNotContain("Sie ", t);
            Assert.DoesNotContain("Ihr", t);
        }
    }
}
