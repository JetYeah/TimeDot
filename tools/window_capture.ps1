Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    public static IntPtr FindByPid(uint pid) {
        IntPtr found = IntPtr.Zero; int largest = 0;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > largest) { largest = area; found = h; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$proc = Get-Process TimeDot -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $proc) { Write-Output 'TimeDot not running'; exit 1 }
$procId = [uint32]$proc.Id
$hwnd = [WinCap]::FindByPid($procId)
Write-Output ("hwnd='{0}'" -f $hwnd)
if ($hwnd -eq [IntPtr]::Zero) { Write-Output 'main window not found'; exit 1 }
Add-Type -AssemblyName System.Drawing
$r = New-Object WinCap+RECT
$null = [WinCap]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
Write-Output ("rect=({0},{1}) {2}x{3}" -f $r.Left, $r.Top, $w, $h)
if ($w -le 0 -or $h -le 0) { Write-Output 'invalid rect'; exit 1 }
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save('d:\ClaudeCode\TimeDot-main\dist\record_visual_verify.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("printwindow ok={0} saved pid={1}" -f $ok, $procId)
