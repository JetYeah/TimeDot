# ext_style_test.ps1 — 外部直接改 TimeDot 主窗 EXSTYLE,验证 layered+transparent 可行性
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ET {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
    public static IntPtr FindMain(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumProc cb = delegate(IntPtr h, IntPtr l) {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp == pid) { var sb = new StringBuilder(256); GetClassName(h, sb, 256);
                if (sb.ToString().StartsWith("HwndWrapper[")) { found = h; return false; } }
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        return found;
    }
}
'@
$td = Get-Process TimeDot -ErrorAction SilentlyContinue
if (-not $td) { $p = Start-Process 'D:\ClaudeCode\TimeDot-main\dist\TimeDot.exe' -PassThru; Start-Sleep -Seconds 6; $td = $p }
$main = [ET]::FindMain([uint32]$td.Id)
$ex0 = [ET]::GetWindowLong($main, -20)
Write-Host ("外部设置前 EXSTYLE=0x{0:X}" -f $ex0)
# 加 LAYERED|TRANSPARENT
$newVal = $ex0 -bor 0x80020
$v = [ET]::SetWindowLong($main, -20, $newVal)
$ex1 = [ET]::GetWindowLong($main, -20)
Write-Host ("SetWindowLong 返回 {0}, 设置后 EXSTYLE=0x{1:X} (LAYERED={2} TRANSPARENT={3})" -f $v, $ex1, (($ex1 -band 0x80000) -ne 0), (($ex1 -band 0x20) -ne 0))
if (($ex1 -band 0x80000) -ne 0) {
    [ET]::SetLayeredWindowAttributes($main, 0, 255, 2) | Out-Null
    Start-Sleep -Milliseconds 300
    $ex2 = [ET]::GetWindowLong($main, -20)
    Write-Host ("SLWA 后 EXSTYLE=0x{0:X}" -f $ex2)
}
foreach ($pt in @(@(400,300),@(960,100))) {
    $pnt = New-Object ET+PT; $pnt.X = $pt[0]; $pnt.Y = $pt[1]
    $hit = [ET]::WindowFromPoint($pnt)
    $sb = New-Object System.Text.StringBuilder 256
    [ET]::GetClassName($hit, $sb, 256) | Out-Null
    $wp = 0; [ET]::GetWindowThreadProcessId($hit, [ref]$wp) | Out-Null
    $pn = '?'; try { $pn = (Get-Process -Id $wp -ErrorAction SilentlyContinue).ProcessName } catch {}
    Write-Host ("WindowFromPoint({0},{1}) = {2}/{3}" -f $pt[0], $pt[1], $pn, $sb)
}
Start-Sleep -Seconds 2
$ex3 = [ET]::GetWindowLong($main, -20)
Write-Host ("2秒后 EXSTYLE=0x{0:X} (若被 WPF 重置会回到 0x8 系)" -f $ex3)
if ($td.Id -eq $p.Id) { Stop-Process -Id $p.Id -Force -Confirm:$false }
Write-Host done
