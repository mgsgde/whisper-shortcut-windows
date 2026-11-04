using WhisperShortcut.Core;
using WhisperShortcut.Tests.Mcp;

namespace WhisperShortcut.Tests;

public class ConfigurationReportTests
{
    private const string PromptSentinel = "PROMPT_SENTINEL_zebra";
    private const string GlossarySentinel = "GLOSSARY_SENTINEL_quokka";
    private const string GeminiKey = "AIzaSyTESTKEY_should_never_appear_1234567";
    private const string OpenAiKey = "sk-proj-should-never-appear-abcdefghijkl";
    private static readonly ConfigurationReportEnvironment Env = new("9.99", "10.0.26100", "x64");

    private static string Build(McpStack stack, bool glossary = false, bool prompts = false) =>
        ConfigurationReport.Build(new ConfigurationReportOptions(glossary, prompts), stack.Settings, stack.Catalog, Env);

    private static McpStack Stack()
    {
        var stack = new McpStack();
        stack.Settings.DictateSystemPrompt = PromptSentinel;
        stack.Settings.Glossary = new List<string> { GlossarySentinel };
        return stack;
    }

    [Fact]
    public void BaseReportHasSettingsAndNoFreeText()
    {
        using var stack = Stack();
        var report = Build(stack);
        Assert.StartsWith("WhisperShortcut configuration\nApp 9.99 (x64) · Windows 10.0.26100", report);
        Assert.Contains("### models", report);
        Assert.Contains("Spracherkennung (Anbieter): ", report);
        Assert.Contains("dictation: customized,", report);
        Assert.Contains("dictate_prompt: default,", report);
        Assert.DoesNotContain(PromptSentinel, report);
        Assert.DoesNotContain(GlossarySentinel, report);
        Assert.DoesNotContain("## Glossary", report);
        Assert.DoesNotContain("## System prompt texts", report);
    }

    [Fact]
    public void GlossaryOptionShowsOnlyGlossary()
    {
        using var stack = Stack();
        var report = Build(stack, glossary: true);
        Assert.Contains(GlossarySentinel, report);
        Assert.Contains("## Glossary", report);
        Assert.DoesNotContain(PromptSentinel, report);
    }

    [Fact]
    public void PromptsOptionShowsOnlyPrompts()
    {
        using var stack = Stack();
        var report = Build(stack, prompts: true);
        Assert.Contains(PromptSentinel, report);
        Assert.Contains("## System prompt texts", report);
        Assert.DoesNotContain(GlossarySentinel, report);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NoVariantLeaksKeys(bool glossary, bool prompts)
    {
        using var stack = Stack();
        stack.Settings.ApiKey = GeminiKey;
        stack.Settings.OpenAiKey = OpenAiKey;
        var report = Build(stack, glossary, prompts);
        Assert.DoesNotContain(GeminiKey, report);
        Assert.DoesNotContain(OpenAiKey, report);
        Assert.DoesNotContain("AIzaSyTESTKEY", report);
        Assert.DoesNotContain("should-never-appear", report);
    }

    [Fact]
    public void KeyPastedIntoPromptOrGlossaryIsRedacted()
    {
        using var stack = Stack();
        stack.Settings.DictateSystemPrompt = "use " + GeminiKey + " please";
        stack.Settings.DictatePromptTemplate = "tpl " + OpenAiKey;
        stack.Settings.Glossary = new List<string> { OpenAiKey, GeminiKey };
        var report = Build(stack, glossary: true, prompts: true);
        Assert.DoesNotContain("TESTKEY", report);
        Assert.DoesNotContain("should-never-appear", report);
        Assert.Contains("AIza…", report);
        Assert.Contains("sk-…", report);
    }

    [Fact]
    public void MessageOrdersNoteFirstAndDropsEmptyParts()
    {
        Assert.Equal("hello\n\nreport", ConfigurationReport.Message("  hello ", " report\n"));
        Assert.Equal("report", ConfigurationReport.Message("   ", "report"));
        Assert.Equal("report", ConfigurationReport.Message(null, "report"));
    }

    [Fact]
    public void FitsInLinkBoundary()
    {
        Assert.True(ConfigurationReport.FitsInLink(new string('a', 1200)));
        Assert.False(ConfigurationReport.FitsInLink(new string('a', 1201)));
    }

    [Fact]
    public void FreshSettingsStillReportModels()
    {
        using var stack = new McpStack();
        var report = Build(stack);
        Assert.Contains("### models", report);
        Assert.Contains("Spracherkennung (Anbieter): ", report);
        Assert.Contains("dictation: default,", report);
        Assert.Contains("template: none", report);
    }
}
