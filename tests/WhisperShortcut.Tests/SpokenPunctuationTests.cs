using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Port of SpokenPunctuationTests.swift: what changes, and the ordinary words that must not.</summary>
public class SpokenPunctuationTests
{
    private static string A(string s) => SpokenPunctuation.Apply(s);

    [Fact]
    public void ColonAttachesToPreviousWord() =>
        Assert.Equal("Anamnese: Patient berichtet über Schmerzen",
            A("Anamnese Doppelpunkt Patient berichtet über Schmerzen"));

    [Fact]
    public void ColonEatsStrayPunctuation()
    {
        Assert.Equal("Befund: Inspektion", A("Befund, Doppelpunkt. Inspektion"));
        Assert.Equal("Befund: Inspektion", A("Befund. Doppelpunkt, Inspektion"));
        Assert.Equal("Therapie:", A("Therapie Doppelpunkt."));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.Equal("Befund: Inspektion", A("Befund doppelpunkt Inspektion"));
        Assert.Equal("Befund: Inspektion", A("Befund DOPPELPUNKT Inspektion"));
    }

    [Fact]
    public void ParagraphBreak()
    {
        Assert.Equal("Patient klagt über Schmerzen.\n\nBefund unauffällig",
            A("Patient klagt über Schmerzen, neuer Absatz, Befund unauffällig"));
        Assert.Equal("Patient klagt über Schmerzen.\n\nBefund unauffällig",
            A("Patient klagt über Schmerzen. Neuer Absatz. Befund unauffällig"));
    }

    [Fact]
    public void LineBreak()
    {
        // A spoken break starts the next line even without ".!?" before it, so the next word
        // gains a capital. It used to stay lower-case ("zweite").
        Assert.Equal("Erste Zeile\nZweite Zeile", A("Erste Zeile neue Zeile zweite Zeile"));
    }

    [Fact]
    public void FullNote()
    {
        var spoken = "Anamnese Doppelpunkt Schmerzen LWS seit drei Tagen. Neuer Absatz Befund, Doppelpunkt. "
            + "ISG links blockiert. Neuer Absatz. Therapie Doppelpunkt neue Zeile HVLA";
        Assert.Equal("Anamnese: Schmerzen LWS seit drei Tagen.\n\nBefund: ISG links blockiert.\n\nTherapie:\nHVLA", A(spoken));
    }

    [Fact]
    public void OrdinaryWordsStay()
    {
        var text = "Der wichtigste Punkt ist das Komma und das Fragezeichen am Ende.";
        Assert.Equal(text, A(text));
    }

    [Fact]
    public void WholeWordsOnly()
    {
        var text = "Die Doppelpunkte und der Absatzmarke bleiben.";
        Assert.Equal(text, A(text));
    }

    [Fact]
    public void NoCommandsUnchangedWhitespaceIncluded()
    {
        var text = "  Patient berichtet über Schmerzen im unteren Rücken.\n";
        Assert.Equal(text, A(text));
    }

    [Fact]
    public void HyphenatedWords()
    {
        Assert.Equal("Anamnese: LWS-Schmerzen seit drei Wochen.",
            A("Anamnese Doppelpunkt LWS-Schmerzen seit drei Wochen."));
        Assert.Equal("Anamnese: Patient berichtet", A("Anamnese, Doppelpunkt. Patient berichtet"));
    }

    [Fact]
    public void Sequence()
    {
        Assert.Equal("Anamnese: Schmerzen.\n\nBefund: Tonus erhöht",
            A("Anamnese Doppelpunkt Schmerzen. Neuer Absatz. Befund Doppelpunkt Tonus erhöht"));
        // Capital after the break: the next line starts a sentence even without ".!?".
        Assert.Equal("Erstens\nZweitens", A("Erstens neue Zeile zweitens"));
        Assert.Equal("Doppelpunkte zählen", A("Doppelpunkte zählen"));
    }

    [Fact]
    public void GluedColon()
    {
        var raw = "Anamnese Doppelpunkt Schmerzen im unteren Rücken seit drei Wochen, neuer Absatz, "
            + "Befund Doppelpunktdruckschmerz L4 bis S1, neuer Absatz, "
            + "Therapie Doppelpunkt Mobilisation der LWS, das ist der wichtigste Punkt.";
        Assert.Equal("Anamnese: Schmerzen im unteren Rücken seit drei Wochen.\n\nBefund: Druckschmerz L4 bis S1.\n\n"
            + "Therapie: Mobilisation der LWS, das ist der wichtigste Punkt.", A(raw));
        var inflected = "Die Doppelpunkten und des Doppelpunktes";
        Assert.Equal(inflected, A(inflected));
    }

    private const PunctuationMode Spoken = PunctuationMode.SpokenOnly;
    private static string S(string s) => SpokenPunctuation.Apply(s, Spoken);
    private static string Strip(string s) => SpokenPunctuation.StripModelPunctuation(s);

    // ---- Schrägstrich ----

    [Fact]
    public void Slash()
    {
        Assert.Equal("ich/sage", S("ich Schrägstrich sage"));
        Assert.Equal("ich/sage", S("ich, Schrägstrich. sage"));
        Assert.Equal("Hals/Nacken", S("Hals Schraegstrich Nacken"));
        // Automatic mode and Dictate Prompt instructions: an ordinary noun.
        var text = "ersetze jeden Schrägstrich durch ein Komma, getrennt mit einem Schrägstrich";
        Assert.Equal(text, A(text));
        Assert.Equal("ich Schrägstrich sage", A("ich Schrägstrich sage"));
    }

    [Fact]
    public void SlashGluedAndPlural()
    {
        Assert.Equal("Hals/nacken", S("Hals Schrägstrichnacken"));
        Assert.Equal("zwei Schrägstriche im Text", S("zwei Schrägstriche im Text"));
    }

    [Fact]
    public void SlashKeepsAbbreviationPeriod() =>
        Assert.Equal("usw./b", S(Strip("usw. Schrägstrich b")));

    [Fact]
    public void DecimalComma()
    {
        Assert.Equal("3,5 cm", S("3 Komma 5 cm"));
        Assert.Equal("Hallo, 5 Stück", S("Hallo Komma 5 Stück"));
        Assert.Equal("3,5", S("drei Komma fünf"));
    }

    [Fact]
    public void CapitalisationAfterBreak()
    {
        Assert.Equal("Ende.\nWeiter", S("Ende Punkt neue Zeile weiter"));
        Assert.Equal("Gut?\n\nJa", S("Gut Fragezeichen neuer Absatz ja"));
    }

    [Fact]
    public void EffectiveMode()
    {
        Assert.Equal(PunctuationMode.Automatic, PunctuationModes.Effective(Spoken, isOffline: true, duringMeeting: true));
        Assert.Equal(PunctuationMode.Automatic, PunctuationModes.Effective(Spoken, isOffline: false, duringMeeting: false));
        Assert.Equal(Spoken, PunctuationModes.Effective(Spoken, isOffline: true, duringMeeting: false));
        Assert.Equal(PunctuationMode.Automatic, PunctuationModes.Effective(PunctuationMode.Automatic, isOffline: true, duringMeeting: false));
    }

    // ---- Only when spoken ----

    [Fact]
    public void StripsModelPunctuation()
    {
        // Parakeet writes a spoken "Punkt" as "." itself, so the sentence marks stay and so do capitals.
        Assert.Equal("Hallo Welt. Wie geht's Gut", Strip("Hallo, Welt. Wie geht's? Gut!"));
        Assert.Equal("Ende...", Strip("Ende..."));
        Assert.Equal("Befund: a b c.", Strip("Befund: a; b, c."));
    }

    [Fact]
    public void StripsGuessedQuestionAndExclamationMarks()
    {
        Assert.Equal("Die Dosis stimmt und weiter", SpokenOnly("Die Dosis stimmt? Und weiter"));
        Assert.Equal("Stimmt die Dosis?", SpokenOnly("Stimmt die Dosis Fragezeichen"));
        Assert.Equal("Stimmt die Dosis?", SpokenOnly("Stimmt die Dosis? Fragezeichen"));
        Assert.Equal("Wirklich!", SpokenOnly("Wirklich! Ausrufezeichen"));
        // Nouns and the formal "Sie" keep their capital.
        Assert.Equal("Stimmt das Dosis gleich", SpokenOnly("Stimmt das? Dosis gleich"));
        Assert.Equal("Geht es Sie kommen", SpokenOnly("Geht es? Sie kommen"));
        Assert.Equal("a?b und 1!2", Strip("a?b und 1!2"));
    }

    [Fact]
    public void TimedMarksOnlyStripSemicolons()
    {
        Assert.Equal("Gut! Danke. a b, c?", SpokenPunctuation.StripModelPunctuation("Gut! Danke. a; b, c?", timedMarks: true));
        Assert.Equal("Wirklich! Weiter", SpokenPunctuation.StripModelPunctuation("Wirklich! Weiter", timedMarks: true));
    }

    private static string SpokenOnly(string text) => SpokenPunctuation.Apply(Strip(text), Spoken);

    [Fact]
    public void StripKeepsNumbers()
    {
        Assert.Equal("Wert 3.5 und 1,2 mg ok.", Strip("Wert 3.5 und 1,2 mg, ok."));
        Assert.Equal("a.b", Strip("a.b"));
    }

    [Fact]
    public void StripKeepsOrdinalsAndAbbreviations()
    {
        var spoken = "Am 3. November untersuchte Dr. Müller z. B. Nr. 5 Punkt";
        Assert.Equal("Am 3. November untersuchte Dr. Müller z. B. Nr. 5.", S(Strip(spoken)));
        Assert.Equal("Der Befund ist unauffällig. Weiter ok.", Strip("Der Befund ist unauffällig. Weiter, ok."));
        Assert.Equal("bis 5.", Strip("bis 5."));
        Assert.Equal("5. Lendenwirbel", Strip("5. Lendenwirbel"));
    }

    [Theory]
    [InlineData("Dr. med. Müller")]
    [InlineData("d. h. die")]
    [InlineData("Az. 12 SB 345/24")]
    [InlineData("i. d. R. stabil")]
    [InlineData("Hauptstr. 5")]
    [InlineData("geb. 12. 3. 1961")]
    public void StripKeepsMoreAbbreviations(string text)
    {
        Assert.Equal(text, Strip(text));
        Assert.Equal("Er ging nach Hause. Dann kam sie.", Strip("Er ging nach Hause. Dann kam sie."));
    }

    [Fact]
    public void KeptPeriodThenComma() =>
        Assert.Equal("z. B. Kopfschmerzen", Strip("z. B., Kopfschmerzen"));

    [Fact]
    public void PunktNoun()
    {
        Assert.Equal("Zu Punkt 3 der Beweisfragen", S("Zu Punkt 3 der Beweisfragen"));
        Assert.Equal("Siehe Punkt 2.1", S("Siehe Punkt 2.1"));
        Assert.Equal("Er kam um 9 Uhr.\nDie", S("Er kam um 9 Uhr Punkt neue Zeile die"));
    }

    [Fact]
    public void AbbreviationThenKomma() =>
        Assert.Equal("usw., die", S(Strip("usw. Komma die")));

    [Fact]
    public void DecimalOnlySinglePair()
    {
        Assert.Equal("3,5 cm", S("3 Komma 5 cm"));
        Assert.Equal("3, 5, 4", S("3 Komma 5 Komma 4"));
    }

    [Theory]
    [InlineData(PunctuationMode.Automatic)]
    [InlineData(PunctuationMode.SpokenOnly)]
    public void CapitalAfterBreakWithModelPeriod(PunctuationMode mode)
    {
        Assert.Equal("Anamnese.\n\nDie Klägerin", SpokenPunctuation.Apply("Anamnese. Neuer Absatz, die Klägerin", mode));
        Assert.Equal("Ist sie arbeitsfähig?\nNein.", SpokenPunctuation.Apply("Ist sie arbeitsfähig? Neue Zeile, nein.", mode));
    }

    [Fact]
    public void SpokenOnlyMarks()
    {
        Assert.Equal("Hallo, wie geht es?", S("Hallo Komma wie geht es Fragezeichen"));
        Assert.Equal("Gut! Danke", S("Gut Ausrufezeichen danke"));
        Assert.Equal("Das ist alles. Weiter", S("Das ist alles Punkt Weiter"));
        Assert.Equal("Ende.", S("Ende Punkt"));
    }

    [Fact]
    public void SpokenOnlyKeepsLayout() =>
        Assert.Equal("Befund: Druck.\n\nTherapie", S("Befund Doppelpunkt Druck Punkt neuer Absatz Therapie"));

    [Fact]
    public void AutomaticLeavesWordsAlone()
    {
        var text = "der wichtigste Punkt und ein Komma im Text";
        Assert.Equal(text, A(text));
        Assert.Equal(text, SpokenPunctuation.Apply(text, PunctuationMode.Automatic));
        Assert.Equal("Doppelpunkte und Kommas", S("Doppelpunkte und Kommas"));
    }

    // ---- Cases added with the Mac port ----

    [Fact]
    public void GluedLineBreak()
    {
        var raw = "Befund Doppelpunkt Neue Zeileinspektion unauffällig neue Zeilepalpation Doppelpunkt Triggerpunkte im Trapezius.";
        Assert.Equal("Befund:\nInspektion unauffällig\nPalpation: Triggerpunkte im Trapezius.", A(raw));
    }

    [Fact]
    public void NewLinePluralStaysWords()
    {
        var text = "Bitte zwei neue Zeilen einfügen, neue Zeilenumbrüche vermeiden.";
        Assert.Equal(text, A(text));
        // "Neuzeilen" is the plural glued into one word, not the command. "Neue Teile" is a real phrase.
        Assert.Equal("Neuzeilen bleiben.", A("Neuzeilen bleiben."));
        Assert.Equal("Neue Teile bestellen.", A("Neue Teile bestellen."));
    }

    [Fact]
    public void WordAfterExistingColonIsDropped()
    {
        Assert.Equal("Befund: Druckschmerz L4", A("Befund: Doppelpunkt Druckschmerz L4"));
        Assert.Equal("Anamnese:\nPatientin berichtet\n\nBefund:\nunauffällig",
            A("Anamnese:\nDoppelpunkt Patientin berichtet\n\nBefund:\nunauffällig"));
        Assert.Equal("Therapie:\nMobilisation", A("Therapie: Doppelpunkt\nMobilisation"));
    }

    [Fact]
    public void StructuredReplyUnchanged()
    {
        var reply = "Anamnese:\nSchmerzen LWS.\n\nBefund:\n(nicht diktiert)\n\nTherapie:\nMobilisation C6/C7.";
        Assert.Equal(reply, A(reply));
    }

    [Fact]
    public void SlashMishearings()
    {
        Assert.Equal("Arbeitsmarkt/im ADL", S("Arbeitsmarkt Schreckstrich im ADL"));
        Assert.Equal("Hals/Nacken", S("Hals Schräg Strich Nacken"));
        Assert.Equal("Hals/Nacken", S("Hals Schräg-Strich Nacken"));
        Assert.Equal("zwei Schrägstriche im Text", S("zwei Schrägstriche im Text"));
    }

    [Fact]
    public void Ellipsis()
    {
        Assert.Equal("Gesamt GdB… und", S("Gesamt GdB drei Pünktchen und"));
        Assert.Equal("Gesamt GdB… und", S("Gesamt GdB drei Puenktchen und"));
        Assert.Equal("Gesamt GdB… und", S("Gesamt GdB Auslassungspunkte und"));
        Assert.Equal("drei Punkte erreicht", S("drei Punkte erreicht"));
        Assert.Equal("Gesamt GdB drei Pünktchen und", A("Gesamt GdB drei Pünktchen und"));
    }

    [Fact]
    public void DatesAndClinicalAbbreviationsKeepPeriod()
    {
        Assert.Equal("Seit dem 12. März.", SpokenOnly("Seit dem 12. März."));
        Assert.Equal("Pat. ist mobil.", SpokenOnly("Pat. ist mobil."));
    }

    [Fact]
    public void PunktBeforeDigitOrNumberWordIsNoun()
    {
        Assert.Equal("unter Punkt 3 der Fragen", SpokenOnly("unter Punkt 3 der Fragen"));
        Assert.Equal("Unter Punkt drei steht", SpokenOnly("Unter Punkt drei steht"));
    }

    [Fact]
    public void DecimalCommaBetweenNumberWords()
    {
        Assert.Equal("Messwert 2,5", SpokenOnly("Messwert zwei Komma fünf"));
        Assert.Equal("3,5", S("drei Komma fünf"));
        var automatic = "zwei Komma fünf";
        Assert.Equal(automatic, SpokenPunctuation.Apply(automatic, PunctuationMode.Automatic));
        // A pair that continues a list stays words.
        Assert.Equal("zwei, fünf, vier", S("zwei Komma fünf Komma vier"));
    }

    [Fact]
    public void RepeatedSpokenMarksCollapse() =>
        Assert.Equal("Ende. Weiter", SpokenOnly("Ende Punkt Punkt weiter"));

    [Fact]
    public void ModelPeriodAndCapitalKeptCommaDropped()
    {
        Assert.Equal("Sie klagt. Über Schmerzen.", SpokenOnly("Sie klagt. Über Schmerzen."));
        Assert.Equal("Befund. Seit gestern", SpokenOnly("Befund. Seit gestern"));
        Assert.Equal("Wir bitten Sie um Antwort", SpokenOnly("Wir bitten, Sie um Antwort"));
        Assert.Equal("klagt. Über", SpokenOnly("klagt. Punkt Über"));
    }

    [Fact]
    public void SpokenMarkNextToModelMarkDoesNotDouble()
    {
        Assert.Equal("Gangbild, geht weiter.", SpokenOnly("Gangbild Komma geht weiter. Punkt."));
        Assert.Equal("Neue Befunde liegen nicht vor.", SpokenOnly("Neue Befunde liegen nicht vor Punkt."));
        Assert.Equal("Neue Befunde liegen nicht vor.", SpokenOnly("Neue Befunde liegen nicht vor. Punkt"));
        Assert.Equal("Gehstrecke unter 500 m.", SpokenOnly("Gehstrecke unter 500 m Punkt."));
        Assert.Equal("Gut?", SpokenOnly("Gut. Fragezeichen"));
        Assert.Equal("Gut! Danke", SpokenOnly("Gut! Ausrufezeichen danke"));
    }

    [Fact]
    public void GluedSlash()
    {
        Assert.Equal("Arbeitsmarkt/im ADL", SpokenOnly("Arbeitsmarktschrägstrich im ADL"));
        Assert.Equal("Arbeitsmarkt/Bewegung", SpokenOnly("Arbeitsmarktschrägstrichbewegung"));
        Assert.Equal("Die Schrägstriche fehlen", SpokenOnly("Die Schrägstriche fehlen"));
        Assert.Equal("Arbeitsmarkt/bewegung", SpokenOnly("Arbeitsmarkt Schrägstrichbewegung"));
        Assert.Equal("Arbeitsmarktschrägstrich im ADL", A("Arbeitsmarktschrägstrich im ADL"));
    }

    [Fact]
    public void GluedEllipsis()
    {
        Assert.Equal("Gesamt GDB… und", SpokenOnly("Gesamt GDB3-Pünktchen und"));
        Assert.Equal("Gesamt GDB… und", SpokenOnly("Gesamt GDB 3 Pünktchen und"));
        Assert.Equal("Gesamt GDB… und", SpokenOnly("Gesamt GDB drei-Pünktchen und"));
        Assert.Equal("Es gibt 13 Pünktchen", SpokenOnly("Es gibt 13 Pünktchen"));
    }

    [Fact]
    public void KeepsEdgeBreaksTrimsSpacesOnly()
    {
        Assert.Equal("Befund\n\n", SpokenPunctuation.Apply("Befund neuer Absatz", keepsEdgeBreaks: true));
        Assert.Equal("Befund", SpokenPunctuation.Apply("Befund neuer Absatz"));
        Assert.Equal("Erste Zeile\n", SpokenPunctuation.Apply("Erste Zeile neue Zeile", keepsEdgeBreaks: true));
        Assert.Equal("Befund\n\n", SpokenPunctuation.Apply("  Befund neuer Absatz  ", keepsEdgeBreaks: true));
    }

    [Fact]
    public void SemicolonWords()
    {
        Assert.Equal("Hüfte frei beweglich; Knie", S("Hüfte frei beweglich Semikolon Knie"));
        Assert.Equal("a; b", S("a Strichpunkt b"));
    }

    [Fact]
    public void CommandWordsAndFunctionWords()
    {
        Assert.Contains("doppelpunkt", SpokenPunctuation.CommandWords);
        Assert.Contains("zeile", SpokenPunctuation.CommandWords);
        Assert.Contains("klammer", SpokenPunctuation.CommandWords);
        Assert.Equal("und", SpokenPunctuation.LowercasingFunctionWord("Und"));
        Assert.Equal("diese", SpokenPunctuation.LowercasingFunctionWord("Diese"));
        Assert.Equal("keine", SpokenPunctuation.LowercasingFunctionWord("Keine"));
        Assert.Equal("bis", SpokenPunctuation.LowercasingFunctionWord("Bis"));
        Assert.Equal("ich", SpokenPunctuation.LowercasingFunctionWord("Ich"));
        Assert.Null(SpokenPunctuation.LowercasingFunctionWord("Sie"));
        Assert.Null(SpokenPunctuation.LowercasingFunctionWord("Ihr"));
        Assert.Null(SpokenPunctuation.LowercasingFunctionWord("Ihnen"));
        Assert.Null(SpokenPunctuation.LowercasingFunctionWord("Dosis"));
        Assert.Null(SpokenPunctuation.LowercasingFunctionWord("und"));
    }
}

/// <summary>Brackets, Parakeet's glued "Neuzeile" / "Neuerabsatz", and a capital after a break (Mac #385).</summary>
public class SpokenPunctuationBracketTests
{
    private static string Apply(string text, PunctuationMode mode = PunctuationMode.Automatic) =>
        SpokenPunctuation.Apply(text, mode);

    [Fact]
    public void BracketsInBothModes()
    {
        (string Spoken, string Written)[] cases =
        [
            (
                "Durch die Updates ist es Klammer auf soweit ich das beurteilen kann Klammer zu deutlich besser geworden.",
                "Durch die Updates ist es (soweit ich das beurteilen kann) deutlich besser geworden."
            ),
            (
                "Sobald ich Klammer auf wahrscheinlich morgen Klammer zu mehr arbeite.",
                "Sobald ich (wahrscheinlich morgen) mehr arbeite."
            ),
            ("Siehe unten Klammer auf Anlage Klammer zu.", "Siehe unten (Anlage)."),
            ("Klammer auf siehe Anlage Klammer zu", "(siehe Anlage)"),
            ("es, Klammer auf, soweit, Klammer zu, deutlich", "es (soweit) deutlich"),
        ];
        foreach (var mode in new[] { PunctuationMode.Automatic, PunctuationMode.SpokenOnly })
            foreach (var (spoken, written) in cases)
                Assert.Equal(written, Apply(spoken, mode));
    }

    [Fact]
    public void BracketsGluedAndHyphenated()
    {
        Assert.Equal("Siehe (Anlage).", Apply("Siehe Klammerauf Anlage Klammer-zu."));
        Assert.Equal("es (soweit) deutlich", Apply("es Klammer-auf soweit Klammerzu deutlich"));
    }

    [Fact]
    public void BracketsStayNouns()
    {
        foreach (var text in new[]
        {
            "Die Hautklammer wurde entfernt.",
            "Zwei Klammern auf der Wunde.",
            "Eine Klammer auf die Wunde gesetzt.",
        })
        {
            Assert.Equal(text, Apply(text));
            Assert.Equal(text, Apply(text, PunctuationMode.SpokenOnly));
        }
    }

    [Fact]
    public void OpeningParenSurvivesEdgeTrim() =>
        Assert.Equal("ist es (",
            SpokenPunctuation.Apply("ist es Klammer auf", PunctuationMode.SpokenOnly, keepsEdgeBreaks: true));

    [Fact]
    public void GluedBreaks()
    {
        Assert.Equal("Update.\nDie Fehler habe ich bewusst gelassen.",
            Apply("Update. Neuzeile die Fehler habe ich bewusst gelassen."));
        // The command is the whole trailing word, so only the line break is left and then trimmed.
        Assert.Equal("einen schönen Sonntag", Apply("einen schönen Sonntag Neuezeile"));
        Assert.Equal("Befund\n\nTherapie", Apply("Befund Neuerabsatz Therapie"));
        Assert.Equal("Befund\n\nTherapie", Apply("Befund Neuenabsatz Therapie"));
        Assert.Equal("Termin ist geplant.\n\nMit freundlichen Grüßen",
            Apply("Termin ist geplant. Neuabsatz mit freundlichen Grüßen"));
    }

    [Fact]
    public void CapitalAfterPlainBreak()
    {
        Assert.Equal("Liebe Grüße und einen schönen Sonntag\nWie gesagt",
            Apply("Liebe Grüße und einen schönen Sonntag neue Zeile wie gesagt"));
        // The model's pause comma is eaten; the next word keeps the case it was spoken with.
        const string comma = "Hallo Herr Müller, neue Zeile vielen Dank";
        const string eaten = "Hallo Herr Müller\nvielen Dank";
        Assert.Equal(eaten, Apply(comma));
        Assert.Equal(eaten, Apply(comma, PunctuationMode.SpokenOnly));
        Assert.Equal("Befund\nInspektion", Apply("Befund, neue Zeile Inspektion"));
        // Spoken "Komma" becomes the comma only in spoken-only mode, after the break was inserted.
        Assert.Equal("Hallo Herr Müller,\nvielen Dank",
            Apply("Hallo Herr Müller Komma neue Zeile vielen Dank", PunctuationMode.SpokenOnly));
        // ", neuer Absatz" still ends the sentence with a period and does not add its own capital.
        Assert.Equal("Schmerzen.\n\nbefund", Apply("Schmerzen, neuer Absatz befund"));
    }

    [Fact]
    public void SplitDoppelPunktIsAColon()
    {
        const string spoken = "Befund Doppel Punkt Druckschmerz";
        const string written = "Befund: Druckschmerz";
        Assert.Equal(written, Apply(spoken));
        Assert.Equal(written, Apply(spoken, PunctuationMode.SpokenOnly));
        Assert.Equal(written, Apply("Befund Doppel-Punkt Druckschmerz", PunctuationMode.SpokenOnly));
    }
}

/// <summary>
/// Pilot-path fixes from Mac #393: a spoken comma before a break, "Klammer auf" plus an article when
/// "Klammer zu" follows, "ein/zu/in/einen Punkt", lower case after a salutation break, and at most
/// two line breaks across a piece seam. Glossary-guard cases are not ported.
/// </summary>
public class SpokenPunctuationPilotTests
{
    private static string Pipeline(params string[] pieces)
    {
        var joiner = new LiveChunkJoiner { HoldsPeriods = false };
        var text = "";
        foreach (var piece in pieces)
            text += joiner.Next(LiveDictation.FinishPiece(piece, PunctuationMode.SpokenOnly, timedMarks: true));
        return text + joiner.Finish();
    }

    [Fact]
    public void CommaBeforeBreakStaysWhenTimed()
    {
        Assert.Equal("Sehr geehrte Frau Müller,\nvielen Dank für Ihre Nachricht.",
            Pipeline("Sehr geehrte Frau Müller, neue Zeile vielen Dank für Ihre Nachricht."));
        Assert.Equal("Sehr geehrte Damen und Herren,\n\nwir haben Ihre Patientin gesehen.",
            Pipeline("Sehr geehrte Damen und Herren, neuer Absatz wir haben Ihre Patientin gesehen."));
        Assert.Equal("Sehr geehrte Damen und Herren,\n\nwir haben Ihre Patientin gesehen.",
            Pipeline("Sehr geehrte Damen und Herren Komma neuer Absatz wir haben Ihre Patientin gesehen Punkt"));
        Assert.Equal("Sehr geehrte Damen und Herren,\n\n",
            LiveDictation.FinishPiece("Sehr geehrte Damen und Herren, neuer Absatz.", PunctuationMode.SpokenOnly, timedMarks: true));
        Assert.Equal("Müller,\nvielen Dank",
            LiveDictation.FinishPiece("Müller, neue Zeile vielen Dank", PunctuationMode.SpokenOnly, timedMarks: true));
    }

    [Fact]
    public void CommaBeforeBreakWithoutTimingKeepsTheOldBehaviour()
    {
        Assert.Equal("Müller\nVielen Dank",
            LiveDictation.FinishPiece("Müller, neue Zeile vielen Dank", PunctuationMode.SpokenOnly));
        Assert.Equal("Herren\n\nWir",
            LiveDictation.FinishPiece("Herren, neuer Absatz wir", PunctuationMode.SpokenOnly));
        Assert.Equal("Herren.\n\nwir",
            LiveDictation.FinishPiece("Herren, neuer Absatz wir", PunctuationMode.Automatic, timedMarks: true));
    }

    [Fact]
    public void BracketWithArticleNeedsClosingBracket()
    {
        Assert.Equal("Der Befund (die Laborwerte liegen bei) ist unauffällig.",
            Pipeline("Der Befund Klammer auf die Laborwerte liegen bei Klammer zu ist unauffällig Punkt"));
        Assert.Equal("Wir empfehlen (eine Kontrolle in drei Monaten).",
            Pipeline("Wir empfehlen Klammer auf eine Kontrolle in drei Monaten Klammer zu Punkt"));
        Assert.Equal("Zustand nach Hüft-TEP (links).",
            Pipeline("Zustand nach Hüft-TEP Klammer auf links Klammer zu Punkt"));
        Assert.Equal("Medikation (Ramipril, Metformin), weiterhin unverändert",
            Pipeline("Medikation Klammer auf Ramipril Komma Metformin Klammer zu Komma weiterhin unverändert"));
        Assert.Equal("Medikation (Ramipril).",
            Pipeline("Medikation Klammer auf", "Ramipril Klammer zu", "Punkt"));
        Assert.Equal("Medikation (Ramipril).",
            Pipeline("Medikation, Klammer auf. Ramipril, Klammer zu."));
        Assert.Equal("Eine Klammer auf die Wunde gesetzt.",
            SpokenPunctuation.Apply("Eine Klammer auf die Wunde gesetzt.", PunctuationMode.SpokenOnly));
        Assert.Equal("Klammer auf die Wunde", Pipeline("Klammer auf die Wunde"));
    }

    [Fact]
    public void PunktAfterParticleIsAMarkUnlessNumberOrNounFollows()
    {
        Assert.Equal("Bitte nehmen Sie die Tablette morgens ein.",
            Pipeline("Bitte nehmen Sie die Tablette morgens ein Punkt"));
        Assert.Equal("Bitte senden Sie uns den Befund zu.",
            Pipeline("Bitte senden Sie uns den Befund zu Punkt"));
        Assert.Equal("Die Patientin stellt sich in drei Monaten wieder vor. Die Werte sind in.",
            Pipeline("Die Patientin stellt sich in drei Monaten wieder vor Punkt Die Werte sind in Punkt"));
        Assert.Equal("Bitte nehmen Sie die Tablette morgens ein.",
            Pipeline("Bitte nehmen Sie die Tablette morgens ein", "Punkt"));
        foreach (var (text, expected) in new[]
        {
            ("morgens ein Punkt", "morgens ein."),
            ("den Befund zu Punkt", "den Befund zu."),
            ("wieder in Punkt Die Werte", "wieder in. Die Werte"),
            ("das ist ein Punkt mehr", "das ist ein Punkt mehr"),
            ("siehe in Punkt 3", "siehe in Punkt 3"),
            ("wir kommen zu Punkt drei", "wir kommen zu Punkt drei"),
            ("das ist ein Punkt Frage", "das ist ein Punkt Frage"),
        })
            Assert.Equal(expected, SpokenPunctuation.Apply(text, PunctuationMode.SpokenOnly));
    }

    [Fact]
    public void SalutationFollowersAfterBreakAreLowercase()
    {
        Assert.Equal("Sehr geehrte Frau Müller,\nvielen Dank",
            Pipeline("Sehr geehrte Frau Müller,", "neue Zeile vielen Dank"));
        Assert.Equal("Sehr geehrte Frau Müller,\nvielen Dank",
            Pipeline("Sehr geehrte Frau Müller, neue Zeile", "Vielen Dank"));
        Assert.Equal("Sehr geehrte Frau Müller,\nwir freuen uns",
            Pipeline("Sehr geehrte Frau Müller Komma neue Zeile", "Wir freuen uns"));
        foreach (var word in new[] { "Wir", "Ich", "Mit", "Anbei", "Bitte", "Danke" })
            Assert.Equal($"Guten Tag Herr Schmidt,\n{word.ToLowerInvariant()} melden uns",
                Pipeline("Guten Tag Herr Schmidt,", $"neue Zeile {word} melden uns"));
    }

    [Fact]
    public void CapitalStaysAfterBreakWithoutCommaAndNounKeepsItsCapital()
    {
        Assert.Equal("Die Patientin kam heute.\nVielen Dank",
            Pipeline("Die Patientin kam heute.", "neue Zeile vielen Dank"));
        Assert.Equal("Haben Sie Schmerzen?\nWir sehen uns",
            Pipeline("Haben Sie Schmerzen?", "neue Zeile wir sehen uns"));
        Assert.Equal("Befund\nVielen Dank", Pipeline("Befund", "neue Zeile vielen Dank"));
        Assert.Equal("Guten Tag,\nHerr Schmidt kommt",
            Pipeline("Guten Tag,", "neue Zeile Herr Schmidt kommt"));
    }

    [Fact]
    public void IchAfterSalutationCommaIsLowercaseAtAPieceSeam()
    {
        Assert.Equal("Sehr geehrte Damen und Herren, ich nehme Stellung.",
            Pipeline("Sehr geehrte Damen und Herren,", "Ich nehme Stellung."));
        Assert.Equal("Sehr geehrte Damen und Herren,\nich nehme Stellung.",
            Pipeline("Sehr geehrte Damen und Herren,", "neue Zeile Ich nehme Stellung."));
        Assert.Equal("Sehr geehrte Damen und Herren. Ich nehme Stellung.",
            Pipeline("Sehr geehrte Damen und Herren.", "Ich nehme Stellung."));
    }

    [Fact]
    public void BreaksAcrossPiecesCapAtTwo()
    {
        Assert.Equal("A\n\nB", Pipeline("A neuer Absatz", "neue Zeile B"));
        Assert.Equal("A\n\nB", Pipeline("A neuer Absatz", "neuer Absatz B"));
        Assert.Equal("A\n\nB", Pipeline("A neue Zeile", "neue Zeile B"));
        Assert.Equal("A\n\nB", Pipeline("A neue Zeile", "neuer Absatz B"));
        Assert.Equal("A\nB", Pipeline("A neue Zeile", "B"));
    }
}

/// <summary>False-positive guards from Mac #408 (R194).</summary>
public class SpokenPunctuationGuardTests
{
    [Fact]
    public void ClosingParenNeedsAnOpenParenInAutomaticMode()
    {
        Assert.Equal("um die Klammer zu entfernen", SpokenPunctuation.Apply("um die Klammer zu entfernen"));
        Assert.Equal("Die Klammer zu lösen war schwer", SpokenPunctuation.Apply("Die Klammer zu lösen war schwer"));
        Assert.Equal("(Test)", SpokenPunctuation.Apply("Klammer auf Test Klammer zu"));
        // Spoken-only decodes piece by piece: a lone closing half still converts there.
        Assert.Equal("Ramipril)", SpokenPunctuation.Apply("Ramipril Klammer zu", PunctuationMode.SpokenOnly));
    }

    [Fact]
    public void NewLineAfterAnArticleIsTheNounPhrase()
    {
        const string text = "Füge eine neue Zeile in der Tabelle ein";
        Assert.Equal(text, SpokenPunctuation.Apply(text));
        Assert.Equal(text, SpokenPunctuation.Apply(text, PunctuationMode.SpokenOnly));
        Assert.Equal("Befund\nInspektion", SpokenPunctuation.Apply("Befund neue Zeile Inspektion"));
    }

    [Fact]
    public void DoppelpunktsetzungStaysAWord() =>
        Assert.Equal("Befund Doppelpunktsetzung", SpokenPunctuation.Apply("Befund Doppelpunktsetzung"));

    [Fact]
    public void PunktAfterUmIsATime() =>
        Assert.Equal("Er kam um Punkt acht Uhr",
            SpokenPunctuation.Apply("Er kam um Punkt acht Uhr", PunctuationMode.SpokenOnly));

    [Fact]
    public void WrittenEllipsisSurvivesAndDuplicateMarksStillCollapse()
    {
        Assert.StartsWith("usw...", SpokenPunctuation.Apply("usw... und so Punkt", PunctuationMode.SpokenOnly));
        Assert.StartsWith("usw…", SpokenPunctuation.Apply("usw… und so Punkt", PunctuationMode.SpokenOnly));
        Assert.Equal("vor. Ende", SpokenPunctuation.Apply("vor. Punkt Ende", PunctuationMode.SpokenOnly));
    }
}
