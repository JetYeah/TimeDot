param(
    [string]$AppArgs = '--todo-show',
    [string]$OutPath = 'd:\ClaudeCode\TimeDot-main\dist\todo_visual_verify.png'
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TodoVisualNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    public static IntPtr FindByPid(uint pid) {
        IntPtr found = IntPtr.Zero; int largest = 0;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            int area = (r.Right-r.Left)*(r.Bottom-r.Top);
            if (area > largest) { largest = area; found = h; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
[TodoVisualNative]::SetProcessDPIAware() | Out-Null
Get-Process TimeDot -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Milliseconds 800
$p = Start-Process 'd:\ClaudeCode\TimeDot-main\dist\TimeDot.exe' -ArgumentList $AppArgs -PassThru
Start-Sleep -Seconds 5
$p.Refresh()
$hwnd = [TodoVisualNative]::FindByPid([uint32]$p.Id) # ShowInTaskbar=False 时 MainWindowHandle/Title 均不可靠
if ($hwnd -eq 0) { Write-Error 'TimeDot 主窗口句柄不可用'; exit 1 }
$r = New-Object TodoVisualNative+RECT
if (-not [TodoVisualNative]::GetWindowRect($hwnd, [ref]$r)) { Write-Error 'GetWindowRect 失败'; exit 1 }
$pad = 24
$x = [Math]::Max(0, $r.Left - $pad)
$y = [Math]::Max(0, $r.Top - $pad)
$w = $r.Right - $r.Left + $pad * 2
$h = $r.Bottom - $r.Top + $pad * 2
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$path = $OutPath
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output ("window=({0},{1}) {2}x{3}; screenshot={4}; pid={5}" -f $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top), $path, $p.Id)
