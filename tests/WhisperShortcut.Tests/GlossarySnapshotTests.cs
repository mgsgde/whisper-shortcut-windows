using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class GlossarySnapshotTests
{
    [Fact]
    public void TryAddGlossaryTerm_SwapsAWholeNewList()
    {
        var s = new Settings { Glossary = new() { "Alpha" } };
        var before = s.Glossary;
        Assert.Equal(GlossaryAddResult.Added, s.TryAddGlossaryTerm("Beta", out _));
        Assert.Equal(new[] { "Alpha" }, before); // the published snapshot is untouched
        Assert.Equal(new[] { "Alpha", "Beta" }, s.Glossary);
        Assert.Equal(GlossaryAddResult.Duplicate, s.TryAddGlossaryTerm("Beta", out _));
        Assert.Same(s.Glossary, s.Glossary);
    }

    [Fact]
    public async Task ConcurrentReadsDuringAdds_NeverThrow()
    {
        var s = new Settings { Glossary = new() { "Start" } };
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var copy = s.Glossary.ToList();
                Assert.All(copy, t => Assert.False(string.IsNullOrEmpty(t)));
            }
        })).ToArray();
        for (int i = 0; i < 2000; i++) s.TryAddGlossaryTerm("Term" + i, out _);
        stop.Cancel();
        await Task.WhenAll(readers);
        Assert.Equal(2001, s.Glossary.Count);
    }
}
