using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class RecentTextsTests
{
    [Fact]
    public void Add_IgnoresBlank_NewestFirst_MovesDuplicate_DropsOldest()
    {
        var recent = new RecentTexts();
        Assert.Null(recent.Latest);
        Assert.Empty(recent.Items);

        recent.Add(null!);
        recent.Add("");
        recent.Add(" \n\t ");
        Assert.Empty(recent.Items);

        recent.Add("one");
        recent.Add("two");
        recent.Add("three");
        recent.Add("four");
        recent.Add("five");
        recent.Add("six");
        Assert.Equal(RecentTexts.Capacity, recent.Items.Count);
        Assert.Equal(new[] { "six", "five", "four", "three", "two" }, recent.Items);
        Assert.Equal("six", recent.Latest);

        // "four" is already kept: it moves to the top and "two" stays. A second copy would have pushed "two" out.
        recent.Add("four");
        Assert.Equal(new[] { "four", "six", "five", "three", "two" }, recent.Items);
        Assert.Equal("four", recent.Latest);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var recent = new RecentTexts();
        recent.Add("kept");
        recent.Clear();
        Assert.Null(recent.Latest);
        Assert.Empty(recent.Items);
    }

    [Fact]
    public void Preview_CollapsesWhitespace_AndCutsAtAWordOrAtMaxChars()
    {
        Assert.Equal("hello world", RecentTexts.Preview(" \thello \r\n\n world\t"));
        Assert.Equal("hi", RecentTexts.Preview("hi"));
        Assert.Equal(new string('a', 60), RecentTexts.Preview(new string('a', 60)));

        // No space in the last 15 of the 20-character window: cut at 20 and mark it.
        Assert.Equal(new string('x', 20) + "…", RecentTexts.Preview(new string('x', 25), 20));

        // Space at index 10 sits inside the last 15 characters of a 20-character window.
        Assert.Equal("abcdefghij…", RecentTexts.Preview("abcdefghij klmnopqrstuvwxyz", 20));

        // The only space is at index 3, outside that 15-character tail, so the cut stays hard.
        var earlySpace = "abc " + new string('d', 30);
        Assert.Equal("abc " + new string('d', 16) + "…", RecentTexts.Preview(earlySpace, 20));
    }
}
