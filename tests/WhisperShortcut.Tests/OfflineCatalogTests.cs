using System.Runtime.InteropServices;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class OfflineCatalogTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Recommended_ByRam()
    {
        Assert.Equal("qwen3.5-9b", OfflineCatalog.Recommended(16 * GiB).Id);
        Assert.Equal("qwen3-4b-instruct-2507", OfflineCatalog.Recommended(16 * GiB - 1).Id);
        Assert.Equal("qwen3-4b-instruct-2507", OfflineCatalog.Recommended(8 * GiB).Id);
    }

    [Fact]
    public void LlmById_KnownAndUnknown()
    {
        Assert.Equal("qwen3-4b-instruct-2507", OfflineCatalog.LlmById("qwen3-4b-instruct-2507").Id);
        Assert.Equal("qwen3.5-9b", OfflineCatalog.LlmById("qwen3.5-9b").Id);
        Assert.Contains(OfflineCatalog.Llms, l => l.Id == OfflineCatalog.LlmById("").Id);
        Assert.Contains(OfflineCatalog.Llms, l => l.Id == OfflineCatalog.LlmById(null).Id);
    }

    [Fact]
    public void LlamaServer_PerArchitecture()
    {
        var x64 = OfflineCatalog.LlamaServer(Architecture.X64);
        Assert.True(x64.ExtractZip);
        Assert.Equal("llama.cpp-b11755-vulkan-x64", x64.Directory);
        Assert.Equal(36751534, x64.Files[0].Size);
        Assert.Equal("llama.cpp-b11755-cpu-arm64", OfflineCatalog.LlamaServer(Architecture.Arm64).Directory);
        Assert.EndsWith("llama-server.exe", OfflineCatalog.LlamaServerExe(Architecture.X64));
        Assert.Throws<PlatformNotSupportedException>(() => OfflineCatalog.LlamaServer(Architecture.X86));
    }

    [Fact]
    public void Entries_AreWellFormed()
    {
        var all = new List<OfflineComponent> { OfflineCatalog.Parakeet };
        all.AddRange(OfflineCatalog.Llms.Select(l => l.Component));
        foreach (var f in all.SelectMany(c => c.Files))
        {
            Assert.Equal(64, f.Sha256.Length);
            Assert.True(f.Size > 0);
            Assert.StartsWith("https://", f.Url);
        }
        Assert.EndsWith(".gguf", OfflineCatalog.Llms[0].GgufPath);
    }
}
