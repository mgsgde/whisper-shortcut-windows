using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Gemini sometimes writes its reasoning into the visible answer between start_thought and
/// end_thought. Port of LeakedThoughtStripTests (Mac #443).
/// </summary>
public class LeakedThoughtStripTests
{
    [Fact]
    public void TextWithoutMarkersIsUnchangedApartFromTheLeadingWhitespaceTrim()
    {
        Assert.Equal("Hello there", TextCleanup.StripLeakedThought("Hello there"));
        Assert.Equal("Hello\nthere", TextCleanup.StripLeakedThought("Hello\nthere"));
        Assert.Equal("Hello", TextCleanup.StripLeakedThought("  \nHello"));
    }

    [Fact]
    public void AFullBlockAtTheStartIsRemovedAndTheAnswerIsKept()
    {
        var leaked = """
            start_thought
            Context: Work Chat (Teams) with colleague Mark.
            Wait, let's keep it close to dictation.
            end_thought
            Thanks Mark, I'll send the notes.
            """;
        Assert.Equal("Thanks Mark, I'll send the notes.", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void AnEndThoughtWithNoOpenerDropsThePrefix()
    {
        var leaked = """
            Context: Work Chat with colleague Mark.
            Wait, let's keep it close to dictation.
            end_thought
            Thanks Mark, I'll send the notes.
            """;
        Assert.Equal("Thanks Mark, I'll send the notes.", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void AStartThoughtWithNoCloserDropsTheTail()
    {
        var leaked = "Thanks Mark, I'll send the notes.\nstart_thought\nStill checking the spelling.";
        Assert.Equal("Thanks Mark, I'll send the notes.\n", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void TwoBlocksAreBothRemoved()
    {
        var leaked = "start_thought first end_thought Hello start_thought second end_thought there";
        Assert.Equal("Hello  there", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void AnOrphanEndThoughtBeforeAnUnclosedStartThoughtIsStillRemoved()
    {
        Assert.Equal("B", TextCleanup.StripLeakedThought("A end_thought B start_thought C"));
    }

    [Fact]
    public void NormalizeReplyStripsALeakedBlockBeforeTheWhitespaceCleanup()
    {
        var leaked = "start_thought Check spelling... end_thought\nHallo Mark";
        Assert.Equal("Hallo Mark", TextCleanup.NormalizeReply(leaked));
    }

    // ---- Leaked planning block (Mac #457) ----

    private const string ExampleAPlanning = """
        Notice from the audio:
        "Hi Christina, wir haben ...?"

        User voice style:
        Hi Kristina,
        Work chat register, informal.
        Keep it concise and clear.
        """;

    private const string ExampleAAnswer =
        "Hi Christina, wir haben für die IT mal drei Telekom SIM-Karten bestellt. Kannst du mal schauen, was das im Monat kostet?";

    private const string ExampleBPlanning = """
        The user dictates an email to "Herr Irion".
        Content:
        - Two open mediCAD invoices

        Formatting:
        - E-Mail style

        Details:
        "Hallo Herr Irion, ..."

        Check prompt constraints:
        - No markdown bold/headers.
        - Return ONLY plain text.
        """;

    private const string ExampleBAnswer = """
        Hallo Herr Irion,

        bei mir liegen noch zwei offene Rechnungen.

        Mit freundlichen Grüßen
        """;

    [Fact]
    public void ExampleAAsTwoPartsDropsThePlanningPart() =>
        Assert.Equal(ExampleAAnswer, TextCleanup.StripLeakedPlanning([ExampleAPlanning, ExampleAAnswer]));

    [Fact]
    public void ExampleBAsTwoPartsDropsThePlanningPart() =>
        Assert.Equal(ExampleBAnswer, TextCleanup.StripLeakedPlanning([ExampleBPlanning, ExampleBAnswer]));

    [Fact]
    public void ExampleBGluedIntoOnePartKeepsTheEmail() =>
        Assert.Equal(ExampleBAnswer, TextCleanup.StripLeakedPlanning([ExampleBPlanning + ExampleBAnswer]));

    [Fact]
    public void ExampleAGluedIntoOnePartKeepsTheMessage() =>
        Assert.Equal(ExampleAAnswer, TextCleanup.StripLeakedPlanning([ExampleAPlanning + ExampleAAnswer]));

    [Fact]
    public void ANormalGermanAnswerIsUnchanged()
    {
        var answer = "Hallo Mark,\n\nkurz: Danke.";
        Assert.Equal(answer, TextCleanup.StripLeakedPlanning([answer]));
        Assert.False(TextCleanup.LooksLikePlanning("Hallo Herr Irion,"));
        Assert.False(TextCleanup.LooksLikePlanning("Betreff: Rechnung"));
    }

    [Fact]
    public void ATwoPartAnswerThatIsNotPlanningIsJoined() =>
        Assert.Equal("Hallo Mark", TextCleanup.StripLeakedPlanning(["Hallo ", "Mark"]));

    [Fact]
    public void AFormattingLabelWithNoGluePointIsUnchanged()
    {
        var text = "Formatting: keep this whole note.";
        Assert.Equal(text, TextCleanup.StripLeakedPlanning([text]));
    }

    [Fact]
    public void APlanningPartIsDroppedEvenWhenTheAnswerIsNotGluedToIt()
    {
        var planning = "Notice from the audio:\n\"Hi?\"";
        var answer = "Hi Christina, danke.";
        Assert.Equal(answer, TextCleanup.StripLeakedPlanning([planning, answer]));
    }

    [Fact]
    public void WhenEveryPartIsPlanningTheJoinedTextIsKept()
    {
        string[] parts = ["Plan: draft the note.", "Formatting: plain text."];
        Assert.Equal(string.Concat(parts), TextCleanup.StripLeakedPlanning(parts));
    }

    [Fact]
    public void AGluePointAcceptsUnicodeUppercaseLetters()
    {
        Assert.Equal("Ähne", TextCleanup.StripLeakedPlanning(["Task: note.Ähne"]));
        Assert.Equal("Öl", TextCleanup.StripLeakedPlanning(["Task: note.Öl"]));
        Assert.Equal("Übung", TextCleanup.StripLeakedPlanning(["Task: note.Übung"]));
    }

    [Fact]
    public void ExtractTextDropsASeparatePlanningPartAndSkipsThoughtParts()
    {
        var planning = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Notice from the audio:\\n\\\"Hi?\\\"\"},{\"text\":\"Hi Christina, danke.\"}]}}]}";
        Assert.Equal("Hi Christina, danke.", GeminiClient.ExtractText(planning));

        var thought = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"thinking...\",\"thought\":true},{\"text\":\"Hallo Mark\"}]}}]}";
        Assert.Equal("Hallo Mark", GeminiClient.ExtractText(thought));

        var leaked = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"start_thought Check spelling... end_thought\\nHallo Mark\"}]}}]}";
        Assert.Equal("Hallo Mark", TextCleanup.NormalizeReply(GeminiClient.ExtractText(leaked)));
    }
}
