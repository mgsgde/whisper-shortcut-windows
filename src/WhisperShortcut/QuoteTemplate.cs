namespace WhisperShortcut;

/// <summary>
/// The "Vorlage" for Dictate Prompt without a selection. It is not used as the whole system prompt:
/// it is appended to the Mac's default editing prompt, so "mach das freundlicher" on a selection
/// still gets the full editing rules and only compose turns (NO SELECTED TEXT) follow the template.
/// </summary>
internal static class QuoteTemplate
{
    public const string Default =
        """
        Wenn ich ein Angebot diktiere, schreibe es in genau diesem Format (Deutsch, Sie-Form, sachlich und freundlich):

        Angebot Nr. [Nummer]
        Datum: [Datum]

        [Name des Kunden]
        [Straße, Hausnummer]
        [PLZ Ort]

        Betreff: Angebot für [Leistung in wenigen Worten]

        Sehr geehrte/r [Anrede und Nachname],

        vielen Dank für Ihre Anfrage. Gerne bieten wir Ihnen folgende Leistungen an:

        Pos. 1  [Menge] × [Material oder Leistung]  je [Einzelpreis] €  [Gesamtpreis] €
        Pos. 2  [Menge] × [Material oder Leistung]  je [Einzelpreis] €  [Gesamtpreis] €
        Arbeitszeit: [Stunden] Std. × [Stundensatz] €  [Gesamtpreis] €
        Anfahrt: pauschal [Betrag] €

        Summe netto: [Betrag] €
        zzgl. 19 % MwSt.: [Betrag] €
        Gesamtbetrag: [Betrag] €

        Ausführung: [Zeitraum oder „nach Absprache“]
        Dieses Angebot gilt 30 Tage. Zahlbar innerhalb von 14 Tagen nach Rechnungsstellung ohne Abzug.

        Bei Fragen erreichen Sie uns jederzeit. Wir freuen uns auf Ihren Auftrag.

        Mit freundlichen Grüßen
        [Firmenname]

        Wenn ich eine E-Mail oder Nachricht diktiere: kurz und freundlich, Sie-Form, mit Anrede und Gruß, ohne Angebotsformat.
        Namen, Mengen, Preise und Termine nur übernehmen, wenn ich sie sage. Alles andere bleibt als [Platzhalter] stehen. Nie Preise erfinden, nie selbst rechnen, wenn mir eine Zahl fehlt.
        """;

    /// <summary>System prompt for a Dictate Prompt request, or null for the plain Mac default.</summary>
    public static string? BuildSystemPrompt(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        return Core.Prompts.DefaultPromptModeSystemPrompt
            + "\n\nTemplate for new texts — applies ONLY when the request is marked NO SELECTED TEXT, never when editing a selection. "
            + "Write the finished text in the language of the voice instruction and follow this template from the user exactly "
            + "(structure, sections, wording). Fill in only what was spoken; keep every [placeholder] the speaker did not fill:\n\n"
            + template.Trim();
    }
}
