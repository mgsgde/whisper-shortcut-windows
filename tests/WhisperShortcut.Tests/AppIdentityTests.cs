using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class AppIdentityTests
{
    private const string Roaming = @"C:\Users\x\AppData\Roaming";
    private const string Local = @"C:\Users\x\AppData\Local";
    private static readonly AppIdentity Normal = new(false, Roaming, Local);
    private static readonly AppIdentity Dev = new(true, Roaming, Local);

    [Fact]
    public void Names_NormalVsDev()
    {
        Assert.Equal("WhisperShortcut", Normal.DisplayName);
        Assert.Equal("WhisperShortcut Dev", Dev.DisplayName);
        Assert.Equal(@"Local\WhisperShortcut.Windows", Normal.MutexName);
        Assert.Equal(@"Local\WhisperShortcut.Windows.Dev", Dev.MutexName);
        Assert.Equal(@"Local\WhisperShortcut.Windows.Show", Normal.ShowEventName);
        Assert.Equal(@"Local\WhisperShortcut.Windows.Dev.Quit", Dev.QuitEventName);
        Assert.Equal("WhisperShortcut", Normal.AutoStartValueName);
        Assert.Equal("WhisperShortcut Dev", Dev.AutoStartValueName);
    }

    [Fact]
    public void Other_SwapsTheIdentity_AndPointsAtTheOthersQuitEvent()
    {
        Assert.Equal(Dev.QuitEventName, Normal.OtherQuitEventName);
        Assert.Equal(Normal.QuitEventName, Dev.OtherQuitEventName);
        Assert.Equal(Dev.InstallExe, Normal.OtherInstallExe);
    }

    [Fact]
    public void DataPaths_DevHasItsOwn()
    {
        Assert.Equal(Path.Combine(Roaming, "WhisperShortcut", "settings.json"), Normal.SettingsPath);
        Assert.Equal(Path.Combine(Roaming, "WhisperShortcut Dev", "settings.json"), Dev.SettingsPath);
        Assert.Equal(Path.Combine(Local, "WhisperShortcut", "wsw.log"), Normal.LogPath);
        Assert.Equal(Path.Combine(Local, "WhisperShortcut Dev", "wsw.log"), Dev.LogPath);
        Assert.Equal(Path.Combine(Local, "Programs", "WhisperShortcut"), Normal.InstallDir);
        Assert.Equal(Path.Combine(Local, "Programs", "WhisperShortcut Dev"), Dev.InstallDir);
    }

    [Fact]
    public void OfflineRoot_IsSharedAndStaysTheNormalApps()
    {
        Assert.Equal(Normal.OfflineRoot, Dev.OfflineRoot);
        Assert.Equal(Path.Combine(Local, "WhisperShortcut", "Offline"), Dev.OfflineRoot);
        Assert.False(Dev.OfflineRoot.StartsWith(Dev.LocalFolder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dev_HasNoUpdaterAndRegistersNoSystemEntries()
    {
        Assert.True(Normal.UpdatesEnabled);
        Assert.False(Dev.UpdatesEnabled);
        Assert.True(Normal.MayRegisterSystemEntries);
        Assert.False(Dev.MayRegisterSystemEntries);
    }

    [Fact]
    public void Marker_MakesTheExeDev()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(AppIdentity.MarkerExists(dir));
            File.WriteAllText(Path.Combine(dir, AppIdentity.MarkerFileName), "");
            Assert.True(AppIdentity.MarkerExists(dir));
            Assert.False(AppIdentity.MarkerExists(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LiveMeetingMarker_WrittenReadAndDeleted()
    {
        var local = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var id = new AppIdentity(true, Roaming, local);
        try
        {
            Assert.Null(AppIdentity.ReadLiveMeetingMarker(id.LiveMeetingMarkerPath));
            var started = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
            id.WriteLiveMeetingMarker(4242, started);

            Assert.Equal(Path.Combine(local, "WhisperShortcut Dev", "live-meeting-recording.json"), id.LiveMeetingMarkerPath);
            var read = AppIdentity.ReadLiveMeetingMarker(id.LiveMeetingMarkerPath);
            Assert.NotNull(read);
            Assert.Equal(4242, read!.Value.Pid);
            Assert.Equal(started, read.Value.Started.ToUniversalTime());
            Assert.Contains("\"pid\":4242", File.ReadAllText(id.LiveMeetingMarkerPath));

            id.DeleteLiveMeetingMarker();
            Assert.False(File.Exists(id.LiveMeetingMarkerPath));
            id.DeleteLiveMeetingMarker(); // already gone: no throw
        }
        finally { if (Directory.Exists(local)) Directory.Delete(local, true); }
    }

    [Fact]
    public void LiveMeetingMarker_Garbage_ReadsAsNull()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "not json");
        try { Assert.Null(AppIdentity.ReadLiveMeetingMarker(file)); }
        finally { File.Delete(file); }
    }
}

public class ComponentLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void PathFor_IsPerComponentBelowLocks()
    {
        Assert.Equal(Path.Combine(_root, ".locks", "parakeet.lock"), ComponentLock.PathFor(_root, "parakeet"));
    }

    [Fact]
    public void SecondAcquireFails_UntilTheFirstIsReleased()
    {
        var first = ComponentLock.TryAcquire(_root, "a");
        Assert.NotNull(first);
        Assert.Null(ComponentLock.TryAcquire(_root, "a"));
        using (var b = ComponentLock.TryAcquire(_root, "b")) Assert.NotNull(b); // other component: independent
        first!.Dispose();
        using var again = ComponentLock.TryAcquire(_root, "a");
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AcquireAsync_WaitsForTheHolder()
    {
        var holder = ComponentLock.TryAcquire(_root, "a")!;
        bool told = false;
        var waiter = ComponentLock.AcquireAsync(_root, "a", CancellationToken.None, TimeSpan.FromMilliseconds(20), () => told = true);
        await Task.Delay(150);
        Assert.False(waiter.IsCompleted);
        Assert.True(told);
        holder.Dispose();
        using var got = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(got);
    }

    [Fact]
    public async Task AcquireAsync_CancelStopsWaiting()
    {
        using var holder = ComponentLock.TryAcquire(_root, "a")!;
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ComponentLock.AcquireAsync(_root, "a", cts.Token, TimeSpan.FromMilliseconds(20)));
    }
}
