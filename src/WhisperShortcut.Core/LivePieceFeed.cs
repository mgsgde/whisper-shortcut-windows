using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhisperShortcut.Core;

/// <summary>
/// The capture side of one live dictation: the chunker that cuts the 16 kHz mono stream at pauses and the queue of
/// finished pieces. The capture thread calls <see cref="Write"/>; the UI thread drains with <see cref="TryTake"/>.
/// The chunker is single-threaded, so every touch of it goes through one lock. After <see cref="Finish"/> or
/// <see cref="Close"/> the feed takes no more audio (a late buffer of the capture thread is dropped).
/// </summary>
public sealed class LivePieceFeed
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<(MeetingAudioChunk Chunk, long Cut)> _pieces = new();
    private readonly Func<long> _clock;
    private MeetingChunker? _chunker;

    /// <param name="policy">Null = <see cref="LiveDictation.ChunkPolicy"/>.</param>
    /// <param name="clock">Timestamp of a cut (default <see cref="Stopwatch.GetTimestamp"/>); the session logs the latency from it.</param>
    public LivePieceFeed(MeetingChunkPolicy? policy = null, Func<long>? clock = null)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        _chunker = new MeetingChunker(16000, policy ?? LiveDictation.ChunkPolicy);
    }

    /// <summary>Any piece is waiting.</summary>
    public bool HasPieces => !_pieces.IsEmpty;

    /// <summary>Feeds 16-bit little-endian mono PCM (an odd trailing byte is dropped). True when at least one piece was cut.</summary>
    public bool Write(ReadOnlySpan<byte> pcm16)
    {
        bool any = false;
        lock (_gate)
        {
            if (_chunker == null) return false;
            foreach (var chunk in _chunker.Write(MemoryMarshal.Cast<byte, short>(pcm16[..(pcm16.Length & ~1)])))
            {
                _pieces.Enqueue((chunk, _clock()));
                any = true;
            }
        }
        return any;
    }

    /// <summary>The next finished piece, in order.</summary>
    public bool TryTake(out (MeetingAudioChunk Chunk, long Cut) piece) => _pieces.TryDequeue(out piece);

    /// <summary>The recording ended: the rest of the stream as the last piece (null when nothing is left). The feed is closed.</summary>
    public MeetingAudioChunk? Finish()
    {
        lock (_gate)
        {
            var tail = _chunker?.Flush();
            _chunker = null;
            return tail;
        }
    }

    /// <summary>Drops the chunker and every waiting piece (cancelled or ended).</summary>
    public void Close()
    {
        lock (_gate) _chunker = null;
        _pieces.Clear();
    }
}
