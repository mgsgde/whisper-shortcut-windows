using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class QuickActionKindTests
{
    [Theory]
    [InlineData("Rechtschreibung und Grammatik korrigieren", QuickActionKind.Correct)]
    [InlineData("Klarer formulieren", QuickActionKind.Rewrite)]
    [InlineData("Kürzer fassen", QuickActionKind.Shorten)]
    [InlineData("Als Aufgabenliste formatieren", QuickActionKind.Format)]
    [InlineData("Ins Englische übersetzen", QuickActionKind.Translate)]
    [InlineData("Als E-Mail formulieren", QuickActionKind.Rewrite)]
    [InlineData("Als Angebot schreiben", QuickActionKind.Other)]
    // Order: shorten before rewrite, format before correct.
    [InlineData("Formuliere neu und kürzer", QuickActionKind.Shorten)]
    [InlineData("Format and fix typos", QuickActionKind.Format)]
    // A token must start a word, and the not-verbs never count.
    [InlineData("Add more information", QuickActionKind.Other)]
    [InlineData("Explain this shortcut", QuickActionKind.Other)]
    [InlineData("", QuickActionKind.Other)]
    public void ClassifiesLikeTheMac(string text, QuickActionKind expected) =>
        Assert.Equal(expected, QuickActionKinds.Classify(text));

    [Fact]
    public void DefaultsAndBareVerbsAreEdits()
    {
        Assert.True(QuickActionKinds.IsEdit("Kürzer fassen"));
        Assert.True(QuickActionKinds.IsEdit("Korrigiere"));
        Assert.False(QuickActionKinds.IsEdit("Als Angebot schreiben"));
    }
}
