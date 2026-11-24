namespace WhisperShortcut.Core;

/// <summary>
/// One cloud provider as the settings, the welcome window and the MCP catalog see it: its engine, the name in messages, where
/// its key is created, where the key lives in <see cref="Settings"/> and what the MCP catalog says when it is missing.
/// <see cref="CloudProviders.All"/> is the one table; the wire profile (URLs, auth, error mapping) is <see cref="CloudApi"/>.
/// </summary>
/// <param name="Engine">The engine this provider serves.</param>
/// <param name="Name">"Gemini" or "OpenAI".</param>
/// <param name="KeyUrl">Page where the user creates an API key.</param>
/// <param name="GetKey">Reads the stored key.</param>
/// <param name="SetKey">Stores a key.</param>
/// <param name="MissingKeyText">Explanation for AI apps (MCP) when no key is entered.</param>
public sealed record CloudProvider(Engine Engine, string Name, string KeyUrl, Func<Settings, string> GetKey,
    Action<Settings, string> SetKey, string MissingKeyText)
{
    /// <summary>True when <paramref name="settings"/> holds a non-blank key for this provider.</summary>
    public bool HasKey(Settings settings) => !string.IsNullOrWhiteSpace(GetKey(settings));
}

/// <summary>The table of cloud providers (Gemini, OpenAI). <see cref="Engine.Local"/> has none.</summary>
public static class CloudProviders
{
    public static readonly CloudProvider Gemini = new(Engine.Gemini, "Gemini", "https://aistudio.google.com/apikey",
        s => s.ApiKey, (s, k) => s.ApiKey = k,
        "No Gemini API key in WhisperShortcut. The user enters it in Settings → Allgemein; AI apps cannot set keys.");

    public static readonly CloudProvider OpenAi = new(Engine.OpenAI, "OpenAI", "https://platform.openai.com/api-keys",
        s => s.OpenAiKey, (s, k) => s.OpenAiKey = k,
        "No OpenAI API key in WhisperShortcut. The user enters it in Settings → Allgemein; AI apps cannot set keys.");

    public static readonly IReadOnlyList<CloudProvider> All = new[] { Gemini, OpenAi };

    /// <summary>The provider of <paramref name="engine"/>; null for <see cref="Engine.Local"/>.</summary>
    public static CloudProvider? For(Engine engine) => All.FirstOrDefault(p => p.Engine == engine);
}
