using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void RedactsGeminiAndOpenAiAndBearer()
    {
        Assert.Equal("key AIza… leaked", SecretRedactor.Redact("key AIzaSyA1234567890abcdefghijklmnop leaked"));
        Assert.Equal("k=sk-…", SecretRedactor.Redact("k=sk-proj-abcdefghijklmnopqrstuvwxyz1234"));
        Assert.Equal("k=sk-…", SecretRedactor.Redact("k=sk-abcdefghijklmnopqrstuvwxyz123456"));
        Assert.Equal("Authorization: Bearer …", SecretRedactor.Redact("Authorization: Bearer super-secret-token"));
        Assert.Equal("x-goog-api-key …", SecretRedactor.Redact("x-goog-api-key secret-key-value"));
    }

    [Fact]
    public void LeavesShortAndPlainText()
    {
        Assert.Equal("AIza short", SecretRedactor.Redact("AIza short"));
        Assert.Equal("sk-abc", SecretRedactor.Redact("sk-abc"));
        Assert.Equal("nothing here", SecretRedactor.Redact("nothing here"));
    }
}
