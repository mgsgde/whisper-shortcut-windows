using System.Runtime.InteropServices;

namespace WhisperShortcut.Core;

/// <summary>One file of an offline component, pinned by size and SHA-256.</summary>
public sealed record ModelFile(string RelativePath, string Url, long Size, string Sha256);

/// <summary>A downloadable unit (speech model, llama.cpp build, one LLM) living in its own folder under the offline root.</summary>
public sealed record OfflineComponent(string Id, string DisplayName, string Directory,
    IReadOnlyList<ModelFile> Files, bool ExtractZip = false)
{
    /// <summary>Absolute folder of the component.</summary>
    public string FullPath => Path.Combine(OfflineCatalog.Root, Directory);
}

/// <summary>A selectable local language model.</summary>
public sealed record LocalLlm(string Id, string DisplayName, string Description,
    OfflineComponent Component, long MinRamBytes)
{
    /// <summary>Absolute path of the GGUF file.</summary>
    public string GgufPath => Path.Combine(Component.FullPath, Component.Files[0].RelativePath);
}

/// <summary>What offline mode downloads: pinned URLs, sizes and hashes.</summary>
public static class OfflineCatalog
{
    private const long GiB = 1024L * 1024 * 1024;
    private const string ParakeetBase = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/";
    private const string LlamaBase = "https://github.com/ggml-org/llama.cpp/releases/download/b11755/";
    private const string UnslothBase = "https://huggingface.co/unsloth/";

    /// <summary>%LOCALAPPDATA%\WhisperShortcut\Offline; env WSW_OFFLINE_ROOT overrides it (tests).</summary>
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("WSW_OFFLINE_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhisperShortcut", "Offline");
        }
    }

    /// <summary>Parakeet TDT 0.6B v3 (int8) for sherpa-onnx.</summary>
    public static OfflineComponent Parakeet { get; } = new(
        "parakeet-tdt-0.6b-v3-int8",
        "Spracherkennung (Parakeet, ca. 670 MB)",
        "parakeet-tdt-0.6b-v3-int8",
        new ModelFile[]
        {
            new("encoder.int8.onnx", ParakeetBase + "encoder.int8.onnx", 652184281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
            new("decoder.int8.onnx", ParakeetBase + "decoder.int8.onnx", 11845275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
            new("joiner.int8.onnx", ParakeetBase + "joiner.int8.onnx", 6355277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
            new("tokens.txt", ParakeetBase + "tokens.txt", 93939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        });

    /// <summary>
    /// The llama.cpp b11755 build for the CPU architecture. X64 uses the Vulkan build (GPU when
    /// there is one, its CPU backends otherwise).
    /// </summary>
    public static OfflineComponent LlamaServer(Architecture arch)
    {
        var (variant, zip, size, sha) = arch switch
        {
            Architecture.X64 => ("vulkan-x64", "llama-b11755-bin-win-vulkan-x64.zip", 36751534L,
                "d7f2e1052fa32932f89a04ad507c4c4bedbf02436681871bec3db41e40b14ab8"),
            Architecture.Arm64 => ("cpu-arm64", "llama-b11755-bin-win-cpu-arm64.zip", 12610059L,
                "3aba7dcf6700d5982ce75a0b6a38777d9bc542ed262a4dce9b8cba0f87c16e13"),
            _ => throw new PlatformNotSupportedException($"Kein lokales Sprachmodell für {arch}."),
        };
        return new OfflineComponent(
            "llama.cpp-b11755-" + variant,
            "Programm für das Sprachmodell (llama.cpp)",
            "llama.cpp-b11755-" + variant,
            new ModelFile[] { new(zip, LlamaBase + zip, size, sha) },
            ExtractZip: true);
    }

    /// <summary>Full path of llama-server.exe for the architecture (inside the extracted zip).</summary>
    public static string LlamaServerExe(Architecture arch) =>
        Path.Combine(LlamaServer(arch).FullPath, "llama-server.exe");

    /// <summary>Selectable local language models, best quality first.</summary>
    public static IReadOnlyList<LocalLlm> Llms { get; } = new LocalLlm[]
    {
        new("qwen3.5-9b", "Qwen3.5 9B", "Beste Qualität, ca. 5,7 GB, ab 16 GB Arbeitsspeicher",
            new OfflineComponent("qwen3.5-9b", "Sprachmodell Qwen3.5 9B (ca. 5,7 GB)", "qwen3.5-9b",
                new ModelFile[]
                {
                    new("Qwen3.5-9B-Q4_K_M.gguf", UnslothBase + "Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf",
                        5680522464, "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8"),
                }),
            16 * GiB),
        new("qwen3-4b-instruct-2507", "Qwen3 4B", "Schneller, ca. 2,5 GB, ab 8 GB Arbeitsspeicher",
            new OfflineComponent("qwen3-4b-instruct-2507", "Sprachmodell Qwen3 4B (ca. 2,5 GB)", "qwen3-4b-instruct-2507",
                new ModelFile[]
                {
                    new("Qwen3-4B-Instruct-2507-Q4_K_M.gguf", UnslothBase + "Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
                        2497281120, "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597"),
                }),
            8 * GiB),
    };

    /// <summary>The model with this id; unknown or empty gives the one recommended for this PC's RAM.</summary>
    public static LocalLlm LlmById(string? id)
    {
        var hit = Llms.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
        return hit ?? Recommended(TotalRamBytes());
    }

    /// <summary>16 GiB or more gets the 9B, everything below the 4B.</summary>
    public static LocalLlm Recommended(long totalRamBytes) =>
        totalRamBytes >= 16 * GiB ? Llms[0] : Llms[1];

    private static long TotalRamBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
}
