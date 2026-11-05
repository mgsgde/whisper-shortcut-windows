// Diagnose tool for "Live diktieren" (branch diag/live-diktat). Audio comes from files, not the microphone.
// Never prints or logs the decoded text of the files, only numbers (the corpus is invented sentences, but the habit stays).
//
//   LiveBench <audioDir> <corpus.json> <modelDir> <mode> [threads]
//   mode: whole | live | threads | levels
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperShortcut.Core;

namespace LiveBench;

internal sealed record Case(string Id, string Expected, string[] Alternatives, string[] Files);

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // LIVEBENCH_DB overrides the silence threshold (the app uses -45; -40 was the value before 0.7.3).
    private static readonly MeetingChunkPolicy Policy = float.TryParse(Environment.GetEnvironmentVariable("LIVEBENCH_DB"), NumberStyles.Float, Inv, out var db)
        ? LiveDictation.ChunkPolicy with { SilenceThresholdDb = db }
        : LiveDictation.ChunkPolicy;

    // LIVEBENCH_GAIN_DB scales the recordings (e.g. -12 = a quieter microphone), to see where the -45 dB cut starts to chop.
    private static readonly float Gain = float.TryParse(Environment.GetEnvironmentVariable("LIVEBENCH_GAIN_DB"), NumberStyles.Float, Inv, out var g)
        ? MathF.Pow(10, g / 20) : 1f;

    private static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("LiveBench <audioDir> <corpus.json> <modelDir> <whole|live|threads|levels> [threads]"); return 2; }
        var audioDir = args[0];
        var cases = LoadCases(args[1], audioDir);
        var modelDir = args[2];
        var mode = args[3];
        var threads = args.Length > 4 && int.TryParse(args[4], out var tt) ? tt : Math.Max(1, Environment.ProcessorCount / 2);
        Console.WriteLine($"cases={cases.Count} files={cases.Sum(c => c.Files.Length)} mode={mode} threads={threads}");
        switch (mode)
        {
            case "levels": Levels(cases); return 0;
            case "mic": MicIdle(int.Parse(args[4], Inv)); return 0;
            case "session": SessionDemo(cases, modelDir, args[4], args[5]).GetAwaiter().GetResult(); return 0;
            case "cuts": Cuts(cases, modelDir, args[4]); return 0;
            case "threads": ThreadsTable(cases, modelDir); return 0;
            case "whole": Run(cases, modelDir, threads, live: false); return 0;
            case "live": Run(cases, modelDir, threads, live: true); return 0;
            default: Console.Error.WriteLine("unknown mode"); return 2;
        }
    }

    private static List<Case> LoadCases(string corpusPath, string audioDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(corpusPath));
        var list = new List<Case>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            var parts = c.GetProperty("spoken").GetArrayLength();
            var files = parts == 1
                ? new[] { Path.Combine(audioDir, id + ".wav") }
                : Enumerable.Range(1, parts).Select(i => Path.Combine(audioDir, $"{id}-{i}.wav")).ToArray();
            if (!files.All(File.Exists)) continue; // 42 cases, 46 files
            var alts = c.TryGetProperty("alternatives", out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
            list.Add(new Case(id, c.GetProperty("expected").GetString()!, alts, files));
        }
        return list;
    }

    // ---- WER ----

    private static readonly Regex NotWord = new(@"[^\p{L}\p{N}/]+", RegexOptions.Compiled);

    private static List<string> Norm(string s) =>
        NotWord.Replace(s.ToLowerInvariant().Replace("/", " / "), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static (int Errors, int Words) Wer(string hyp, Case c)
    {
        var h = Norm(hyp);
        var best = (Errors: int.MaxValue, Words: 0);
        foreach (var exp in new[] { c.Expected }.Concat(c.Alternatives))
        {
            var r = Norm(exp);
            var e = LiveDictation.EditDistance(h, r);
            if (e < best.Errors) best = (e, r.Count);
        }
        return best;
    }

    private static bool Exact(string hyp, Case c) =>
        new[] { c.Expected }.Concat(c.Alternatives).Any(e => string.Equals(hyp.Trim(), e.Trim(), StringComparison.Ordinal));

    // ---- decode ----

    private static string DecodePiece(ParakeetTranscriber p, byte[] wav, PunctuationMode mode, out double decodeSeconds)
    {
        var sw = Stopwatch.StartNew();
        var d = p.TranscribeDetailed(wav);
        decodeSeconds = sw.Elapsed.TotalSeconds;
        // As TrayApp.DecodeLivePieceAsync: marks kept by token timing, then FinishPiece.
        var kept = ParakeetPunctuationTiming.KeepSpokenMarks(d.Text, d.Tokens, d.AudioSeconds, out var timed);
        return LiveDictation.FinishPiece(kept, mode, timedMarks: timed);
    }

    // Normal Diktieren with Parakeet in "Nur gesprochen", as TrayApp.TranscribeOfflineAsync + DictateAsync
    // (glossary empty, so GlossaryBackstops changes nothing).
    private static string DecodeWhole(ParakeetTranscriber p, byte[] wav, out double decodeSeconds)
    {
        decodeSeconds = 0;
        var parts = new List<string>();
        foreach (var chunk in WavChunker.SplitForLocal(wav))
        {
            var sw = Stopwatch.StartNew();
            var d = p.TranscribeDetailed(chunk);
            decodeSeconds += sw.Elapsed.TotalSeconds;
            var kept = ParakeetPunctuationTiming.KeepSpokenMarks(d.Text, d.Tokens, d.AudioSeconds, out var timed);
            var piece = timed ? kept : SpokenPunctuation.StripModelPunctuation(kept);
            if (piece.Trim().Length > 0) parts.Add(piece.Trim());
        }
        var spoken = string.Join(" ", parts);
        var raw = SpokenPunctuation.Apply(SpokenPunctuation.StripModelPunctuation(spoken, timedMarks: true), PunctuationMode.SpokenOnly);
        return TextCleanup.DiscardImplausible(TextCleanup.ForDictation(raw, addTrailingPeriod: false), Wav.DurationSeconds(wav));
    }

    private static bool OnlyPunctuation(string s) => s.Trim().Length > 0 && s.Trim().All(ch => !char.IsLetterOrDigit(ch));

    private sealed record Row(string Id, int Errors, int Words, bool Exact, double Audio, double Decode, int Pieces,
        int Pause, int Max, int Stop, int Silent, double StopLatency, double MaxLag, int Tiny, int TinyMarks);

    private static void Run(List<Case> cases, string modelDir, int threads, bool live)
    {
        var sw = Stopwatch.StartNew();
        using var parakeet = ParakeetTranscriber.Load(modelDir, threads);
        Console.WriteLine($"model loaded in {sw.ElapsedMilliseconds} ms");
        // warm-up decode, not counted: the first run after load is slower
        parakeet.Transcribe(Wav.Encode16(new short[16000], 16000));
        var rows = new List<Row>();
        var mode = PunctuationMode.SpokenOnly;
        foreach (var c in cases)
        {
            var joiner = new LiveChunkJoiner { HoldsPeriods = false };
            double audio = 0, decode = 0, stopLatency = 0, maxLag = 0;
            int pieces = 0, pause = 0, max = 0, stop = 0, silent = 0, tiny = 0, tinyMarks = 0;
            foreach (var file in c.Files)
            {
                var wavBytes = File.ReadAllBytes(file);
                var samples = Wav.ReadMonoFloat(wavBytes, out var rate);
                var pcm = samples.Select(s => (short)Math.Clamp((int)Math.Round(s * Gain * 32768f), short.MinValue, short.MaxValue)).ToArray();
                if (Gain != 1f) wavBytes = Wav.Encode16(pcm, rate);
                audio += pcm.Length / (double)rate;
                if (!live)
                {
                    var text = DecodeWhole(parakeet, wavBytes, out var t);
                    decode += t; pieces++; stop++;
                    joiner.Next(text);
                    stopLatency = t;
                    continue;
                }
                // Live: the capture thread feeds 100 ms buffers into the chunker, exactly like TrayApp.FeedLive.
                var chunker = new MeetingChunker(16000, Policy);
                var cuts = new List<(MeetingAudioChunk Chunk, double At, string Reason)>();
                const int step = 1600;
                for (int off = 0; off < pcm.Length; off += step)
                {
                    var n = Math.Min(step, pcm.Length - off);
                    foreach (var ch in chunker.Write(pcm.AsSpan(off, n)))
                        cuts.Add((ch, (off + n) / 16000.0, ch.DurationSeconds >= LiveDictation.ChunkPolicy.MaxChunk.TotalSeconds - 0.01 ? "Max" : "Pause"));
                }
                var end = pcm.Length / 16000.0;
                var tail = chunker.Flush();
                if (tail != null) cuts.Add((tail, end, "Stop"));
                // One decode chain in speaking order: a piece starts when it was cut and the previous one is done.
                double chain = 0;
                foreach (var (ch, at, reason) in cuts)
                {
                    pieces++;
                    if (reason == "Pause") pause++; else if (reason == "Max") max++; else stop++;
                    if (ch.Silent) { silent++; continue; }
                    var text = DecodePiece(parakeet, ch.Wav, mode, out var t);
                    decode += t;
                    var start = Math.Max(chain, at);
                    chain = start + t;
                    maxLag = Math.Max(maxLag, chain - at);
                    // Short pieces with letters are the "zerhackt" symptom; a lone "." or "," is a spoken mark after a pause.
                    if (text.Trim().Length is > 0 and <= 2)
                    {
                        if (OnlyPunctuation(text)) tinyMarks++; else tiny++;
                    }
                    joiner.Next(text);
                }
                // Whatever is still decoding when the speaker stops: the wait after Stop.
                stopLatency = Math.Max(0, chain - end);
            }
            joiner.Finish();
            var result = joiner.Inserted.Trim();
            var (e, w) = Wer(result, c);
            if (Environment.GetEnvironmentVariable("LIVEBENCH_SHOW") == "1" && !Exact(result, c))
                Console.WriteLine("DIFF " + c.Id + ": got [" + result.Replace("\n", "\\n") + "] want [" + c.Expected.Replace("\n", "\\n") + "]");
            rows.Add(new Row(c.Id, e, w, Exact(result, c), audio, decode, pieces, pause, max, stop, silent, stopLatency, maxLag, tiny, tinyMarks));
        }
        Print(rows, live);
    }

    private static void Print(List<Row> rows, bool live)
    {
        Console.WriteLine();
        Console.WriteLine("id\twerr\twords\texact\taudioS\tdecodeS\trtf\tpieces\tpause\tmax\tstop\tsilent\ttiny\tstopLatS\tmaxLagS");
        foreach (var r in rows)
            Console.WriteLine(string.Join('\t', r.Id, r.Errors, r.Words, r.Exact ? "1" : "0", F(r.Audio), F(r.Decode),
                F(r.Decode / Math.Max(0.001, r.Audio)), r.Pieces, r.Pause, r.Max, r.Stop, r.Silent, r.Tiny, F(r.StopLatency), F(r.MaxLag)));
        double errors = rows.Sum(r => r.Errors), words = rows.Sum(r => r.Words);
        double audio = rows.Sum(r => r.Audio), decode = rows.Sum(r => r.Decode);
        var lat = rows.Select(r => r.StopLatency).OrderBy(x => x).ToArray();
        Console.WriteLine();
        Console.WriteLine($"SUMMARY {(live ? "live" : "whole")}: WER={100 * errors / words:0.0}% ({errors}/{words}) exact={rows.Count(r => r.Exact)}/{rows.Count} "
            + $"audio={audio:0.0}s decode={decode:0.0}s RTF={decode / audio:0.000} pieces={rows.Sum(r => r.Pieces)} "
            + $"pause={rows.Sum(r => r.Pause)} max={rows.Sum(r => r.Max)} stop={rows.Sum(r => r.Stop)} silent={rows.Sum(r => r.Silent)} tinyLetters={rows.Sum(r => r.Tiny)} tinyMarksOnly={rows.Sum(r => r.TinyMarks)} "
            + $"stopLatency median={lat[lat.Length / 2]:0.00}s p90={lat[(int)(lat.Length * 0.9)]:0.00}s max={lat[^1]:0.00}s maxLag={rows.Max(r => r.MaxLag):0.00}s");
    }

    private static string F(double v) => v.ToString("0.00", Inv);

    // ---- threads ----

    private static void ThreadsTable(List<Case> cases, string modelDir)
    {
        var files = cases.SelectMany(c => c.Files).Select(f => (f, dur: Wav.DurationSeconds(File.ReadAllBytes(f)))).OrderBy(x => x.dur).ToList();
        var short1 = files.First(x => x.dur >= 2.0);
        var longest = files[^1];
        Console.WriteLine($"short {Path.GetFileName(short1.f)} {short1.dur:0.0}s, longest {Path.GetFileName(longest.f)} {longest.dur:0.0}s");
        Console.WriteLine("threads\tload_ms\tshort_s_median\tshort_rtf\tlong_s_median\tlong_rtf");
        foreach (var t in new[] { 1, 2, 3, 4, Environment.ProcessorCount }.Distinct())
        {
            var sw = Stopwatch.StartNew();
            using var p = ParakeetTranscriber.Load(modelDir, t);
            var load = sw.ElapsedMilliseconds;
            p.Transcribe(Wav.Encode16(new short[16000], 16000));
            double Median(string f)
            {
                var wav = File.ReadAllBytes(f);
                var times = new List<double>();
                for (int i = 0; i < 5; i++) { var s = Stopwatch.StartNew(); p.Transcribe(wav); times.Add(s.Elapsed.TotalSeconds); }
                times.Sort();
                return times[times.Count / 2];
            }
            var a = Median(short1.f);
            var b = Median(longest.f);
            Console.WriteLine($"{t}\t{load}\t{F(a)}\t{F(a / short1.dur)}\t{F(b)}\t{F(b / longest.dur)}");
        }
    }


    // ---- cuts: where does the live chunker cut one file, and what does each piece decode to (private look) ----

    private static void Cuts(List<Case> cases, string modelDir, string id)
    {
        using var p = ParakeetTranscriber.Load(modelDir, 3);
        foreach (var f in cases.Where(c => c.Id == id).SelectMany(c => c.Files))
        {
            var wavBytes = File.ReadAllBytes(f);
            var samples = Wav.ReadMonoFloat(wavBytes, out _);
            var pcm = samples.Select(s => (short)Math.Clamp((int)Math.Round(s * 32768f), short.MinValue, short.MaxValue)).ToArray();
            var line = new System.Text.StringBuilder();
            for (int i = 0; i + 3200 <= pcm.Length; i += 3200) line.Append(MeetingAudioMixer.RmsDb(pcm.AsSpan(i, 3200)).ToString("0", Inv)).Append(' ');
            Console.WriteLine($"{Path.GetFileName(f)} 0.2s-window dB: {line}");
            var chunker = new MeetingChunker(16000, Policy);
            var chunks = new List<MeetingAudioChunk>();
            for (int off = 0; off < pcm.Length; off += 1600)
                chunks.AddRange(chunker.Write(pcm.AsSpan(off, Math.Min(1600, pcm.Length - off))));
            var tail = chunker.Flush(); if (tail != null) chunks.Add(tail);
            foreach (var ch in chunks)
                Console.WriteLine($"  piece start={ch.StartSeconds:0.0}s dur={ch.DurationSeconds:0.0}s silent={ch.Silent} -> [{(ch.Silent ? "" : DecodePiece(p, ch.Wav, PunctuationMode.SpokenOnly, out _))}]");
        }
    }


    // ---- session: the real LiveDictationSession with a fake inserter, to see the LIVE-DIAG log lines ----

    private static async Task SessionDemo(List<Case> cases, string modelDir, string id, string logPath)
    {
        Log.Init(logPath);
        using var p = ParakeetTranscriber.Load(modelDir, 3);
        var c = cases.First(x => x.Id == id);
        var wavBytes = File.ReadAllBytes(c.Files[0]);
        var samples = Wav.ReadMonoFloat(wavBytes, out _);
        var pcm = samples.Select(s => (short)Math.Clamp((int)Math.Round(s * 32768f), short.MinValue, short.MaxValue)).ToArray();
        var session = new LiveDictationSession(
            (wav, ct) => Task.Run(() => DecodePiece(p, wav, PunctuationMode.SpokenOnly, out _), ct),
            async text => { await Task.Delay(60); return true; }, holdsPeriods: false);
        var chunker = new MeetingChunker(16000, Policy);
        for (int off = 0; off < pcm.Length; off += 1600)
            foreach (var ch in chunker.Write(pcm.AsSpan(off, Math.Min(1600, pcm.Length - off))))
                session.Add(ch.Wav, ch.Silent, new LivePieceInfo("Pause", Stopwatch.GetTimestamp()));
        var tail = chunker.Flush();
        if (tail != null) session.Add(tail.Wav, tail.Silent, new LivePieceInfo("Stop", Stopwatch.GetTimestamp()));
        await session.FinishAsync(wavBytes);
        Console.WriteLine(string.Join(Environment.NewLine, File.ReadAllLines(logPath).Where(l => l.Contains("LIVE-"))));
    }


    // ---- mic: idle noise floor of the Windows default recording device, same capture path as MicAudioSource ----

    private static void MicIdle(int seconds)
    {
        for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
            Console.WriteLine($"device {i}: {NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName}");
        var all = new List<short>();
        using var waveIn = new NAudio.Wave.WaveInEvent { DeviceNumber = -1, WaveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        waveIn.DataAvailable += (_, e) =>
        {
            var s = new short[e.BytesRecorded / 2];
            Buffer.BlockCopy(e.Buffer, 0, s, 0, s.Length * 2);
            lock (all) all.AddRange(s);
        };
        waveIn.StartRecording();
        Thread.Sleep(seconds * 1000);
        waveIn.StopRecording();
        short[] pcm; lock (all) pcm = all.ToArray();
        var dbs = new List<float>();
        for (int i = 0; i + 3200 <= pcm.Length; i += 3200) dbs.Add(MeetingAudioMixer.RmsDb(pcm.AsSpan(i, 3200)));
        Console.WriteLine($"idle 0.2s windows dB: {string.Join(' ', dbs.Select(d => d.ToString("0", Inv)))}");
        Console.WriteLine($"SUMMARY idle floor: min={dbs.Min():0.0} median={dbs.OrderBy(x => x).ElementAt(dbs.Count / 2):0.0} max={dbs.Max():0.0} dB (threshold -45); windows above -45: {dbs.Count(d => d >= -45)}/{dbs.Count}");
    }

    // ---- levels ----

    private static void Levels(List<Case> cases)
    {
        Console.WriteLine("file\tdurS\tpeakDb\tfloorDb(p10 of 0.2s windows)\tspeechDb(p90)\twindowsBelow-45pct");
        var floors = new List<double>(); var speech = new List<double>();
        foreach (var f in cases.SelectMany(c => c.Files))
        {
            var samples = Wav.ReadMonoFloat(File.ReadAllBytes(f), out var rate);
            var pcm = samples.Select(s => (short)Math.Clamp((int)Math.Round(s * 32768f), short.MinValue, short.MaxValue)).ToArray();
            var win = (int)(0.2 * rate);
            var dbs = new List<double>();
            for (int i = 0; i + win <= pcm.Length; i += win) dbs.Add(MeetingAudioMixer.RmsDb(pcm.AsSpan(i, win)));
            if (dbs.Count == 0) continue;
            var sorted = dbs.OrderBy(x => x).ToArray();
            var peak = 20 * Math.Log10(Math.Max(1, pcm.Max(s => Math.Abs((int)s))) / 32768.0);
            var p10 = sorted[(int)(sorted.Length * 0.1)]; var p90 = sorted[(int)(sorted.Length * 0.9)];
            floors.Add(p10); speech.Add(p90);
            Console.WriteLine($"{Path.GetFileName(f)}\t{pcm.Length / (double)rate:0.0}\t{peak:0.0}\t{p10:0.0}\t{p90:0.0}\t{100.0 * dbs.Count(d => d < -45) / dbs.Count:0}");
        }
        floors.Sort(); speech.Sort();
        Console.WriteLine($"SUMMARY floor median={floors[floors.Count / 2]:0.0} dB (max {floors[^1]:0.0}); speech median={speech[speech.Count / 2]:0.0} dB (min {speech[0]:0.0})");
    }
}
