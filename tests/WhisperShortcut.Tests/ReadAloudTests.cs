using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ReadAloudTests
{
    [Fact]
    public void Models_AreInMacOrder_WithGermanDescriptions()
    {
        var ids = ReadAloud.Models.Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "gemini-3.8-flash-lite-tts", "gemini-3.8-flash-tts", "gpt-4o-mini-tts", "system-windows" }, ids);
        Assert.Equal(TtsProvider.System, ReadAloud.Models[3].Provider);
        Assert.Equal(ReadAloud.DefaultModel, ids[0]);
        Assert.Equal(TtsProvider.Gemini, ReadAloud.Models[1].Provider);
        Assert.Equal(TtsProvider.OpenAI, ReadAloud.Models[2].Provider);
        Assert.Equal("GPT-4o mini TTS", ReadAloud.Models[2].Name);
        Assert.Contains("Empfohlen", ReadAloud.Models[0].Description);
    }

    [Theory]
    [InlineData("gemini-3.8-flash-tts", "gemini-3.8-flash-tts")]
    [InlineData("gpt-4o-mini-tts", "gpt-4o-mini-tts")]
    [InlineData("gemini-2.5-flash-preview-tts", "gemini-3.8-flash-lite-tts")]
    [InlineData("gemini-2.5-pro-preview-tts", "gemini-3.8-flash-lite-tts")]
    [InlineData("gemini-3.1-flash-tts-preview", "gemini-3.8-flash-lite-tts")]
    [InlineData("something-else", "gemini-3.8-flash-lite-tts")]
    [InlineData("", "gemini-3.8-flash-lite-tts")]
    [InlineData(null, "gemini-3.8-flash-lite-tts")]
    public void NormalizeModel_KeepsKnown_MigratesLegacy_ElseDefault(string? id, string expected)
    {
        Assert.Equal(expected, ReadAloud.NormalizeModel(id));
        Assert.Equal(expected, ReadAloud.ModelById(id).Id);
    }

    [Fact]
    public void Voices_HaveTheMacCounts_AndAreSortedMaleFemaleNeutral()
    {
        var gemini = ReadAloud.Voices(TtsProvider.Gemini);
        var openAi = ReadAloud.Voices(TtsProvider.OpenAI);
        Assert.Equal(30, gemini.Count);
        Assert.Equal(13, openAi.Count);
        Assert.Equal(16, gemini.Count(v => v.Gender == "m"));
        Assert.Equal(14, gemini.Count(v => v.Gender == "w"));

        foreach (var list in new[] { gemini, openAi })
        {
            var ranks = list.Select(v => v.Gender == "m" ? 0 : v.Gender == "w" ? 1 : 2).ToArray();
            Assert.Equal(ranks.OrderBy(r => r).ToArray(), ranks);
        }
        // Stable: Charon is the first Gemini voice in the Mac list and a male one, Zephyr the first female one.
        Assert.Equal("Charon", gemini[0].Id);
        Assert.Equal("Zephyr", gemini.First(v => v.Gender == "w").Id);
        // OpenAI: alloy (neutral) comes last, ash is the first male voice, coral the first female one.
        Assert.Equal("ash", openAi[0].Id);
        Assert.Equal("coral", openAi.First(v => v.Gender == "w").Id);
        Assert.Equal("alloy", openAi[^1].Id);
    }

    [Fact]
    public void DisplayName_UsesFirstLetterUpperCase_GenderAndGermanDescriptor()
    {
        Assert.Equal("Charon (m) – Informativ", ReadAloud.Voices(TtsProvider.Gemini).First(v => v.Id == "Charon").DisplayName);
        Assert.Equal("Alloy (neutral)", ReadAloud.Voices(TtsProvider.OpenAI).First(v => v.Id == "alloy").DisplayName);
        Assert.Equal("Marin (w) – Empfohlen", ReadAloud.Voices(TtsProvider.OpenAI).First(v => v.Id == "marin").DisplayName);
        Assert.Equal("Callirrhoe (w) – Locker", ReadAloud.Voices(TtsProvider.Gemini).First(v => v.Id == "Callirrhoe").DisplayName);
    }

    [Fact]
    public void EveryGeminiVoice_HasAGermanDescriptor()
    {
        var english = new[] { "Informative", "Bright", "Upbeat", "Firm", "Excitable", "Youthful", "Breezy", "Easy-going", "Breathy",
            "Clear", "Smooth", "Gravelly", "Soft", "Even", "Mature", "Forward", "Friendly", "Casual", "Gentle", "Lively",
            "Knowledgeable", "Recommended" };
        foreach (var v in ReadAloud.Voices(TtsProvider.Gemini))
        {
            Assert.NotEqual("", v.Descriptor);
            Assert.DoesNotContain(v.Descriptor, english);
        }
    }

    [Fact]
    public void DefaultAndNormalizeVoice()
    {
        Assert.Equal("Charon", ReadAloud.DefaultVoice(TtsProvider.Gemini));
        Assert.Equal("alloy", ReadAloud.DefaultVoice(TtsProvider.OpenAI));
        Assert.Equal("Kore", ReadAloud.NormalizeVoice(TtsProvider.Gemini, "Kore"));
        Assert.Equal("Charon", ReadAloud.NormalizeVoice(TtsProvider.Gemini, "kore"));
        Assert.Equal("Charon", ReadAloud.NormalizeVoice(TtsProvider.Gemini, "alloy"));
        Assert.Equal("alloy", ReadAloud.NormalizeVoice(TtsProvider.OpenAI, "Kore"));
        Assert.Equal("alloy", ReadAloud.NormalizeVoice(TtsProvider.OpenAI, null));
        Assert.Equal("cedar", ReadAloud.NormalizeVoice(TtsProvider.OpenAI, "cedar"));
    }

    [Fact]
    public void Speeds_NormalizeNextAndLabel()
    {
        Assert.Equal(new[] { 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 }, ReadAloud.Speeds.ToArray());
        Assert.Equal(1.0, ReadAloud.DefaultSpeed);
        Assert.Equal(1.25, ReadAloud.NormalizeSpeed(1.2500001));
        Assert.Equal(1.0, ReadAloud.NormalizeSpeed(1.3));
        Assert.Equal(1.0, ReadAloud.NormalizeSpeed(double.NaN));

        Assert.Equal(1.0, ReadAloud.NextSpeed(0.75));
        Assert.Equal(1.25, ReadAloud.NextSpeed(1.0));
        Assert.Equal(2.0, ReadAloud.NextSpeed(1.75));
        Assert.Equal(0.75, ReadAloud.NextSpeed(2.0));

        Assert.Equal(new[] { "0,75×", "1×", "1,25×", "1,5×", "1,75×", "2×" },
            ReadAloud.Speeds.Select(ReadAloud.SpeedLabel).ToArray());
    }

    [Fact]
    public void ChunkSizes()
    {
        Assert.Equal(260, ReadAloud.ChunkSizeFor(TtsProvider.Gemini));
        Assert.Equal(500, ReadAloud.ChunkSizeFor(TtsProvider.OpenAI));
        Assert.Equal(120, ReadAloud.FirstChunkSizeChars);
        Assert.Equal(0.085, ReadAloud.EstimatedSecondsPerCharacter);
    }

    [Fact]
    public void RewriteLengthBudget_AppliesFrom200Chars_At1Point3()
    {
        // Short inputs may grow freely.
        Assert.True(ReadAloud.RewriteStaysWithinLengthBudget(new string('x', 400), new string('a', 199)));
        var original = new string('a', 200);
        Assert.True(ReadAloud.RewriteStaysWithinLengthBudget(new string('x', 260), original));   // exactly 1.3x
        Assert.False(ReadAloud.RewriteStaysWithinLengthBudget(new string('x', 261), original));
        Assert.True(ReadAloud.RewriteStaysWithinLengthBudget("kurz", new string('a', 1000)));
    }

    [Fact]
    public void DefaultRewritePrompt_IsTheMacPrompt()
    {
        Assert.StartsWith("You prepare text for text-to-speech playback.", ReadAloud.DefaultRewritePrompt);
        Assert.EndsWith("return the input unchanged.", ReadAloud.DefaultRewritePrompt);
        Assert.Contains("Output rules (CRITICAL):", ReadAloud.DefaultRewritePrompt);
    }

    [SwiftSourceFact]
    public void DefaultRewritePrompt_MatchesTheSwiftSource()
    {
        var swift = File.ReadAllText(Paths.SwiftFile("AppConstants.swift"));
        var i = swift.IndexOf("static let defaultReadAloudRewritePrompt", StringComparison.Ordinal);
        var a = swift.IndexOf("\"\"\"\n", i, StringComparison.Ordinal) + 4;
        var b = swift.IndexOf("\n\"\"\"", a, StringComparison.Ordinal);
        Assert.Equal(swift[a..b].Replace("\r\n", "\n"), ReadAloud.DefaultRewritePrompt.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Effective_IsWindowsVoiceOffline_StoredChoiceOnline()
    {
        Assert.Equal(ReadAloud.SystemModel, ReadAloud.Effective("gpt-4o-mini-tts", offlineMode: true).Id);
        Assert.Equal("gpt-4o-mini-tts", ReadAloud.Effective("gpt-4o-mini-tts", offlineMode: false).Id);
        Assert.Equal(ReadAloud.DefaultModel, ReadAloud.Effective("unknown", offlineMode: false).Id);
        Assert.Equal(ReadAloud.SystemModel, ReadAloud.Effective(ReadAloud.SystemModel, offlineMode: false).Id);
    }

    [Fact]
    public void SystemVoices_KeepStoredName_NoStaticList()
    {
        Assert.Empty(ReadAloud.Voices(TtsProvider.System));
        Assert.Equal("", ReadAloud.DefaultVoice(TtsProvider.System));
        Assert.Equal("Microsoft Katja Desktop", ReadAloud.NormalizeVoice(TtsProvider.System, " Microsoft Katja Desktop "));
        Assert.Equal("", ReadAloud.NormalizeVoice(TtsProvider.System, null));
        Assert.Equal(500, ReadAloud.ChunkSizeFor(TtsProvider.System));
    }

    [Fact]
    public void PickSystemVoice_StoredThenUiLanguageThenFirst()
    {
        var installed = new[] { ("Microsoft Zira Desktop", "en-US"), ("Microsoft Hedda Desktop", "de-DE"), ("Microsoft Karsten", "de-CH") };
        Assert.Equal("Microsoft Zira Desktop", ReadAloud.PickSystemVoice("Microsoft Zira Desktop", installed, "de-DE"));
        Assert.Equal("Microsoft Hedda Desktop", ReadAloud.PickSystemVoice("Gone", installed, "de-DE"));
        Assert.Equal("Microsoft Karsten", ReadAloud.PickSystemVoice("", installed, "de-CH"));
        Assert.Equal("Microsoft Hedda Desktop", ReadAloud.PickSystemVoice(null, installed, "de-AT"));
        Assert.Equal("Microsoft Zira Desktop", ReadAloud.PickSystemVoice(null, installed, "fr-FR"));
        Assert.Null(ReadAloud.PickSystemVoice("x", Array.Empty<(string, string)>(), "de-DE"));
    }

    [Fact]
    public void SystemVoices_UiLanguageFirst_ThenCultureAndName()
    {
        var voices = ReadAloud.SystemVoices(new[]
        {
            ("Microsoft Zira Desktop", "en-US", "w"), ("Microsoft Stefan", "de-DE", "m"), ("Microsoft Hedda Desktop", "de-DE", "w"),
            ("Microsoft David Desktop", "en-US", "m"),
        }, "de-DE");
        Assert.Equal(new[] { "Microsoft Hedda Desktop", "Microsoft Stefan", "Microsoft David Desktop", "Microsoft Zira Desktop" },
            voices.Select(v => v.Id).ToArray());
        Assert.Equal("Microsoft Hedda Desktop (w) \u2013 de-DE", voices[0].DisplayName);
    }
}
