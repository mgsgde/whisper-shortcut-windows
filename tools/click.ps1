# Left click at screen pixel (x, y) in the VM: tools/vm.sh copies this into the share.
param([int]$X, [int]$Y)
Add-Type @"
using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, System.UIntPtr e);
}
"@
Start-Sleep -Milliseconds 700  # let the hidden console window give the foreground back
[M]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 80
[M]::mouse_event(2, 0, 0, 0, [System.UIntPtr]::Zero)
[M]::mouse_event(4, 0, 0, 0, [System.UIntPtr]::Zero)
"clicked $X $Y"
