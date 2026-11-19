using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// A filler-only spoken Dictate Prompt instruction ("Mhm.", "Äh, ähm.", a lone "0", no speech)
/// must not edit the selection or write text. Port of DictatePromptSelectionTests.fillerOnlyInstruction
/// (Mac #510). The hesitation words are the #507 list (<see cref="LiveDictation.FillerRegex"/>);
/// "oh", "ah" and "0" are extra because they are the whole instruction, not a word inside one.
/// </summary>
public class FillerOnlyInstructionTests
{
    [Fact]
    public void FillerOnlyInstruction_IsFillersPunctuationALoneZeroOrNoSpeech()
    {
        var fillers = new[]
        {
            "Mhm.", "Ah.", "Oh.", "0", "Äh, ähm.", "hmm", ".", "…",
            "mmhmm", "m-hm", "Hm", "  Mhm.  ",
            FillerOnlyInstruction.NoSpokenInstructionMarker,
        };
        var notFillers = new[]
        {
            "",
            "Korrigiere",
            "Oh, kürze das",
            "Ah, mach es kürzer",
            "Okay",
            "10",
            "aha",
            "um",
            "achso",
            PromptConversationMemory.SpokenPlaceholder,
        };
        foreach (var text in fillers)
        {
            Assert.True(FillerOnlyInstruction.Is(text), "expected filler: " + text);
            Assert.True(FillerOnlyInstruction.Refuses(text), "expected refusal: " + text);
        }
        foreach (var text in notFillers)
        {
            Assert.False(FillerOnlyInstruction.Is(text), "expected not a filler: " + text);
            Assert.False(FillerOnlyInstruction.Refuses(text), "expected no refusal: " + text);
        }
        // Typed quick-action text and a template turn are not spoken fillers.
        Assert.False(FillerOnlyInstruction.Refuses("Mhm.", isQuickAction: true));
        Assert.False(FillerOnlyInstruction.Refuses("0", isTemplateTurn: true));
    }
}
