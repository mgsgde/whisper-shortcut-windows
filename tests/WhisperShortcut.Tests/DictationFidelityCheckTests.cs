using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// The Dictate Prompt output may reorder and normalize the dictation but must not add content.
/// These cases pin what passes (reordering, number and segment normalization, inflection) and what is flagged.
/// </summary>
public class DictationFidelityCheckTests
{
    private const string Transcript =
        "Patient klagt seit fünf Tagen über Schmerzen im Nacken links, morgens schlimmer, kein Schwindel. Befund C5 C6 links eingeschränkt. Levator scapulae links hyperton. Rotation nach links sechzig Grad.";

    private static IReadOnlyList<string> Check(string output, params string?[] sources) =>
        DictationFidelityCheck.UnsupportedWords(output, sources);

    [Fact]
    public void FaithfulReorder_WithHeadingsAndMarker_Passes()
    {
        var output = "Anamnese:\nPatient klagt seit 5 Tagen über Schmerzen im Nacken links, morgens schlimmer, kein Schwindel.\n\nBefund:\nC5/C6 links eingeschränkt. Levator scapulae links hyperton. Rotation nach links 60 Grad.\n\nTherapie:\n(nicht diktiert)";
        Assert.Empty(Check(output, Transcript));
    }

    [Fact]
    public void InventedTherapy_IsFlagged()
    {
        var output = "Therapie:\nMobilisation C5/C6 links, Muskel-Energie-Technik Levator scapulae links, Dehnübung mitgegeben. Nächster Termin in einer Woche.";
        var flagged = Check(output, Transcript);
        foreach (var w in new[] { "Mobilisation", "Muskel", "Energie", "Technik", "Dehnübung", "mitgegeben", "Nächster", "Termin", "Woche" })
            Assert.Contains(w, flagged);
        foreach (var w in new[] { "links", "Levator", "C5" })
            Assert.DoesNotContain(w, flagged);
    }

    [Fact]
    public void InflectedForms_Match()
    {
        Assert.Empty(Check("kraniosakraler Technik", "kraniosakrale Technik"));
        Assert.Empty(Check("Schmerz", "Schmerzen"));
    }

    [Fact]
    public void SegmentNormalization_Passes() =>
        Assert.Empty(Check("L4/L5 und Th12/L1", "L4 L5 und Th12 L1"));

    [Fact]
    public void Markers_AreNotFlagged() =>
        Assert.Empty(Check("Psoas links [?] hyperton [Zuordnung prüfen]", "Psoas links hyperton"));

    [Fact]
    public void CaseDoesNotMatter() => Assert.Empty(Check("Psoas", "psoas"));

    [Fact]
    public void AddedAbbreviation_IsFlagged() =>
        Assert.Equal(new[] { "M" }, Check("M. psoas links hyperton", "Psoas links hyperton"));

    [Fact]
    public void InventedDiagnosis_IsFlaggedAlthoughItsHeadingLineIsSkipped() =>
        Assert.Equal(new[] { "Lumbago" }, Check("Diagnose:\nLumbago", "Patient klagt über Schmerzen im Rücken"));

    [Fact]
    public void SelectionCountsAsSupportedSource() =>
        Assert.Empty(Check("Lumbago links", "links", "Lumbago"));

    [Fact]
    public void DictatedCompoundSplitInTwoWords_MatchesJoinedWord() =>
        Assert.Empty(Check("Lendenwirbelsäule", "Lenden Wirbelsäule"));

    [Fact]
    public void RepeatedInventedWords_ReportedOnceInFirstOccurrenceOrder() =>
        Assert.Equal(new[] { "Zebra", "Apfel", "Birne" }, Check("Zebra Apfel zebra Apfel Birne", "links"));

    [Fact]
    public void WrongNumber_IsFlagged() =>
        Assert.Equal(new[] { "45" }, Check("Rotation 45 Grad", "Rotation sechzig Grad"));

    [Fact]
    public void FieldNameWithValue_StaysChecked() =>
        Assert.Equal(new[] { "rechts" }, Check("Befund: Psoas rechts", "Psoas links"));

    [Fact]
    public void MarkdownHeadingLines_AreNotFlagged() =>
        Assert.Empty(Check("## Befund\nPsoas links\n**Therapie:**\n(nicht diktiert)", "Psoas links"));

    [Fact]
    public void NullAndBlankSources_AreIgnored() =>
        Assert.Empty(Check("Psoas", null, "  ", "psoas"));
}
