using System.Reflection;
using Microsoft.Win32;

namespace WhisperShortcut;

/// <summary>
/// The per-user "Apps → Installierte Apps" entry (HKCU, no admin). The app has no installer; this makes Windows
/// list it with a working "Deinstallieren" that runs <c>"&lt;exe&gt;" --uninstall</c>.
/// </summary>
internal static class UninstallEntry
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WhisperShortcut";

    /// <summary>Writes or refreshes the entry for this exe; only values that differ are written. Never throws.</summary>
    public static void Ensure()
    {
        if (!InstallState.MayRegister) return;
        var exe = InstallState.ExePath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            var asm = typeof(UninstallEntry).Assembly;
            var version = asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
            var company = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";
            long sizeKb = 0;
            try { sizeKb = Math.Max(1, new FileInfo(exe).Length / 1024); } catch (Exception) { }
            var values = new (string Name, object Value, RegistryValueKind Kind)[]
            {
                ("DisplayName", "WhisperShortcut", RegistryValueKind.String),
                ("DisplayIcon", $"{exe},0", RegistryValueKind.String),
                ("DisplayVersion", version, RegistryValueKind.String),
                ("Publisher", company, RegistryValueKind.String),
                ("InstallLocation", Path.GetDirectoryName(exe) ?? "", RegistryValueKind.String),
                ("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord),
                ("NoModify", 1, RegistryValueKind.DWord),
                ("NoRepair", 1, RegistryValueKind.DWord),
                ("UninstallString", $"\"{exe}\" --uninstall", RegistryValueKind.String),
            };
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (key == null) return;
            bool changed = false;
            foreach (var (name, value, kind) in values)
            {
                if (key.GetValue(name) is { } old && old.Equals(value) && key.GetValueKind(name) == kind) continue;
                key.SetValue(name, value, kind);
                changed = true;
            }
            if (changed) Core.Log.Info("Uninstall entry written");
        }
        catch (Exception ex)
        {
            Core.Log.Error("Uninstall entry failed", ex);
        }
    }

    /// <summary>Removes the entry (uninstall). Never throws.</summary>
    public static void Remove()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false); }
        catch (Exception ex) { Core.Log.Error("Uninstall entry removal failed", ex); }
    }
}
