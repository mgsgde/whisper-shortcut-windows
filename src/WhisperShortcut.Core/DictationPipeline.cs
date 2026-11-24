using System.Diagnostics;

namespace WhisperShortcut.Core;

/// <summary>
/// Everything one finished recording needs to be processed, handed to <see cref="DictationPipeline.RunAsync"/>. The pipeline
/// reads nothing else about the run: no shared "current take". A snapshot taken when processing starts.
/// </summary>
/// <param name="Kind">Dictate, Prompt or Template (Live never comes here).</param>
/// <param name="Wav">The recording; empty for a quick instruction or template run on a selection alone.</param>
/// <param name="Selection">The text marked while recording, or null.</param>
/// <param name="IsRetry">A replay: its selection is finished, a late Ctrl+C no longer matters.</param>
/// <param name="LateCopyArrived">Asked when needed: did a Ctrl+C come after the selection deadline (a selection we never read)?</param>
public sealed record TakeInput(RecordingKindName Kind, byte[] Wav, double Seconds, string? Selection, bool IsRetry,
    DictationTemplate? Template = null, bool NoNewTextTemplate = false, string? QuickAction = null,
    ScreenshotImage? Screenshot = null, Func<bool>? LateCopyArrived = null);

/// <summary>
/// What a run produced. Either <see cref="Text"/> to paste (with the words nobody dictated and the recognizer's raw text),
/// or <see cref="Stop"/>: the run has nothing to do and this is what to tell the user (the text is then empty).
/// </summary>
public sealed record TakeResult(string Text, IReadOnlyList<string> Unsupported, string? RawText = null, Presentation? Stop = null)
{
    public bool HasUnsupported => Unsupported.Count > 0;

    public static TakeResult Done(string text, IReadOnlyList<string>? unsupported = null, string? rawText = null) =>
        new(text, unsupported ?? Array.Empty<string>(), rawText);

    public static TakeResult Stopped(Presentation stop, string? rawText = null) =>
        new("", Array.Empty<string>(), rawText, stop);
}

/// <summary>What the pipeline needs from the app: engines, settings values, the prompt memory. Implemented by TrayApp.</summary>
public interface IPipelineHost
{
    /// <summary>Test mode (--fake-gemini): fixed texts instead of the recognizer and the language model.</summary>
    bool FakeGemini { get; }

    string PromptModeSystemPrompt { get; }
    string DictatePromptTemplate { get; }
    Engine TextEngine { get; }
    IReadOnlyList<string> QuickActions { get; }

    /// <summary>Longest recording the Dictate Prompt request takes inline, in seconds.</summary>
    double PromptInlineLimitSeconds { get; }

    Task<string> TranscribeAsync(TranscribePurpose purpose, byte[] wav, CancellationToken ct);
    PunctuationMode PunctuationFor(TranscribePurpose purpose);
    string Backstops(TranscribePurpose purpose, string raw);

    /// <summary>The text provider of prompts, templates and quick instructions; reports "loading" through <paramref name="progress"/> while the local model is not up.</summary>
    ITextProvider PromptProvider(IProgress<string> progress);

    /// <summary>Gemini hears the instruction itself (inline audio).</summary>
    Task<string> PromptWithAudioAsync(byte[] wav, string? selection, string systemPrompt, ScreenshotImage? image,
        IReadOnlyList<PromptTurn>? history, CancellationToken ct);

    IReadOnlyList<PromptTurn>? HistoryFor(string? selection);
    void RememberPrompt(string? selection, string instruction, string response, string? reportInstruction = null);
}

/// <summary>An <see cref="IProgress{T}"/> that runs the callback on the calling thread, in order (Progress&lt;T&gt; posts).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// The processing of one stopped recording: dictation, Dictate Prompt (with quick instructions) and templates. Pure of UI:
/// every "nothing to do" exit is a <see cref="TakeResult.Stop"/> the caller shows in one place, every failure is an exception
/// for <see cref="RunOutcomes.ForFailure"/>. What the run needs is in the <see cref="TakeInput"/> it is given.
/// </summary>
public sealed class DictationPipeline(IPipelineHost host)
{
    private const string NoInstructionText = "In der Aufnahme war keine Anweisung zu hören. Sprich sie noch einmal.";

    public async Task<TakeResult> RunAsync(TakeInput input, IProgress<string> progress, CancellationToken ct)
    {
        var run = new Run(host, input, progress);
        var result = input.Kind switch
        {
            RecordingKindName.Dictate => await run.DictateAsync(ct),
            RecordingKindName.Template => await run.TemplateAsync(ct),
            _ => await run.PromptAsync(ct),
        };
        ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Speech gate for a recording or chunk going to Gemini or OpenAI; the file source of --test-wav passes through here too.</summary>
    public static bool HasSpeech(byte[] wav)
    {
        var level = SpeechGate.Analyze(wav);
        if (SpeechGate.HasSpeech(level)) return true;
        Log.Info($"No speech (peak {level.PeakDbfs:0} dBFS, loud frames {level.LoudFrames}/{level.Frames}), request skipped");
        return false;
    }

    private static TakeResult NoSpeech() => TakeResult.Stopped(
        Presentation.Notice(UserCopy.NoSpeechTitle, UserCopy.NoSpeechText, Severity.Info));

    private static TakeResult NoInstruction() => TakeResult.Stopped(
        Presentation.Notice(UserCopy.NoSpeechTitle, NoInstructionText, Severity.Info));

    private static TakeResult NothingSelected() => TakeResult.Stopped(
        Presentation.Notice(UserCopy.NothingSelectedTitle, UserCopy.NothingSelectedText, Severity.Info) with { ErrorSound = true });

    /// <summary>One run's state: its input and the words its reply was found to add.</summary>
    private sealed class Run(IPipelineHost host, TakeInput input, IProgress<string> progress)
    {
        private IReadOnlyList<string> _unsupported = Array.Empty<string>();

        private TakeResult Done(string text, string? raw = null) => TakeResult.Done(text, _unsupported, raw);

        public async Task<TakeResult> DictateAsync(CancellationToken ct)
        {
            var seconds = input.Seconds;
            var punctuation = host.PunctuationFor(TranscribePurpose.Dictation);
            string raw = host.FakeGemini
                ? host.Backstops(TranscribePurpose.Dictation, "das ist ein test diktat aus der virtuellen maschine")
                : await host.TranscribeAsync(TranscribePurpose.Dictation, input.Wav, ct);
            string text = TextCleanup.DiscardImplausible(
                TextCleanup.ForDictation(raw, addTrailingPeriod: punctuation == PunctuationMode.Automatic, audioSeconds: seconds), seconds);
            // A chatbot answer ("please provide the audio") or our own instruction is no dictation.
            if (TextCleanup.LooksLikeRefusal(text)) text = "";
            Log.Info($"Dictate: {raw.Length} chars raw, {text.Length} chars after cleanup, punctuation {punctuation}");
            return text.Length == 0 ? TakeResult.Stopped(NoSpeech().Stop!, raw) : Done(text, raw);
        }

        public async Task<TakeResult> PromptAsync(CancellationToken ct)
        {
            var (wav, seconds, selection) = (input.Wav, input.Seconds, input.Selection);
            // A Ctrl+C that came after the 2 s deadline means there is a selection we never read: writing a
            // new text now would replace it. (A retry has its own, finished selection.)
            ThrowIfLateCopy("Prompt");
            Log.Info($"Prompt: {(selection == null ? "no selection (compose)" : $"selection {selection.Length} chars")}");

            string systemPrompt = DictatePromptText.BuildSystemPrompt(host.PromptModeSystemPrompt,
                input.NoNewTextTemplate ? null : host.DictatePromptTemplate) ?? "";
            if (input.QuickAction is { } quick) return await QuickActionAsync(quick, systemPrompt, ct);

            if (host.FakeGemini)
            {
                return Done(selection == null
                    ? "Angebot (Testmodus ohne Gemini)"
                    : $"Bearbeitet (Testmodus ohne Gemini): {selection.Length} Zeichen markiert");
            }

            // Only Gemini hears the instruction itself, and only up to the Dictate Prompt limit (inline request); a local or
            // OpenAI text model, or a longer take (a template recording replayed without its template), gets it transcribed.
            // +1 s: a recording stopped by the limit can run a buffer past it and must still go inline.
            if (DictatePromptText.UsesTranscript(host.TextEngine, seconds, host.PromptInlineLimitSeconds + 1))
                return await PromptFromTranscriptAsync(systemPrompt, ct);
            if (!HasSpeech(wav)) return NoInstruction();
            progress.Report(UserCopy.EditingText);
            var answer = host.PromptWithAudioAsync(wav, selection, systemPrompt, input.Screenshot, host.HistoryFor(selection), ct);

            // "Mhm.", "Äh." or a lone "0" is not an instruction. With a selection Gemini hears the
            // audio and would edit on a guess, so transcribe in parallel (the no-selection path
            // already did this for a bare edit command) and paste nothing. With or without a selection.
            // With a selection the transcript is only that check, so a failed transcription must not
            // fail an edit Gemini answers fine: log it and go on without the check.
            string instruction = "";
            bool instructionKnown = true;
            if (selection == null)
                instruction = await host.TranscribeAsync(TranscribePurpose.Instruction, wav, ct);
            else
            {
                try { instruction = await host.TranscribeAsync(TranscribePurpose.Instruction, wav, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Log.Warn($"Prompt: filler check transcription failed, continuing without it: {ex.GetType().Name}");
                    instructionKnown = false;
                }
            }
            if (instructionKnown && FillerOnlyInstruction.Refuses(instruction))
            {
                Log.Info("Prompt: instruction was only a filler, refused");
                _ = answer.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe, discard
                return NoInstruction();
            }
            if (selection == null && BareEditCommand.Matches(instruction))
            {
                // Nothing selected and the instruction is only "korrigiere" / "mach das freundlicher":
                // there is nothing to edit, and the model would invent a text.
                Log.Info("Prompt: bare edit command without selection, refused");
                _ = answer.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe, discard
                return NothingSelected();
            }

            // With a selection the transcript is only the filler check. Gemini heard the instruction
            // itself, so the reply is not compared with the transcript.
            string text = Reply(await answer, selection == null ? new[] { instruction, systemPrompt } : null);
            Log.Info($"Prompt: answer {text.Length} chars");
            text = ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
            GuardPromptReply(text, selection, instructionKnown && instruction.Length > 0 ? instruction : null);
            host.RememberPrompt(selection, selection == null ? instruction : PromptConversationMemory.SpokenPlaceholder, text,
                reportInstruction: instructionKnown ? instruction : PromptConversationMemory.SpokenPlaceholder);
            return Done(text);
        }

        /// <summary>
        /// A dictation template run. The recording is transcribed in chunks (so a long dictation is not cut off) and the
        /// transcript goes as text to the text engine, with the template's instructions as the whole system prompt.
        /// A selection is formatted into the template; without one the dictation is.
        /// </summary>
        public async Task<TakeResult> TemplateAsync(CancellationToken ct)
        {
            var (wav, seconds, selection) = (input.Wav, input.Seconds, input.Selection);
            var template = input.Template ?? throw new InvalidOperationException("Template run without a template");
            // Same guard as Dictate Prompt: a late Ctrl+C means a selection we never read.
            ThrowIfLateCopy("Template");

            if (wav.Length > 0 && seconds >= 1.0) progress.Report(UserCopy.Transcribing);
            string transcript = wav.Length == 0 || seconds < 1.0 ? ""
                : await TranscribeBodyAsync(TranscribePurpose.TemplateBody, ct);
            transcript = transcript.Trim();
            if (TextCleanup.LooksLikeRefusal(transcript)) transcript = "";
            Log.Info($"Template: transcript {transcript.Length} chars, selection {selection?.Length ?? 0} chars");
            if (transcript.Length == 0 && !TemplatePrompt.AcceptsEmptyTranscript(selection)) return NoSpeech();

            // Nothing from Dictate Prompt's stored prompt, the template for new texts or the quick instructions.
            string system = TemplatePrompt.SystemPrompt(template.Instructions);
            string user = TemplatePrompt.UserTurn(selection, transcript);

            progress.Report(UserCopy.TemplateBusy(template.DisplayName));
            var sw = Stopwatch.StartNew();
            string raw;
            if (host.FakeGemini)
            {
                raw = "Anamnese: Kopfschmerzen seit drei Tagen\nBefund: unauffällig\nTherapie: Ibuprofen 400 mg";
            }
            else
            {
                raw = await host.PromptProvider(progress).CompleteTextAsync(system, user, input.Screenshot, ct);
            }
            string text = Reply(raw, new[] { transcript, selection }, template.Instructions, keepMarkdown: true);
            Log.Info($"Template: transcript {transcript.Length} chars, selection {selection?.Length ?? 0} chars, answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
            text = ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
            return Done(text);
        }

        /// <summary>
        /// Dictate Prompt with a text model that cannot hear (local, as on the Mac, or OpenAI): the
        /// instruction is transcribed with the chosen speech engine and sent as text with the selection.
        /// </summary>
        private async Task<TakeResult> PromptFromTranscriptAsync(string systemPrompt, CancellationToken ct)
        {
            var selection = input.Selection;
            progress.Report(UserCopy.Transcribing);
            string instruction = (await host.TranscribeAsync(TranscribePurpose.Instruction, input.Wav, ct)).Trim();
            Log.Info($"Prompt via transcript ({host.TextEngine}): instruction {instruction.Length} chars");
            // "Mhm." is the same as nothing heard: do not send it on, and do not paste.
            if (instruction.Length == 0 || FillerOnlyInstruction.Refuses(instruction))
            {
                if (instruction.Length > 0) Log.Info("Prompt: instruction was only a filler, refused");
                return NoInstruction();
            }
            // A mis-transcription ("0", "Oh.", a subtitle timestamp) is no instruction: nothing is sent to the model.
            // A near-miss of exactly one saved quick prompt becomes that prompt (Mac #459).
            if (PromptInstructionGuard.IsNotUnderstood(instruction))
            {
                Log.Warn($"Prompt: transcribed instruction ({instruction.Length} chars) is not an instruction, model not called");
                throw new InstructionNotUnderstoodException();
            }
            if (PromptInstructionGuard.Snapped(instruction, host.QuickActions) is { } snapped && snapped != instruction)
            {
                Log.Info("Prompt: transcribed instruction snapped to a saved quick prompt");
                instruction = snapped;
            }
            if (selection == null && BareEditCommand.Matches(instruction))
            {
                Log.Info("Prompt: bare edit command without selection, refused");
                return NothingSelected();
            }

            progress.Report(UserCopy.EditingText);
            var sw = Stopwatch.StartNew();
            string text = Reply(await PromptTextAsync(selection, instruction, systemPrompt, ct), new[] { instruction, selection, systemPrompt });
            Log.Info($"Prompt via transcript: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
            text = ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
            GuardPromptReply(text, selection, instruction);
            host.RememberPrompt(selection, instruction, text);
            return Done(text);
        }

        /// <summary>
        /// A quick instruction (Ctrl+1–3 or a click while recording). Whatever was said is transcribed and
        /// goes along as text, so this path always has a transcript to check the reply against.
        /// </summary>
        private async Task<TakeResult> QuickActionAsync(string quick, string systemPrompt, CancellationToken ct)
        {
            var (seconds, selection) = (input.Seconds, input.Selection);
            // Under a second nothing was said yet; a recognizer would only guess at the silence.
            if (seconds >= 1.0) progress.Report(UserCopy.Transcribing);
            string transcript = seconds < 1.0 ? ""
                : await TranscribeBodyAsync(TranscribePurpose.QuickActionTranscript, ct);
            var request = QuickActionRequest.Build(quick, selection, transcript);
            Log.Info($"Prompt quick action: transcript {transcript.Trim().Length} chars, selection {selection?.Length ?? 0} chars");
            if (request == null)
            {
                return TakeResult.Stopped(Presentation.Notice("Nichts zum Anwenden",
                    $"Erst Text markieren oder etwas diktieren, dann {QuickActionCard.KeyRange} drücken.", Severity.Info) with { ErrorSound = true });
            }
            var (target, instruction, fromDictation) = request.Value;
            progress.Report(UserCopy.QuickActionBusy(quick));

            string raw;
            var sw = Stopwatch.StartNew();
            if (host.FakeGemini)
            {
                // Test mode: Markdown and an invented drug, so the plain-text cleanup and the warning show.
                raw = "## Anamnese\nKopfschmerzen seit drei Tagen\n\n**Therapie:**\n* Ibuprofen 400 mg";
            }
            else
            {
                raw = await PromptTextAsync(target, instruction, systemPrompt, ct, compose: fromDictation);
            }
            string text = Reply(raw, new[] { transcript, selection, quick, systemPrompt });
            Log.Info($"Prompt quick action: answer {text.Length} chars in {sw.ElapsedMilliseconds} ms");
            text = ReplyGuards.RequireReply(text, "Das Sprachmodell hat keinen Text geliefert.");
            GuardPromptReply(text, fromDictation ? null : target, instruction);
            // A dictated target is a compose turn: remembered without a selection, like the Mac.
            host.RememberPrompt(fromDictation ? null : target, instruction, text);
            return Done(text);
        }

        /// <summary>
        /// Leaked reasoning or a runaway reply is never pasted over the selection (Mac #459). The retry keeps the
        /// recording. <paramref name="instruction"/> is null when the spoken instruction is unknown.
        /// </summary>
        private static void GuardPromptReply(string reply, string? selection, string? instruction)
        {
            var verdict = PromptReplyGuard.Check(reply, selection, instruction);
            if (verdict == PromptReplyGuard.Verdict.Ok) return;
            Log.Warn($"Prompt: reply withheld ({verdict}): {reply.Length} chars, selection {selection?.Length ?? 0} chars, instruction {instruction?.Length ?? 0} chars");
            throw new PromptReplyRejectedException();
        }

        /// <summary>
        /// Cleans a language-model reply for a plain text field and, when there is a transcript to compare
        /// with, collects the words nobody dictated (<see cref="TakeResult.Unsupported"/>, shown after the paste).
        /// The template text is NOT a source: example content in it (a drug, a dose) must still be flagged when
        /// nobody dictated it. Only a template's label words pass (<paramref name="templateInstructions"/>).
        /// A template run keeps its Markdown (<paramref name="keepMarkdown"/>), as on the Mac.
        /// </summary>
        private string Reply(string raw, string?[]? sources, string? templateInstructions = null, bool keepMarkdown = false)
        {
            string text = TextCleanup.NormalizeReply(raw);
            if (!keepMarkdown) text = TextCleanup.StripMarkdown(text);
            if (sources == null || text.Length == 0) return text;
            var unsupported = _unsupported = DictationFidelityCheck.UnsupportedWords(text, sources, templateInstructions);
            // Count only: the words are patient data and stay out of the log.
            if (unsupported.Count > 0) Log.Info($"Prompt: {unsupported.Count} word(s) in the reply are not in the dictation, warning shown");
            return text;
        }

        /// <summary>The transcript a template or quick instruction works on (test mode: a fixed sentence, no recognizer).</summary>
        private async Task<string> TranscribeBodyAsync(TranscribePurpose purpose, CancellationToken ct) =>
            host.FakeGemini ? "Patient klagt über Kopfschmerzen seit drei Tagen" : await host.TranscribeAsync(purpose, input.Wav, ct);

        private void ThrowIfLateCopy(string what)
        {
            if (input.Selection != null || input.IsRetry || input.LateCopyArrived?.Invoke() != true) return;
            Log.Info($"{what}: selection arrived after the deadline, not composing over it");
            throw new SelectionUnreadableException();
        }

        /// <summary>A text-only Dictate Prompt call with the chosen text engine.</summary>
        private Task<string> PromptTextAsync(string? selection, string instruction, string systemPrompt, CancellationToken ct, bool compose = false)
        {
            // compose: the target is the fresh transcript, not a selection, so no earlier turns go along.
            var history = compose ? null : host.HistoryFor(selection);
            return host.PromptProvider(progress).PromptTextAsync(selection, instruction, systemPrompt, ct, history, input.Screenshot);
        }
    }
}
