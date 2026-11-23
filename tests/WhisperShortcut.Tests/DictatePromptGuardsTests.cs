using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

/// <summary>Port of the Mac's PromptReplyGuardTests and PromptInstructionGuardTests (Mac #459).</summary>
public class PromptReplyGuardTests
{
    private static readonly string ScaffoldReply =
        "The user provided an SRT-like or timestamped transcript and a selection.\n"
        + "SELECTED TEXT FROM CLIPBOARD (apply the voice instruction to this text):\n\n-\n\n"
        + "VOICE INSTRUCTION:\nthe list\n"
        + string.Concat(Enumerable.Repeat("Let me think about what the user might want here. ", 200));

    [Fact]
    public void ObservedFailure_ElevenKCharactersOfSelfTalkQuotingTheScaffold()
    {
        var instruction = string.Concat(Enumerable.Repeat("a list item, ", 31));
        Assert.True(ScaffoldReply.Length > 10_000);
        Assert.Equal(PromptReplyGuard.Verdict.ScaffoldEcho, PromptReplyGuard.Check(ScaffoldReply, "-", instruction));
        // Without an instruction the scaffold rule still fires.
        Assert.Equal(PromptReplyGuard.Verdict.ScaffoldEcho, PromptReplyGuard.Check(ScaffoldReply, "-", null));
    }

    [Fact]
    public void BothLabelsCountInTheColonFormTheModelQuotes()
    {
        Assert.Equal(PromptReplyGuard.Verdict.ScaffoldEcho,
            PromptReplyGuard.Check("Sure.\nSELECTED TEXT FROM CLIPBOARD:\nfoo", "foo", "fix"));
        Assert.Equal(PromptReplyGuard.Verdict.ScaffoldEcho,
            PromptReplyGuard.Check("Sure.\n  VOICE INSTRUCTION: fix", "foo", "fix"));
    }

    [Fact]
    public void TheRequestBuilderAndTheGuardShareTheLabelConstants()
    {
        Assert.StartsWith(Prompts.ClipboardSelectionLabel, Prompts.ClipboardSelectionHeader);
        Assert.True(PromptReplyGuard.StartsLineWithScaffoldLabel(Prompts.ClipboardSelectionHeader));
        Assert.True(PromptReplyGuard.StartsLineWithScaffoldLabel(Prompts.VoiceInstructionLabel + "\nx"));
        Assert.True(PromptReplyGuard.StartsLineWithScaffoldLabel(DictatePromptText.SelectionBlock("Text")));
        Assert.True(PromptReplyGuard.StartsLineWithScaffoldLabel(DictatePromptText.LocalUserTurn("Text", "kürzer")));
    }

    [Fact]
    public void AReplyThatMerelyMentionsTheWordsMidSentencePasses()
    {
        const string reply = "Your voice instruction was clear. The selected text from clipboard is now shorter.";
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(reply, "Hello world", "shorter"));
        Assert.Equal(PromptReplyGuard.Verdict.Ok,
            PromptReplyGuard.Check("Note: VOICE INSTRUCTION: is a label.", "x", "y"));
    }

    [Fact]
    public void ALabelTheUsersOwnSelectionAlreadyHasAtALineStartIsNotAnEcho()
    {
        const string selection = "Docs\nVOICE INSTRUCTION: spoken command\nmore";
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(
            "Docs\nVOICE INSTRUCTION: spoken command\nmore!", selection, "add an exclamation mark"));
    }

    [Fact]
    public void ANormalEditPasses() =>
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(
            "Hallo Mark, danke für die Nachricht.", "hallo mark danke fuer die nachricht", "Korrigiere"));

    [Fact]
    public void ALongComposeReplyUnderTheCapPassesOverItDoesNot()
    {
        const string instruction = "Write a long email to the landlord about the broken heating";
        var cap = Math.Max(2000, 8 * instruction.Length);
        Assert.Equal(2000, cap);
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(new string('a', 1800), null, instruction));
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(new string('a', cap), null, instruction));
        Assert.Equal(PromptReplyGuard.Verdict.Runaway, PromptReplyGuard.Check(new string('a', cap + 1), null, instruction));
    }

    [Fact]
    public void TheCapGrowsWithALongSelectionPlusInstruction()
    {
        var selection = new string('s', 400);
        var instruction = new string('i', 100);
        const int cap = 8 * 500;
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(new string('r', cap), selection, instruction));
        Assert.Equal(PromptReplyGuard.Verdict.Runaway, PromptReplyGuard.Check(new string('r', cap + 1), selection, instruction));
    }

    [Fact]
    public void AnUnknownInstructionSkipsOnlyTheLengthRule() =>
        Assert.Equal(PromptReplyGuard.Verdict.Ok, PromptReplyGuard.Check(new string('a', 9000), null, null));

    [Fact]
    public void TheRejectionIsARetryableToastWithAMessage()
    {
        var p = RunOutcomes.ForFailure(new PromptReplyRejectedException(), audioKept: true);
        Assert.Equal(UserCopy.ReplyNotPastedTitle, p.Title);
        Assert.Equal(FollowUp.Retry, p.Action);
        Assert.True(p.KeepAudio);
        Assert.Contains("nichts eingefügt", p.Text);
        Assert.DoesNotContain(" Ihre ", p.Text);
    }
}

public class PromptInstructionGuardTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("Oh.")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ja")]
    [InlineData("12.")]
    [InlineData("00:00:01.077 - 00:00:03.277\nI'm looking for a good book.")]
    [InlineData("00:00:01,077 --> 00:00:03,277")]
    public void MisTranscriptionsAreNotInstructions(string text) =>
        Assert.True(PromptInstructionGuard.IsNotUnderstood(text));

    [Theory]
    [InlineData("korrigiere")]
    [InlineData("kürzer")]
    [InlineData("fix")]
    [InlineData("Übersetze auf Englisch.")]
    [InlineData("corrigir")]
    [InlineData("Korrigiere Formular 0.")]
    [InlineData("Korriger formulere noe.")]
    [InlineData("Make it 10:30 sharp")]
    [InlineData("缩短")]
    [InlineData("短く")]
    [InlineData("요약")]
    public void LegitimateShortInstructionsPass(string text) =>
        Assert.False(PromptInstructionGuard.IsNotUnderstood(text));

    private static readonly string[] Saved =
        { "Korrigiere, formuliere neu.", "Fasse in drei Sätzen zusammen", "Übersetze auf Englisch" };

    [Fact]
    public void ANearMissOfASavedPromptSnapsToIt()
    {
        Assert.Equal("Korrigiere, formuliere neu.", PromptInstructionGuard.Snapped("Korriger formulere noe.", Saved));
        Assert.Equal("Korrigiere, formuliere neu.", PromptInstructionGuard.Snapped("korrigiere formuliere neu", Saved));
        Assert.Equal("Übersetze auf Englisch", PromptInstructionGuard.Snapped("Übersetze auf Englisch.", Saved));
    }

    [Fact]
    public void TheNearMissWithAStrayDigitIsJustBelowTheBarSoNothingSnaps()
    {
        var key = PromptInstructionGuard.NormalizedKey("Korrigiere Formular 0.");
        var target = PromptInstructionGuard.NormalizedKey("Korrigiere, formuliere neu.");
        Assert.True(PromptInstructionGuard.Similarity(key, target) < 0.8);
        Assert.Null(PromptInstructionGuard.Snapped("Korrigiere Formular 0.", Saved));
    }

    [Fact]
    public void UnrelatedLongOrEmptyInputChangesNothing()
    {
        Assert.Null(PromptInstructionGuard.Snapped("kürzer", Saved));
        Assert.Null(PromptInstructionGuard.Snapped("fix", Saved));
        Assert.Null(PromptInstructionGuard.Snapped("Korrigiere formuliere neu und mach es freundlicher bitte", Saved));
        Assert.Null(PromptInstructionGuard.Snapped("Korriger formulere noe", Array.Empty<string>()));
        Assert.Null(PromptInstructionGuard.Snapped("...", Saved));
    }

    [Fact]
    public void TwoSavedPromptsWithinReachMeansNoSnap()
    {
        Assert.Null(PromptInstructionGuard.Snapped("Mach es kurzer", new[] { "Mach es kürzer", "Mach es kurz" }));
        // Duplicates by key are one prompt, not two.
        Assert.NotNull(PromptInstructionGuard.Snapped("Mach es kurzer", new[] { "Mach es kürzer", "mach es kürzer." }));
    }

    [Fact]
    public void SimilarityIsCaseAndPunctuationInsensitiveSymmetricAndBounded()
    {
        Assert.Equal("korrigiere formuliere neu", PromptInstructionGuard.NormalizedKey("  Korrigiere,   FORMULIERE neu! "));
        Assert.Equal(1, PromptInstructionGuard.Similarity("abc", "abc"));
        Assert.Equal(0, PromptInstructionGuard.Similarity("abc", "xyz"));
        Assert.Equal(PromptInstructionGuard.Similarity("kitten", "sitting"), PromptInstructionGuard.Similarity("sitting", "kitten"));
    }

    [Fact]
    public void TheRejectionIsANonRetryableToastWithAMessage()
    {
        var p = RunOutcomes.ForFailure(new InstructionNotUnderstoodException(), audioKept: true);
        Assert.Equal(UserCopy.InstructionNotUnderstoodTitle, p.Title);
        Assert.Equal(FollowUp.None, p.Action);
        Assert.Equal("Deine gesprochene Anweisung wurde nicht verstanden, daher wurde nichts geändert. Bitte sag sie noch einmal.", p.Text);
    }
}
