# Gemeinsame Testfälle für Mac und Windows (Plan)

Stand 31.10.2026. Ziel: Die Logik, die nicht am System hängt, verhält sich auf beiden Apps gleich, und
das prüft ein Test, kein Abgleich von Hand. Windows darf Funktionen später bekommen als der Mac, aber
wenn eine Funktion da ist, liefert sie dasselbe Ergebnis.

## Prinzip

Die Fälle sind **Daten, kein Code**. Eine JSON-Datei pro Funktion, darin Eingabe und erwartetes
Ergebnis. Beide Testsuiten lesen dieselben Dateien und schicken jeden Fall durch ihre eigene
Implementierung. Wer einen Fehler in der Textlogik findet, schreibt zuerst einen Fall dazu, dann wird er
auf beiden Seiten behoben (oder auf Windows als offen markiert).

Was es heute schon gibt: `PromptsTests.cs` vergleicht Prompt-Texte mit dem Swift-Quelltext über feste
Zeilenfenster (`SwiftSourceFact`). Das funktioniert, bricht aber, sobald sich in der Swift-Datei eine Zeile
verschiebt, und prüft nur Texte, kein Verhalten. Die gemeinsamen Fälle lösen das ab.

## Was rein gehört und was nicht

**Rein (reine Funktionen, Text rein, Text oder Entscheidung raus):**

| Vertrag | Mac | Windows |
|---|---|---|
| `fidelity.unsupportedWords` | `DictationFidelityCheck.unsupportedWords` | `DictationFidelityCheck.UnsupportedWords` |
| `cleanup.normalize` | `TextProcessingUtility.normalizeTranscriptionText` | `TextCleanup.Normalize` |
| `cleanup.stripPreamble` | `strippingTranscriptionPreamble` | Teil von `TextCleanup.ForDictation` |
| `cleanup.discardImplausible` | `discardingImplausibleTranscript` | `TextCleanup.DiscardImplausible` |
| `glossary.discardEcho` | `discardingGlossaryEchoTranscript` | `TextCleanup.DiscardGlossaryEcho` |
| `glossary.stripTrailingEcho` | `strippingTrailingGlossaryEcho` | `TextCleanup.StripTrailingGlossaryEcho` |
| `glossary.parse` / `glossary.hint` | `SpeechService` (Glossar-Teil) | `GlossaryPrompt.Parse` / `Hint` |
| `glossary.correctRejected` | Nachkorrektur in `SpeechService` | `GlossaryPrompt.CorrectRejectedSpellings` |
| `reply.looksLikeRefusal` | Absage-Erkennung in `TextProcessingUtility` | `TextCleanup.LooksLikeRefusal` |
| `speech.plainFromMarkdown` | `SpeechTextSanitizer.plainSpeech` | `TextCleanup.StripMarkdown` |
| `prompts.*` | `AppConstants.swift` | `Prompts.cs` |

**Zweite Welle (Entscheidungen, auf Windows teils noch nicht gebaut):** Retry ja/nein und Wartezeit
aus Statuscode und Antworttext (`RetryBackoff`, 429 mit `RetryInfo`), Einfügeziel (`PasteTargetPolicy`),
Migration alter Modell-IDs. Diese Fälle zeigen Windows-Lücken direkt als offene Tests an und ersetzen
nach und nach die Tabelle in `docs/macos-paritaet.md`.

**Nicht rein:** Hotkey, Mikrofon, Einfügen, Zwischenablage, Fenster. Das ist pro System anders und
bleibt bei den Ende-zu-Ende-Tests (`tools/e2e/` in der VM, auf dem Mac von Hand bzw. mit den UI-Tests
dort). Auch nicht rein: Aufrufe an Gemini oder OpenAI selbst. Geprüft wird nur, was die Apps mit der
Antwort machen.

## Format

Eine Datei pro Vertrag, z. B. `cases/glossary.discardEcho.json`:

```json
{
  "contract": "glossary.discardEcho",
  "cases": [
    {
      "id": "pure-term-list-short-audio",
      "note": "Modell gibt bei 2 s Stille nur die Glossarliste zurück",
      "input": { "text": "Bachata, Kizomba, Sabaki", "audioSeconds": 2.0,
                 "glossaryTerms": ["Bachata", "Kizomba", "Sabaki"] },
      "expect": { "equals": "" },
      "windows": "ok"
    }
  ]
}
```

- `expect` kennt wenige Formen: `equals`, `isEmpty`, `contains` / `notContains` (Listen, wie beim
  Fidelity-Check, wo die Reihenfolge der markierten Wörter egal ist) und `bool`.
- `windows`: `ok` (muss bestehen), `offen` (Funktion fehlt auf Windows, Test wird mit Begründung
  übersprungen), `abweichend` (bewusst anders, Grund steht in `note`). Dasselbe Feld `mac` gibt es für
  den seltenen Fall, dass Windows weiter ist.
- Die `id` bleibt stabil, damit Testberichte auf beiden Seiten denselben Fall benennen.

## Wo die Dateien liegen

Quelle ist die Mac-App, weil sie die Referenz ist: `whisper-shortcut-private/shared-cases/`. Windows
hält eine Kopie unter `tests/shared-cases/`, damit die Tests auch ohne das private Mac-Repo laufen
(z. B. bei Christoph oder in CI). Ein Test auf Windows vergleicht die Kopie mit der Quelle, wenn das
Mac-Repo auf der Maschine liegt (wie heute `SwiftSourceFact`), und schlägt fehl, wenn sie veraltet ist.
`tools/sync-cases.sh` kopiert.

Verworfen: ein eigenes Repo als Git-Submodul. Sauberer, aber jeder Fall braucht dann drei Commits in
drei Repos, und Submodule nerven beim Arbeiten mit Worktrees.

## Ausführung

- **Windows (xUnit):** eine `[Theory]` pro Vertrag mit `MemberData`, das die JSON-Datei liest. Ein
  kleiner Verteiler ordnet den Vertragsnamen der C#-Funktion zu. Ein Vertrag ohne Eintrag im Verteiler
  lässt den Test fehlschlagen, damit nichts still ungeprüft bleibt.
- **Mac (Swift Testing):** `@Test(arguments:)` über dieselben Fälle, Verteiler in Swift. Die Dateien
  kommen als Ressource ins Test-Target.
- Auf Windows erscheint am Ende eine kurze Zählung: `ok`, `offen`, `abweichend` pro Vertrag. Das ist
  die Paritätsliste, die sich selbst aktuell hält.

## Woher die ersten Fälle kommen

Die Mac-App hat rund 1.200 `@Test`-Fälle. Die passenden davon (Textlogik, siehe Tabelle oben) werden
in JSON übertragen, statt neue zu erfinden. Danach läuft jeder Fall auf Windows. Was dort scheitert,
ist entweder ein Windows-Fehler (beheben) oder eine bewusste Abweichung (begründen). Echte Ausgaben, die
Nutzer gemeldet haben (Vollmer-Praxis, Christoph), kommen als Fälle mit Quelle in `note` dazu.

## Schritte

1. **Pilot `fidelity.unsupportedWords`.** Gleiche Signatur auf beiden Seiten, Mac-Tests liegen in
   `DictationFidelityCheckTests.swift`. Format, Verteiler und beide Runner daran fertig machen.
   Abnahme: beide Suiten grün mit denselben Fall-IDs.
2. **Textbereinigung und Glossar** (`cleanup.*`, `glossary.*`, `reply.*`, `speech.*`) aus den
   Mac-Tests übertragen. Abweichungen auf Windows beheben oder markieren.
3. **Prompts** aus den Zeilenfenstern lösen: die Prompt-Texte als Fälle (`prompts.*`, `equals`) statt
   Swift-Quelltext zu parsen. `PromptsTests.cs` danach entfernen.
4. **Zweite Welle** (Retry, 429, Einfügeziel, Modell-Migration) als Fälle anlegen, auf Windows zunächst
   `offen`. Jede Windows-Lücke aus `docs/macos-paritaet.md` wird so zu einem Test, der grün wird, wenn sie
   geschlossen ist.
5. **Regel in beide `AGENTS.md`:** Änderung an Textlogik heißt Fall zuerst, dann Code, dann
   `tools/sync-cases.sh`.

Wer was macht (wie in `AGENTS.md`): Format, Verträge und Review der Fälle macht die Opus-Session.
Runner, Verteiler, Sync-Skript und das Übertragen der Mac-Tests in JSON gehen an Grok bzw. Sonnet.
Abnahme ist jeweils die wörtliche Testausgabe beider Suiten.

## Aufwand und Risiken

- Schritt 1 ca. ein halber Tag, Schritt 2 ein bis zwei Tage (das meiste ist Übertragen), Schritte 3–5
  je ein paar Stunden.
- **Unterschiede zwischen Swift und .NET** bei Unicode (Umlaut-Faltung, Groß/Klein, Regex). Genau diese
  Fälle sind der Grund für das Ganze. Wo sie auftauchen, entscheidet der Fall, nicht die Sprache.
- **Verschiedene Signaturen:** Manche Mac-Funktionen sind `private` oder nehmen mehr Parameter. Dann
  bekommt der Verteiler eine dünne Hülle, die Funktion selbst wird nicht umgebaut.
- **Mac-Repo ist privat:** Christoph sieht die Quelle nicht, nur die Kopie in diesem Repo. Reicht zum
  Testen.

## Stand 31.10.2026 (pausiert bis Mo/Di)

- Worktrees, Branch `test/shared-cases`: Mac `~/wt/mac-shared-cases` (Repo `whisper-shortcut`),
  Windows `~/wt/win-shared-cases`. Nichts gepusht.
- Briefs: `docs/gemeinsame-testfaelle-briefs/brief-mac.md` und `brief-win.md` (nicht committet).
- Ein Grok-Lauf mit dem Mac-Brief lief beim Pausieren noch. Weitermachen: `git -C ~/wt/mac-shared-cases log -1`
  und Diff prüfen, Mac-Tests (`bash scripts/run-tests.sh --hermetic`) selbst laufen lassen, dann
  `grok-build -f docs/gemeinsame-testfaelle-briefs/brief-win.md` im Windows-Worktree.
- Grok-Mac-Lauf beendet (exit 0), letzter Commit: `58891ab8 test: shared JSON cases for the dictation fidelity check`. Log: `docs/gemeinsame-testfaelle-briefs/grok-mac.log`. Noch nicht geprüft.
