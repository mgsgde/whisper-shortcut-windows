using System.Buffers.Binary;
using System.Text;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class RecordingCheckpointTests
{
    [Fact]
    public void BeginAppendDispose_ThenRecover_KeepsPcmAndMode()
    {
        using var dir = new TempDir();
        var pcm = Pcm(seconds: 1, seed: 3);
        using (var cp = RecordingCheckpoint.TryBegin(dir.Path, "Prompt"))
        {
            Assert.NotNull(cp);
            Assert.True(cp!.TryAppend(pcm));
        }

        var rec = RecordingCheckpoint.TryRecover(dir.Path);
        Assert.NotNull(rec);
        Assert.Equal("Prompt", rec!.Mode);
        Assert.InRange(rec.Seconds, 0.99, 1.01);
        Assert.Equal(pcm, rec.Wav.AsSpan(44).ToArray());
        Assert.True(File.Exists(Path.Combine(dir.Path, RecordingCheckpoint.FileName)));
    }

    [Fact]
    public void Repair_FixesZeroSizedHeader_AsAfterACrash()
    {
        var pcm = Pcm(seconds: 1, seed: 9);
        var raw = Header(dataSize: 0).Concat(pcm).ToArray();
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(40)));

        var wav = RecordingCheckpoint.Repair(raw);
        Assert.NotNull(wav);
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav!.AsSpan(40)));
        Assert.Equal(36 + pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(pcm, wav.AsSpan(44).ToArray());
        Assert.InRange(Wav.DurationSeconds(wav), 0.99, 1.01);
    }

    [Fact]
    public void TryRecover_UnfinalizedFile_RepairsAndKeeps()
    {
        using var dir = new TempDir();
        var pcm = Pcm(seconds: 2, seed: 11);
        File.WriteAllBytes(Path.Combine(dir.Path, RecordingCheckpoint.FileName), Header(0).Concat(pcm).ToArray());
        File.WriteAllText(Path.Combine(dir.Path, RecordingCheckpoint.MetaFileName),
            "{\"Mode\":\"Dictate\",\"StartedUtc\":\"2026-11-01T12:00:00\"}");

        var rec = RecordingCheckpoint.TryRecover(dir.Path);
        Assert.NotNull(rec);
        Assert.Equal("Dictate", rec!.Mode);
        Assert.Equal(pcm, rec.Wav.AsSpan(44).ToArray());
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), rec.StartedUtc);
    }

    [Fact]
    public void TryRecover_TooShort_DeletesAndReturnsNull()
    {
        using var dir = new TempDir();
        var pcm = Pcm(seconds: 0.2, seed: 1);
        File.WriteAllBytes(Path.Combine(dir.Path, RecordingCheckpoint.FileName), Header(pcm.Length).Concat(pcm).ToArray());

        Assert.Null(RecordingCheckpoint.TryRecover(dir.Path));
        Assert.False(File.Exists(Path.Combine(dir.Path, RecordingCheckpoint.FileName)));
    }

    [Fact]
    public void TryRecover_MissingOrJunk_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(RecordingCheckpoint.TryRecover(dir.Path));

        File.WriteAllBytes(Path.Combine(dir.Path, RecordingCheckpoint.FileName), new byte[] { 1, 2, 3, 4 });
        Assert.Null(RecordingCheckpoint.TryRecover(dir.Path));
        Assert.False(File.Exists(Path.Combine(dir.Path, RecordingCheckpoint.FileName)));
    }

    [Fact]
    public void Discard_RemovesWavAndMeta()
    {
        using var dir = new TempDir();
        var pcm = Pcm(seconds: 1, seed: 2);
        var cp = RecordingCheckpoint.TryBegin(dir.Path, "Dictate");
        Assert.NotNull(cp);
        Assert.True(cp!.TryAppend(pcm));
        cp.Discard();

        Assert.False(File.Exists(Path.Combine(dir.Path, RecordingCheckpoint.FileName)));
        Assert.False(File.Exists(Path.Combine(dir.Path, RecordingCheckpoint.MetaFileName)));
        Assert.Null(RecordingCheckpoint.TryRecover(dir.Path));
    }

    [Fact]
    public void Begin_OverwritesPreviousTake()
    {
        using var dir = new TempDir();
        using (var first = RecordingCheckpoint.TryBegin(dir.Path, "Dictate"))
            Assert.True(first!.TryAppend(Pcm(seconds: 1, seed: 1)));

        var secondPcm = Pcm(seconds: 1, seed: 4);
        using (var second = RecordingCheckpoint.TryBegin(dir.Path, "Prompt"))
            Assert.True(second!.TryAppend(secondPcm));

        var rec = RecordingCheckpoint.TryRecover(dir.Path);
        Assert.Equal("Prompt", rec!.Mode);
        Assert.Equal(secondPcm, rec.Wav.AsSpan(44).ToArray());
    }

    [Fact]
    public void Repair_RejectsNonPcm()
    {
        Assert.Null(RecordingCheckpoint.Repair(new byte[10]));
        var eightBit = Header(100);
        eightBit[34] = 8;
        Assert.Null(RecordingCheckpoint.Repair(eightBit.Concat(new byte[100]).ToArray()));
    }

    [Fact]
    public void DiscardAll_OnEmptyDirectory_DoesNotThrow()
    {
        using var dir = new TempDir();
        RecordingCheckpoint.DiscardAll(dir.Path);
        RecordingCheckpoint.DiscardAll(Path.Combine(dir.Path, "missing"));
    }

    private static byte[] Pcm(double seconds, int seed)
    {
        var n = (int)Math.Round(seconds * 16000) * 2;
        var pcm = new byte[n];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static byte[] Header(int dataSize)
    {
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(h, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(h, 12);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), dataSize);
        return h;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsw-cp-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* leftover from a failed assert */ }
        }
    }
}
