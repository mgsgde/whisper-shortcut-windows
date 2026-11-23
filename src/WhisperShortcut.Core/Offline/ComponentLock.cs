namespace WhisperShortcut.Core;

/// <summary>
/// Cross-process lock for one offline component. The normal app and the Dev instance share the models folder, so
/// only one process may download (or delete) a component at a time: it holds an exclusive lock file
/// <c>&lt;Offline root&gt;\.locks\&lt;componentId&gt;.lock</c> (FileShare.None, deleted on close). The other
/// process waits, then finds the component installed. The lock dies with its process, so a crash never blocks.
/// A file lock rather than a named mutex: a Mutex belongs to one thread, and a download awaits across threads.
/// </summary>
public sealed class ComponentLock : IDisposable
{
    private FileStream? _stream;

    private ComponentLock(FileStream stream) => _stream = stream;

    /// <summary>The lock file of a component below an offline root.</summary>
    public static string PathFor(string offlineRoot, string componentId) =>
        Path.Combine(offlineRoot, ".locks", componentId + ".lock");

    /// <summary>Takes the lock if nobody holds it; null otherwise.</summary>
    public static ComponentLock? TryAcquire(string offlineRoot, string componentId)
    {
        var path = PathFor(offlineRoot, componentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new ComponentLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose));
        }
        catch (IOException) { return null; } // held by another process (sharing violation)
        catch (UnauthorizedAccessException) { return null; } // a pending delete of the other holder's file
    }

    /// <summary>Waits (polling) until the lock is free, or the token is cancelled.</summary>
    public static async Task<ComponentLock> AcquireAsync(string offlineRoot, string componentId, CancellationToken ct,
        TimeSpan? poll = null, Action? waiting = null)
    {
        bool told = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var l = TryAcquire(offlineRoot, componentId);
            if (l != null) return l;
            if (!told)
            {
                told = true;
                waiting?.Invoke();
            }
            await Task.Delay(poll ?? TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
