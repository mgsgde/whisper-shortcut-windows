# Mac-Abgleich 23.11.2026: Mac-PRs #348–#572 (v8.80 → v9.08) gegen Windows

Stand: Windows `origin/main` = a924e9b (0.7.14, enthält den Mac-Look von Pill und Dictate-Prompt-Karte, 2603e7c); geprüft per `git grep` auf `origin/main`. Mac-Quelle: v9.08. 194 PRs aus `macprs-relevant.tsv` plus die Mac-Direktcommits ganz unten. 

Zähler (PRs): n/a (Mac-Tooling/Docs) 48, schon da 47, bewusst anders 41, fehlt 28, anderswo (Session/Bug) 14, Feature fehlt auf Windows ganz 10, in Arbeit (Feedback-PR) 3, unklar 3 = 194.

Lesehilfe: **fehlt** = Windows hat die Funktion, aber nicht diese Änderung (Größe S/M/L steht in der Notiz, dazu die Swift-Dateien). **Feature fehlt auf Windows ganz** = die Grundfunktion gibt es nicht. **anderswo** = Bereich gehört einer anderen Session (RecordingOverlay.cs, Silero-Gate/TranscribeOnlineAsync, ModelDownloader/Parakeet-Download): nicht hier anfassen.

| Mac-PR | Was (kurz, deutsch) | Windows | Status | Größe/Notiz |
|---|---|---|---|---|
| #348 | Release-Build mit Xcode-Cache | - | n/a (Mac-Tooling/Docs) |  |
| #349 | Einstellungen bleiben offen, solange ein Sheet da ist | - | bewusst anders | Windows-Einstellungen schließen nie von selbst (Plan 11.19: Schalter „außerhalb klicken“ offen); Fehler kann nicht auftreten |
| #350 | Build: Apple-Development-Identität | - | n/a (Mac-Tooling/Docs) |  |
| #351 | Konten: Eigener Server neben anderen Anbietern | - | bewusst anders | §8: keine Zusatzanbieter/eigener Server |
| #353 | Chat: Datum, Uhrzeit und Zone im Prompt | e9f561b (Mac #353), Core/ChatDateContext.cs | schon da |  |
| #355 | Chat: Schriftart Sans/Serif/Rounded | keine Schriftart-Einstellung im Chat | fehlt | S, niedrig. Swift: ChatTheme.swift, ChatSettingsTab.swift, SettingsConfiguration.swift. Einstellung Chat → Darstellung + Font der RichTextBox. |
| #357 | Meetings: Live-Notizen von Hand bearbeiten | keine laufenden Notizen (Plan 11.3: „keine laufenden Notizen und keine Zusammenfassung während des Meetings“) | Feature fehlt auf Windows ganz | L. Swift: LiveMeetingSession.swift, LiveMeetingTranscriptStore.swift, MeetingListService.swift, ChatView.swift. Voraussetzung: Live-Notizen selbst (siehe #407 R187, #444). |
| #358 | Eigener Server: finden statt raten | - | bewusst anders | §8 eigener Server |
| #359 | Menü: Meeting starten/fortsetzen/beenden | Tray-Untermenü „Meeting“ (MeetingController.cs, „Meeting starten“) | schon da | Windows hat das Untermenü zusätzlich (§8) |
| #362 | Dock-Symbol; Einstellungen schließen nicht bei Außenklick | - | bewusst anders | Dock = Mac; Fensterverhalten siehe #349 |
| #364 | Chat folgt dem Streaming, merkt sich den Zustand je Sitzung | a6ec2a5 (Mac #364), ChatWindow.IsAtBottom/FollowSlack | schon da | Merken je Sitzung nicht nötig (keine Chat-Tabs) |
| #365 | Diktat: ohne Schlüssel auf heruntergeladenes Offline-Modell zurückfallen oder Download anbieten | kein Reconciler gefunden; ohne Schlüssel kommt die NoKey-Meldung (CloudApi.NoKey) | fehlt | S–M, unklar wie Windows bei gelöschtem Schlüssel heute wählt. Swift: ModelSelectionReconciler.swift, MenuBarController.swift, WelcomeSteps.swift |
| #366 | Updater-Log pro Kanal (Sparkle) | - | n/a (Mac-Tooling/Docs) |  |
| #367 | Whisper-Stille-Halluzinationen unter 2 s verwerfen | 39f7b42 (Mac #367) | schon da |  |
| #368 | Chat: Mitscrollen als Schalter | ChatWindow folgt immer, wenn am Ende; kein Schalter | fehlt | S, niedrig. Swift: ChatSettingsTab.swift (followNewContent). Flag in Settings + Abfrage in ChatWindow.IsAtBottom. |
| #369 | Dictate Live: Schwelle relativ zum Ruhepegel | LivePieceCut: Ruhepegel + 12 dB, −60…−45 dB | bewusst anders | §8 „Live diktieren bei leisem Mikro“ (eigene Lösung) |
| #370 | Dictate-Prompt-Karte: letzte drei Prompts, letzte Wahl vorgewählt | RecordingOverlay.cs (Mac-Look seit 0.7.14, 2603e7c) | anderswo (Session/Bug) | Overlay-Session |
| #371 | Karte: eigene Prompts in fester Reihenfolge, drei Zeilen, Scroll bis ⌃9 | RecordingOverlay.cs | anderswo (Session/Bug) | Overlay-Session; Windows hat neun feste Felder in fester Reihenfolge (Plan 9) |
| #372 | MCP: Dictate-Prompt-Schnellanweisungen lesen/setzen | Core/Mcp/McpConfigTools.cs `update_quick_instructions` (eigenes Tool statt Katalogeintrag) | schon da | Windows-Variante |
| #373 | Claude Desktop verbindet ohne Node.js (die App selbst ist die stdio-Brücke) | McpClientSetup schreibt `npx mcp-remote@0.14.3`, McpService.cs:357 prüft npx | fehlt | M, hoher Nutzen für Praxis-Rechner ohne Node. Swift: MCPStdioBridge.swift, MCPClientInstaller.swift, FullApp.swift (`--mcp-stdio`, URL+Token per env). Windows: Argument in Program.cs, stdin↔HTTP. |
| #374 | KI-Apps: Trennen-Knopf | McpClientSetup.RemoveServer, ClaudeCodeRemoveArguments (Tests McpClientSetupRemoveTests) | schon da | Codex gibt es auf Windows nicht |
| #375 | Offline-Chat: Gemma 4 26B | - | bewusst anders | §8: keine Gemma/mlx |
| #376 | On-Device Qwen3-ASR und -TTS (mlx-audio) | - | bewusst anders | §8: Offline = Parakeet (sherpa-onnx) + Qwen-GGUF (llama.cpp) |
| #377 | Glossar: einwortige falsche Schreibung nach Titel | GlossaryPrompt.ReplacingRejectedNameAfterTitle | schon da |  |
| #378 | Dictate Prompt: schlanker Bildschirmkontext, niedriger Denkaufwand | Denkaufwand: GeminiModels.BuiltInThinking (low/minimal) schon da; Feldtext und Fensterbild werden nie gesendet | bewusst anders | Windows schickt nur Auswahl + eigenen Snip (macos-paritaet §3 „Screenshot … Patientendaten“) |
| #379 | Skripte: MCP-Helfer zählen nicht als Release | - | n/a (Mac-Tooling/Docs) |  |
| #380 | Berechtigungen-Hub, ehrlicher Status | - | bewusst anders | macOS-TCC; Windows braucht nichts (WelcomeForm: Mikrofon + Datenschutz-Link) |
| #381 | Einstellungen: Seitenleiste in vier Gruppen | bfcde22, SettingsForm.cs:128 | schon da |  |
| #382 | Einstellungen: Gemini-Vergleichslink raus, Modell-Hinweise | 4bc8efd | schon da |  |
| #383 | Dictate Prompt: VS-Code-Zeilenkopie ohne Auswahl ignorieren | SelectionCopyPolicy (Mac #383) | schon da |  |
| #384 | Chat: GPT-6.1 Terra | OpenAiModels.Prompt: Sol, Luna, 5.4-mini; `gpt-5.6-terra` wird auf Sol gemappt | fehlt | S. Swift: SettingsConfiguration.swift (openaiGPT61Terra, $1/$5, reasoning_effort none, /model terra), ChatModelCommandResolver.swift. Windows: ModelInfo + ChatThinking.OpenAiEffort prüfen. |
| #385 | Offline-Diktat: Klammer auf/zu, Neue Zeile, Großschreibung | 091bc28 | schon da |  |
| #386 | Test: Konfigurationsbericht hermetisch | - | n/a (Mac-Tooling/Docs) |  |
| #387 | Testhost ohne Keychain-Dialog | - | n/a (Mac-Tooling/Docs) |  |
| #388 | Qwen3 1.7B ab 32 GB, README | - | bewusst anders | §8 Qwen3-mlx |
| #389 | Testsuite verkleinert | - | n/a (Mac-Tooling/Docs) |  |
| #390 | Daily-Dev-Instanz | - | n/a (Mac-Tooling/Docs) |  |
| #391 | Skript copy-release-data-to-dev | - | n/a (Mac-Tooling/Docs) |  |
| #392 | Qwen3-Stimmen erstellen/klonen | - | bewusst anders | §8 |
| #393 | Spoken-only-Fixes (Kommas, Klammern, Glossar-Wächter) | b65bd46 | schon da | Glossar-Wächter entfällt (Plan 11.26) |
| #396 | Dictate Live: Umschalt+Return in Chrome, kein Update mitten im Diktat, zweiter Druck ignoriert | c058bfb, b5c9eb9 | schon da |  |
| #397 | Vorlesen über den eigenen OpenAI-Server | - | bewusst anders | §8: kein eigener Server |
| #398 | Qwen3 TTS: Stille kürzen | - | bewusst anders | §8 |
| #399 | Qwen3: Stereo zu Mono | - | bewusst anders | §8 |
| #400 | CONTRIBUTING.md | - | n/a (Mac-Tooling/Docs) |  |
| #401 | AGENTS.md verweist auf CONTRIBUTING | - | n/a (Mac-Tooling/Docs) |  |
| #403 | Fn-Abbruchregel, Setup-Symbol, Mikro nach abgebrochener Freigabe, lokales Dictate Prompt (R170, R175–R177) | HotkeyPressGate (rechte Strg bricht kein Live-Ende ab), TrayIcons-Warnsymbol | schon da | R176/R177 sind Mac-Berechtigung bzw. Cloud-Gate; nicht übertragbar |
| #404 | Qwen3-Diktat, Own-Server-Check, halber Qwen3-Download, Personas | - | bewusst anders | §8 |
| #405 | Glossar-Kürzel stellt Zwischenablage her, Vorlesen/Glossar ignorieren Altinhalt | ein Pfad: ClipboardPaster.CopySelectionAsync (Sequenznummer, Late-Copy-Watch) | schon da | anders gebaut, der Fehler kann nicht auftreten |
| #407 | Meeting-Notizen nach Resume, Google/Trello-Fixes, Chat-Follow | - | bewusst anders | Notizen: #357 (fehlen ganz); Google/Trello: §8; Chat-Follow: #364 schon da |
| #408 | Onboarding-Offline-Gate, Spoken Punctuation, Vorlagen-Kürzel | 4013cd2 (Mac #408) | schon da | Own-Server-Text n/a |
| #409 | MCP-Config-Schreiben, System-Audio neu aufbauen (OpenRouter/translocated n/a) | 4f22d89, d681ea5, eecc7e0 | schon da |  |
| #410 | Meetings offline, MCP-Vorschläge/Undo, Pill-Bildschirm, Sie | 5a7b874, 156b96b | schon da | Anrede n/a (duzt) |
| #411 | Chat-Rendering: Listen-Nummern/Verschachtelung, Tabelle nur mit Trennzeile, Undo im Eingabefeld | ChatMarkdown ist bewusst Teilmenge: zeilenweiser Fence-Scanner da, keine Tabellen, Aufzählung nur „•“ | fehlt | S, niedrig: verschachtelte Aufzählungen, nummerierte Listen. Swift: MarkdownParsing.swift, ReplyBlockBuilder.swift. |
| #412 | Meeting-Fehler R203–R210 | 19324bc, 24ff678 | schon da |  |
| #413 | Abgebrochener Meeting-Chunk zeigt kein Fehler-Popup | LiveMeetingSession.ProcessAsync wirft bei Abbruch weiter, kein ChunkFailed | schon da | anders gebaut |
| #417 | README: Kürzel | - | n/a (Mac-Tooling/Docs) |  |
| #418 | Onboarding: Offline startet den Download sofort | WelcomeForm.StartSpeechDownloadIfNeeded (Kommentar „Mac #418“) | schon da |  |
| #419 | Tests: kein Writing-Style-Panel | - | n/a (Mac-Tooling/Docs) |  |
| #420 | Bedienungshilfen-Liste vor dem Öffnen neu laden | - | bewusst anders | macOS-TCC |
| #421 | Globe-Taste freigeben (TISUpdateFnUsageType) | - | bewusst anders | Fn gibt es nicht (§8) |
| #422 | Nutzungslog lesbar für Tuning-Modell | - | bewusst anders | §8: kein Nutzungslog, keine gespeicherten Diktate |
| #423 | README: MCP | - | n/a (Mac-Tooling/Docs) |  |
| #424 | Chat-Freigabekarte zeigt Diff beim Ändern der App-Anweisungen | Chat hat keine Werkzeuge (kein update_app_instructions) | Feature fehlt auf Windows ganz | L (Chat-Werkzeuge insgesamt). §8 nennt nur „kein Memory/Dienste/Ordner“; Magnus fragen, ob Konfig-Werkzeuge im Windows-Chat gewollt sind. Swift: ChatTools.swift, ChatAgentRunner.swift, ProposalReviewView.swift. Siehe #535, #559. |
| #425 | Leerer Chat: Tipp „Claude/Codex per MCP verbinden“ | kein Tipp im ChatWindow | fehlt | S, niedrig. Swift: MCP/ChatMCPTipView.swift. Nur wenn kein MCP-Client verbunden und nicht offline; × merkt sich. |
| #426 | Prompt-Abschnittslimit relativ zum Standard | McpConfigTools.MaxPromptCharacters = 10 000 | schon da |  |
| #427 | Modelle: vorhandene Gewichte von der Platte importieren | Core/Offline/ModelDownloader.cs ohne Import | anderswo (Session/Bug) | ModelDownloader liegt bei anderer Session. Windows-Gegenstück „Modell aus Ordner/LM-Studio-Cache übernehmen“ (GGUF), L |
| #428 | README kurz, Handbuch in docs/guide.md | - | n/a (Mac-Tooling/Docs) |  |
| #429 | Dictate-Prompt-Seite nennt Erkennung und Glossar | 4bc8efd | schon da |  |
| #430 | MCP: get_configuration nennt ausgeschaltete Funktionen (`not_yet_enabled`) | McpConfigTools.GetConfiguration ohne dieses Feld | fehlt | S, niedrig. Swift: MCPFeatureHints.swift, MCPTuneRecipe.swift. Auf Windows nur Auto-Einfügen und „Screenshots mitsenden“ sinnvoll. |
| #433 | Lange-Aufnahme-Dialog entfällt bei schon transkribierten Stücken | - | bewusst anders | macos-paritaet §1: kein Kostendialog, 30 min Limit, verarbeitet automatisch |
| #434 | Callouts für Fn/🌐 und Bedienungshilfen | - | bewusst anders | Mac-Hinweise |
| #435 | Benchmark Glossar-Echo | - | n/a (Mac-Tooling/Docs) |  |
| #436 | Glossar-Echo in Stichwort-Reihenfolge abfangen | Commit „strip a glossary echo appended in keywords order“ (Mac #436) | schon da |  |
| #437 | Silero-VAD vor Cloud-Diktat | Core/SileroVad.cs, SpeechActivityGate.cs (Port vorhanden) | schon da | Bug-Bereich Silero-Gate/TranscribeOnlineAsync liegt bei anderer Session: nicht anfassen |
| #438 | Vorlesen: Leertaste pausiert, ←/→ springen, Esc stoppt | ReadAloudOverlay: nur Knöpfe, nimmt nie den Fokus (WS_EX_NOACTIVATE) | fehlt | M. Swift: RecordingIndicatorWindow.swift (keyOwner, keySkipInterval). Windows ohne Fokusnahme nur über Tastatur-Hook, solange es spricht; mit Overlay-Session abstimmen (ReadAloudOverlay.cs, nicht RecordingOverlay.cs). |
| #439 | Qwen3-Vorlesen: Vorpuffer | - | bewusst anders | Kein Qwen3-TTS; Windows hat SpeechPlaybackBuffer |
| #440 | Karte: die drei meistgenutzten Prompts | RecordingOverlay.cs | anderswo (Session/Bug) | Overlay-Session; zählt Nutzung der letzten 30 Tage, Windows hat kein Nutzungslog (eher bewusst anders) |
| #441 | Neues Kürzel „Vorlesen mit Anweisung“ (Dictate-Prompt-Ergebnis wird vorgelesen) | kein HotkeyAction dafür (HotkeySlots.cs) | fehlt | M, mittel. Swift: MenuBarController.swift (toggleReadAloudPrompting, startDictatePromptCapture), ShortcutConfig, KeyboardShortcutsSection. Windows: neuer HotkeySlot (ab Werk frei), Prompt-Pipeline liefert Text an ReadAloudController statt Einfügen. Marker-Fix 232324ea (nichts markiert) ist schon da. |
| #442 | Refactor: toter Code, PKCE-Helfer | - | n/a (Mac-Tooling/Docs) |  |
| #443 | Geleakte Gemini-Thought-Blöcke entfernen | 4d17981 | schon da |  |
| #444 | Chat-Schriftart auch in Notizen, Zusammenfassung, Überschriften, Tabellen | s. #355 | fehlt | S, nur zusammen mit #355. Swift: MarkdownParsing.swift, ReplyBlockBuilder.swift |
| #445 | Diktat: Namen aus dem fokussierten Feld als Schreibhilfe für GPT Transcribe (opt-in) | FocusedTextContext liest nur klassische Edit-Felder | fehlt | M, niedrig–mittel. Swift: DictationFieldHint.swift, MenuBarController.swift, SpeechService.swift, SpeechToTextSettingsTab.swift. Nur Edit/RichEdit lesbar; Browser/Citrix per UI Automation wäre L. |
| #446 | Chat: nicht verbundener Google/Trello/Mail-Dienst melden | - | bewusst anders | §8: keine Chat-Dienste |
| #447 | Testhost überspringt Modell-Migration | - | n/a (Mac-Tooling/Docs) |  |
| #448 | MCP: veraltete Client-Token selbst heilen | McpService.TryRewriteClientUrls, McpClientSetup.RewriteOwnUrl | schon da | bei Port-/Token-Wechsel |
| #449 | Dictate Prompt: Bildschirmtext nur mit Einwilligung | - | bewusst anders | Windows sendet nie Feldtext/Fensterbild (siehe #378) |
| #451 | Build-Lock-Umgehung geschlossen | - | n/a (Mac-Tooling/Docs) |  |
| #452 | dev-daily: PATH | - | n/a (Mac-Tooling/Docs) |  |
| #453 | Live: gesprochener Doppelpunkt trotz „Doppelgun“ | 4f82ae6 | schon da |  |
| #454 | Einwilligung Bildschirmkontext schaltet Schalter nicht um | - | bewusst anders | Mac-Dialog, kein Gegenstück |
| #456 | Standard-Prompts gestrafft | 86e3e07 | schon da |  |
| #457 | Dictate Prompt: Planungsblock vor dem Einfügen entfernen | 27822b5 (TextCleanup.StripLeakedPlanning) | schon da | Abkürzungs-Fix #538 fehlt noch |
| #458 | Nutzungslog: keine Testzeilen, Latenz | - | bewusst anders | §8 kein Nutzungslog |
| #459 | Dictate Prompt: Antworten mit eigenen Anfrage-Labels oder Überlänge nicht einfügen; schlecht erkannte Anweisung ablehnen | ReplyGuards hat nur AnswerSuspiciouslyShort | fehlt | M, hoch (Schutz vor ~11 000 Zeichen Denktext über der Auswahl). Swift: DictatePromptGuards.swift, PromptConversationHistory.swift, SpeechErrorFormatter.swift. Alle Anbieter: Zeile beginnt mit „SELECTED TEXT FROM CLIPBOARD“/„VOICE INSTRUCTION:“ oder Länge > max(2000, 8×(Auswahl+Anweisung)) → wiederholbarer Fehler, nichts eingefügt. Nur lokaler Pfad: Anweisung < 3 Buchstaben oder SRT-Zeitstempel ablehnen; ≤5 Wörter mit ≥0,8 Ähnlichkeit zu genau einer eigenen Schnellanweisung ersetzen. |
| #460 | Chat legt Termine/Aufgaben ohne Rückfrage an | - | bewusst anders | §8: keine Chat-Dienste |
| #461 | Screenshot: Hinweis, wenn kein Speicherordner gewählt | - | bewusst anders | Windows speichert Screenshots nie in einen Ordner (nur Zwischenablage) |
| #462 | Live: Mengenzahlen als Ziffern, GdB-Kleben | 2e0cdd4, 63e3c6e | schon da |  |
| #463 | Nur-Diktieren-Modus, Teil 1: Schalter, Sperren, Erststart-Standards | kein Modus (`DictationOnly` kommt nicht vor) | Feature fehlt auf Windows ganz | L (gesamt mit #464, #504, #526, Teil #511). Swift: DictationOnlyMode.swift, MenuBarController.swift, SettingsView.swift, Shortcuts.swift, KeyboardShortcutsSection.swift. Blendet Chat/Meeting/Vorlesen/Tabs/Menüs aus; Magnus muss Zielgruppe bestätigen. |
| #464 | Nur-Diktieren-Modus, Teil 2: Einstellungsschalter und Tour-Pfad | - | Feature fehlt auf Windows ganz | siehe #463; Swift: DictationOnlyOnboarding.swift, DictationOnlySteps.swift, FullApp.swift, WelcomeSteps.swift |
| #465 | Testprofile für Agenten | - | n/a (Mac-Tooling/Docs) |  |
| #466 | Docs: Fable-Regel | - | n/a (Mac-Tooling/Docs) |  |
| #467 | Chat: standardmäßig mitscrollen | Standard wie Mac (a6ec2a5: folgt, wenn am Ende) | schon da | Schalter dazu fehlt, siehe #368 |
| #468 | Fester Prompt-Kern: Nutzer ergänzen Anweisungen statt die Dictation-/Dictate-Prompt-Prompts zu ersetzen | Windows ersetzt den Prompt komplett; tests/PromptsTests.cs: „the fixed-core architecture is not ported“ | Feature fehlt auf Windows ganz | L, mittel–hoch (verhindert kaputte Prompts wie in #455). Swift: AppConstants.swift (Abschnitte „Dictation Additions“), SystemPromptsStore.swift, ChatTools.swift, MCPConfigTools/MCPProposalStore, SettingsConfiguration. Betrifft Settings-UI, MCP-Tools, Migration vorhandener Texte, Konfigurationsbericht; Magnus-Entscheidung. |
| #469 | Chat: „Immer erlauben“ für Kalender/Aufgaben/Trello | - | bewusst anders | §8 keine Chat-Dienste |
| #470 | Pill nennt das aktive Eingabegerät | RecordingOverlay.cs | anderswo (Session/Bug) | Overlay-Session; Mikrofonwahl und Pegel hat Windows schon (#527) |
| #471 | Prompt-Fingerprints (Mac-intern) | - | n/a (Mac-Tooling/Docs) |  |
| #472 | Glossar: Startlisten für Fachbegriffe (Orthopädie, Psychiatrie, Gutachten) mit einem Klick | keine Startlisten (GlossaryEdit nur Einzelbegriff) | fehlt | S, hoher Nutzen für Praxis-Einrichtung (auch Direkt-Commit b0def6cd). Swift: GlossaryStarterLists.swift, SpeechToTextSettingsTab.swift. 3 Listen à 15 Begriffe, neue Zeilen, Vorhandenes überspringen, 1 200-Zeichen-Budget, Statuszeile „n hinzugefügt, m schon da, k passten nicht“. |
| #473 | Vokabel-Wächter: bedeutungsdrehende Ersetzungen | - | bewusst anders | Plan 11.26: kein CTC-Rescorer, nur Hotwords (Score 2,5) |
| #474 | Fn: Verwerfen vor Mikrostart strandet nicht | HotkeyPressGate, RightCtrlDictationToggle (+Tests) | schon da | anders gebaut (rechte Strg) |
| #475 | Vorlesen: Return pausiert, Tasten überleben Klick auf die Pill | s. #438 | fehlt | gehört zu #438 |
| #476 | Vorlesen: Pause lässt den Tasten-Fokus nicht los | s. #438 | fehlt | gehört zu #438 |
| #477 | Vorlesen: Pfeiltasten 5 s, Knöpfe 10 s | Knöpfe ±10 s da (Command.Back/Forward) | fehlt | gehört zu #438 (5 s für die Tasten) |
| #478 | Pill auch über Vollbild-Apps | RecordingOverlay.cs (Mac-Look seit 0.7.14, 2603e7c) | anderswo (Session/Bug) | Overlay-Session |
| #479 | /improve: Review-Modell als eigener Chip | - | bewusst anders | §8 Smart Improvement (/improve ist dessen Nachfolger und hängt an Chat-Werkzeugen) |
| #480 | Loop-Jobs: Mail nur bei Fehler | - | n/a (Mac-Tooling/Docs) |  |
| #481 | gpt-4o-transcribe und gpt-4o-mini-tts zurückgezogen | 2bfae9d (Mac #481) | schon da |  |
| #482 | Live: gesprochener Schrägstrich zwischen Zahlen | 3ac315a | schon da |  |
| #483 | Live: weiche Maximallänge + Neuabsatz | b1dc7c9, db028e9 | schon da |  |
| #484 | Live: „ich“ klein nach Komma | 01d6fe0 | schon da |  |
| #485 | Problem melden: letzte Prompt-Antwort und Vorlesen | Branch port/mac-feedback-mail | erledigt (Branch port/mac-feedback-mail) | |
| #486 | Modelle: vor dem Download Import anbieten | Core/Offline/ModelDownloader.cs ohne Import | anderswo (Session/Bug) | ModelDownloader liegt bei anderer Session. Windows-Gegenstück „Modell aus Ordner/LM-Studio-Cache übernehmen“ (GGUF), L |
| #487 | Meetings: Titel aus der Zusammenfassung, auch ohne offenes Chatfenster | MeetingStore.SetTitle existiert, wird nirgends aufgerufen; Titel bleiben „Meeting <Zeitstempel>“ | Feature fehlt auf Windows ganz | M. Swift: MeetingTitleService.swift (Modell = Meeting-Zusammenfassung, offline lokal, nie über manuelle Umbenennung schreiben). Windows: nach der Zusammenfassung ein kurzer Aufruf + SetTitle. |
| #488 | Vorlesen: gpt-realtime-2.1-mini (OpenAIs TTS-Nachfolger über Realtime-WebSocket) | ReadAloud.cs: gpt-4o-mini-tts nur zurückgezogen; Kommentar „no speech-endpoint successor“ ist falsch | fehlt | L, mittel–niedrig (nur OpenAI-Schlüssel ohne Gemini). Swift: SpeechService.swift (Realtime-Sitzung, s16le 24 kHz), AppConstants, ModelSelectionReconciler. WebSocket-Client + PCM in TtsPlayer; Migration der gespeicherten Auswahl. |
| #489 | Karte: Schnellaktionen behalten feste Position | RecordingOverlay.cs (Mac-Look seit 0.7.14, 2603e7c) | anderswo (Session/Bug) | Overlay-Session |
| #492 | MCP: freier Port, wenn 47823 belegt | 285bdd1, 5f1a2f3 | schon da |  |
| #493 | Live: Punkt kleben, GdB, „Neue Absatz“ | 63e3c6e | schon da |  |
| #494 | Benchmark Long-Form-Replay | - | n/a (Mac-Tooling/Docs) |  |
| #495 | Kürzel: Konfliktzeile neu prüfen, wenn andere Taste sich ändert | HotkeySlots.Conflict (eine Prüfung für alle Kürzel inkl. Vorlagen) | schon da | UI-Neubewertung der Konfliktzeile nicht einzeln geprüft |
| #496 | MCP: Vorlagen-Werkzeuge und vollständige Kürzelliste in get_configuration | get_configuration.hotkeys nennt nur dictate, dictate_prompt, add_to_glossary; Vorlage nur lesbar (TODO McpConfigTools:247) | fehlt | Kürzelliste S, Vorlagen-Werkzeuge M. Swift: MCPConfigTools.swift, MCPSettingsCatalog.swift, MCPChangeLog/MCPProposalStore, TemplatesSettingsTab.swift. |
| #497 | Kürzel-Konflikt kennt Vorlagen-Tasten | HotkeySlots.Conflict, HotkeyAction.Template/TemplatePicker | schon da |  |
| #498 | Glossar-Kürzel lernt die falsch gehörte Form (`Begriff (not "gehört")`) | GlossaryEdit.TryAdd fügt nur den Begriff an; RecentTexts (letztes Diktat im Speicher) ist da | fehlt | M. Swift: GlossaryFastLearner.swift, GlossaryMisheard.swift, MenuBarController.swift. Nur bei einem Wort, gleicher Anfangsbuchstabe, Ähnlichkeit ≥0,5, keine Beugung, Diktat < 10 min; Meldung zeigt das Paar. Parakeet-Wächter-Teil entfällt (11.26), neue Zeile statt Komma gibt es schon. |
| #499 | Glossar: nicht-wortförmige falsche Schreibungen überall ersetzen, ähnliche Varianten | GlossaryPrompt.CorrectRejectedSpellings: einwortig nur nach Titel | fehlt | M–L (braucht Wörterbuch Deutsch/Englisch; Windows hat keinen NSSpellChecker: ISpellChecker oder eigene Liste). Swift: GlossaryFastLearner.isKnownWordInDictationLanguages, SpeechService.correctingRejectedSpellings. Gilt für alle Engines. |
| #500 | Parakeet: Booster ersetzt kein echtes Wort | - | bewusst anders | Plan 11.26: kein Booster, nur Beam-Search-Hotwords |
| #502 | Live wählen setzt Parakeet Ultra und gibt das alte Modell zurück | Modus-Seite zeigt nur den Hinweis „Parakeet verwenden“ (SettingsForm.cs:1641) | fehlt | S. Swift: SpeechToTextSettingsTab.swift (modelBeforeDictateLive). Mit #570: bei Cloud-Modell nicht ersetzen. |
| #503 | Review-Fixes: Meeting-Chunk bei Abbruch, Vorlesen-Tasten | Meeting: LiveMeetingSession wirft bei Abbruch weiter | schon da | Teil Vorlesen-Tasten fehlt, siehe #438 |
| #504 | Nur-Diktieren: Wiederherstellung, MCP folgt dem Modus (R236, R232, R227) | - | Feature fehlt auf Windows ganz | siehe #463 |
| #505 | Glossar-Schreiber unter einem Budget serialisiert (R223, R225) | Kürzel (UI-Thread) und MCP add_glossary_terms schreiben getrennt | unklar | ein Prozess, wahrscheinlich kein Wettlauf; MCP-Threads nicht geprüft. S, niedrig |
| #506 | Modell-Import: Ordner je Store, nicht während Download | Core/Offline/ModelDownloader.cs ohne Import | anderswo (Session/Bug) | ModelDownloader liegt bei anderer Session. Windows-Gegenstück „Modell aus Ordner/LM-Studio-Cache übernehmen“ (GGUF), L |
| #507 | Äh/Ähm in jedem Transkript entfernen | 3cfd8da (TextCleanup.RemovingFillers) | schon da |  |
| #508 | Lokales Dictate Prompt: kyrillische Lookalikes falten | 51668d7 | schon da |  |
| #510 | Dictate Prompt: reine Füllwort-Anweisung bearbeitet nichts | 3907918, c74a562 | schon da |  |
| #511 | UI/UX-Review: echte Taste im Ausprobieren, Einrichtungsmenü, ⌃⌥⇧⌘ | 4bc8efd (Tray während Einrichtung, rechte Strg unter Diktieren), WelcomeForm mit echtem Kürzel | schon da | teilweise; Nur-Diktieren-Teile siehe #463 |
| #512 | run-tests stellt Dev-Einstellungen wieder her | - | n/a (Mac-Tooling/Docs) |  |
| #513 | Dictate-Prompt-Schnellaktionen auch im laufenden Meeting | RecordingOverlay.cs, TrayApp | anderswo (Session/Bug) | Overlay-Session; ob Dictate Prompt im Meeting auf Windows erlaubt ist, nicht geprüft |
| #514 | Refactor: Hub-Snapshot-Reihenfolge | - | n/a (Mac-Tooling/Docs) |  |
| #515 | Refactor: returnAction | - | n/a (Mac-Tooling/Docs) |  |
| #516 | Refactor: Log-Fenster-Argumente | - | n/a (Mac-Tooling/Docs) |  |
| #518 | Statusmenü: Vorschlagszeile mit Punkt am Symbol | McpApprovalDialog öffnet MCP-Vorschläge direkt als Fenster | unklar | Windows hat weder Punkt am Tray-Symbol noch Vorschlagszeile; ob ein liegen gebliebener Vorschlag wiederfindbar ist, nicht geprüft |
| #519 | Writing-Style-Untertitel | - | bewusst anders | §8 kein Writing Style |
| #520 | Auto-Paste: Warnung bei ersetztem Bundle | - | bewusst anders | Mac-Bundle |
| #521 | Praxis-Playbook v1 (Mac-Docs) | - | n/a (Mac-Tooling/Docs) |  |
| #522 | MCP: gebündelte App-Docs lesen (list/read_whisper_shortcut_doc) | kein Docs-Werkzeug; Windows bündelt keine Docs | fehlt | M, niedrig (Inhalt müsste für Windows geschrieben werden). Swift: ChatTools.swift (availableDocs), MCPConfigTools.swift, MCPDispatcher.swift. |
| #523 | Praxis-Playbook v1.1 | - | n/a (Mac-Tooling/Docs) |  |
| #524 | Services-Gate, Import-Ordner, Prompt-Konvertierung (R243–R246) | - | bewusst anders | Mac-Services; Import siehe #427 |
| #525 | Zahlwörter lassen Fließtext in Ruhe (R248); ein Offline-Abschluss auch für normales Diktat (R247) | GermanNumberWords hat noch die Regel „Ziffern daneben“ („Zimmer 12 drei Betten“); GdB/Zahlen/Punkt-Teilung nur in LiveDictation.FinishPiece | fehlt | S–M. Swift: GermanNumberWords.swift, OfflineTranscriptFinisher.swift, MenuBarController.swift. 1) Prosa-Fälle („Seit 2020 zwei Operationen“ bleibt, „um vierzehn Uhr dreißig“ → „14 Uhr 30“) + Tests; 2) normales Offline-Diktat durch denselben Abschluss. |
| #526 | Fixes R249–R252: Legacy-Prompt-Regel, Konten im Nur-Diktieren, Modell-Hinweis, Advisor-Tabs | - | Feature fehlt auf Windows ganz | siehe #463 (R249 hängt an #468) |
| #527 | Mikrofonwahl in den Einstellungen | Microphones.cs, Diktieren → Mikrofon mit Pegeltest | schon da | Windows war schon weiter |
| #528 | Einstellungen → Allgemein: Einführung öffnen | Einführung nur im Tray (SettingsForm.cs:879) | fehlt | S. Swift: WelcomeTourSection.swift, GeneralSettingsTab.swift. Abschnitt mit Knopf „Einführung öffnen …“. |
| #529 | Docs: mehrere Formate pro Vorlage | - | n/a (Mac-Tooling/Docs) |  |
| #530 | Plan: Standardwerte-Inventar | - | n/a (Mac-Tooling/Docs) |  |
| #532 | Writing Style: Web-Mail/Chat in Browsern | - | bewusst anders | §8 |
| #534 | Dictate Prompt: gesendeten Bildschirmkontext auf der Pill zeigen | RecordingOverlay.cs | anderswo (Session/Bug) | Overlay-Session; Windows sendet keinen Bildschirmkontext automatisch |
| #535 | Chat-Werkzeug-Richtlinien (Freigabe für forget_about_user …) | - | Feature fehlt auf Windows ganz | hängt an Chat-Werkzeugen (#424) |
| #536 | Refactor: keyOwner | - | n/a (Mac-Tooling/Docs) |  |
| #537 | Nutzungsdaten speichern: neu an, in der Tour | - | bewusst anders | §8 |
| #538 | Planungsblock-Entfernung schneidet Antworten nicht an Abkürzungen an | TextCleanup.GluedAnswerStart schneidet an jedem Satzzeichen + Großbuchstabe | fehlt | S. Swift: LeakedThoughtStrip.swift (gluedAnswerStart: Wort davor ≥3 Zeichen, endet klein). „Task: Ship it to the U.S. office“ wird sonst „S. office“. |
| #539 | Refactor: isOwnAppFrontmost | - | n/a (Mac-Tooling/Docs) |  |
| #540 | Refactor: ConsentToggleRow | - | n/a (Mac-Tooling/Docs) |  |
| #541 | Refactor: ein Prompt-Startpfad | - | n/a (Mac-Tooling/Docs) |  |
| #543 | README: Hero-Animation | - | n/a (Mac-Tooling/Docs) |  |
| #544 | Pill auf dem Bildschirm, an dem man arbeitet | RecordingOverlay.cs (Mac-Look seit 0.7.14, 2603e7c) | anderswo (Session/Bug) | Overlay-Session |
| #546 | Pill frei verschiebbar, merkt Position | RecordingOverlay.cs (ReadAloudOverlay ist schon verschiebbar) | anderswo (Session/Bug) | Overlay-Session |
| #547 | MCP: ein Token für alle Builds (Keychain-Gruppen) | - | bewusst anders | Mac-Keychain; Windows hat DPAPI und einen Build |
| #549 | Ein Feedback-Weg: Problem melden mit Konfiguration als Datei | Branch port/mac-feedback-mail | erledigt (Branch port/mac-feedback-mail) | |
| #550 | Chat-Freigabekarte: „Immer erlauben“ gleichwertig | - | bewusst anders | §8 Chat ohne Dienste |
| #551 | Chat: falsche Ergebnisse als Konfig-Ursache diagnostizieren | - | bewusst anders | hängt am Nutzungslog (§8) |
| #552 | Nutzungslog: Cursorkontext/History je Prompt-Zeile | - | bewusst anders | §8 kein Nutzungslog (Teil im Problem-Bericht: #555) |
| #553 | Dictate Prompt: größere Offline-Modelle ohne Stern anbieten | OfflineCatalog hat Qwen3.5 9B und Qwen3 4B; Empfehlung ab 16 GB ist 9B | unklar | Beide schon wählbar. Mac-Audit (34/36 vs. 28/36 Regelchecks; 9B formalisiert, übersetzt EN→DE) empfiehlt für Dictate Prompt die 4B. Entscheidung nötig (auch 23761f33). Gemma entfällt (§8). |
| #554 | Auto-Merge und Nightly-Release | - | n/a (Mac-Tooling/Docs) |  |
| #555 | Problem-Bericht: Cursorkontext und frühere Runden | Branch port/mac-feedback-mail | erledigt (Branch port/mac-feedback-mail) | |
| #556 | Wöchentliche App-Store-Einreichung | - | n/a (Mac-Tooling/Docs) |  |
| #557 | AGENTS.md: App Store | - | n/a (Mac-Tooling/Docs) |  |
| #558 | Plans: Merge-Train | - | n/a (Mac-Tooling/Docs) |  |
| #559 | Chat: Warnung, bevor ein schwaches Modell die Konfiguration ändert | - | Feature fehlt auf Windows ganz | hängt an Chat-Werkzeugen (#424); Direkt-Commit c4b53f54 |
| #560 | Pill: Griff zum Verschieben | RecordingOverlay.cs (Mac-Look seit 0.7.14, 2603e7c) | anderswo (Session/Bug) | Overlay-Session |
| #561 | eval-improve.py | - | n/a (Mac-Tooling/Docs) |  |
| #562 | Gemini 4 Hydrogen und Neon, Argon auf Neon umstellen | GeminiModels.Prompt bietet noch gemini-4-argon (kostet ab 2026-12-01 $4/$20) | fehlt | S, zeitkritisch. IDs gemini-4-hydrogen ($1,50/$7,50), gemini-4-neon ($3/$15), nicht fürs Diktat. Swift: SettingsConfiguration.swift, TranscriptionModels.swift (migrateLegacyPromptRawValue), ChatModelCommandResolver.swift. Windows: ModelInfo-Liste, Renamed Argon→Neon, BuiltInThinking/RejectsMinimal, Chat-Befehle (Chat.cs). Direkt-Commits 2466fd10, acf1808d, c4b53f54. |
| #565 | Merge-Train: Runner-Ausfall | - | n/a (Mac-Tooling/Docs) |  |
| #566 | release-nightly: Tag | - | n/a (Mac-Tooling/Docs) |  |
| #567 | Wechsel zum Dev-Build | - | n/a (Mac-Tooling/Docs) |  |
| #568 | stable-promote stündlich | - | n/a (Mac-Tooling/Docs) |  |
| #569 | Wechsel zum Dev-Build (Wartezeit) | - | n/a (Mac-Tooling/Docs) |  |
| #570 | Dictate Live mit Cloud-Modellen | LiveDictation.UnavailableReason: nur lokal (Parakeet) | fehlt | L. Swift: LiveDictation.swift (unavailableReason, .noAPIKey), LiveDictationSession.swift (decodesConcurrently: Stücke parallel hochladen, Einfügen in Sprechreihenfolge), MenuBarController.swift, SpeechToTextSettingsTab.swift. Abhängigkeit: Cloud-Pfad = TranscribeOnlineAsync/Silero-Gate (andere Session). Direkt-Commits ed5f5762, afa7366a. |
| 4701520f | Feedback-Sprache (Direktcommit) | Branch port/mac-feedback-mail | erledigt (Branch port/mac-feedback-mail) | |
| 232324ea | Dictate Prompt ohne Markierung erfindet nichts (Marker-Text) | Prompts.DictatePromptComposeMarker ist wortgleich (86e3e07/Mac #456 Abgleich) | schon da | Pill-Hinweis „eigenes Fenster vorne“ gehört zur Overlay-Session |
| b0def6cd | Glossar-Startlisten (Direktcommit) | siehe #472 | fehlt | S, wie #472 |
| ed5f5762, afa7366a | Dictate Live mit Cloud-Modellen (Direktcommits) | siehe #570 | fehlt | L, wie #570 |
| 2466fd10, acf1808d, c4b53f54 | Gemini 4 Hydrogen/Neon, Argon abgelöst; Neon als Chat-Empfehlung (Direktcommits) | siehe #562 | fehlt | S, wie #562 (c4b53f54 hängt an Chat-Werkzeugen, #559) |
| 23761f33 | Größere Offline-Modelle für Dictate Prompt (Direktcommit) | siehe #553 | unklar | Entscheidung 4B/9B nötig, wie #553 |
