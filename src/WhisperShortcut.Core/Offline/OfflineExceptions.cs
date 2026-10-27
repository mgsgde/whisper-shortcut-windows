namespace WhisperShortcut.Core;

/// <summary>A model download failed; the message is the German user text.</summary>
public sealed class OfflineDownloadException : Exception
{
    /// <summary>Creates the error with the user message.</summary>
    public OfflineDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The local language model could not be started or did not answer; the message is the German user text.</summary>
public class LocalLlmException : Exception
{
    /// <summary>Creates the error with the user message.</summary>
    public LocalLlmException(string message, Exception? inner = null) : base(message, inner) { }
}


/// <summary>llama-server died with an illegal instruction: this CPU cannot run the llama.cpp build.</summary>
public sealed class UnsupportedCpuException : LocalLlmException
{
    /// <summary>Creates the error with the user message.</summary>
    public UnsupportedCpuException(string message) : base(message) { }
}
