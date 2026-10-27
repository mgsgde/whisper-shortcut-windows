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

    /// <summary>
    /// Sample for practices: a dictated treatment note in three sections. The markers
    /// are the ones <see cref="Core.DictationFidelityCheck"/> ignores, so a gap shows as a marker
    /// instead of an invented sentence.
    /// </summary>
    public const string Praxis =
        """
        Wenn ich eine Behandlung dokumentiere, gliedere das Diktat so (Deutsch, sachlich, Stichpunkte erlaubt):

        Anamnese:
        [Beschwerden, Verlauf, Vorgeschichte, so wie diktiert]

        Befund:
        [Untersuchung und Tests, so wie diktiert]

        Therapie:
        [Behandlung, Empfehlungen, nächster Termin, so wie diktiert]

        Nur übernehmen, was ich diktiere: keine Diagnosen, Medikamente, Dosierungen oder Behandlungen ergänzen.
        Fehlt zu einem Abschnitt etwas, schreibe „(nicht diktiert)“. Ist unklar, wohin ein Satz gehört, setze „[Zuordnung prüfen]“ dahinter.
        Ein Wort, das ich vermutlich falsch verstanden habe, bekommt „[?]“. Fachbegriffe, Namen und Zahlen genau so, wie ich sie sage.

        Wenn ich einen Brief oder eine Nachricht diktiere (an Patienten, Ärzte, Kassen): kurz und freundlich, Sie-Form, mit Anrede und Gruß.
        Immer reiner Text, ohne Sternchen oder Rauten.
        """;
}
