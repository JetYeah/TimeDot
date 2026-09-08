# pass_probe.ps1 — 诊断恒定窗口版的穿透状态:展开/收起后 WindowFromPoint 命中与主窗 EXSTYLE
param([string]$Exe = 'D:\ClaudeCode\TimeDot-main\dist\TimeDot.exe')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PP {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    public const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
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
    public static IntPtr FindRender(IntPtr parent) {
        IntPtr found = IntPtr.Zero;
        EnumProc cb = delegate(IntPtr h, IntPtr l) {
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            if (sb.ToString() == "Chrome_RenderWidgetHostHWND") { found = h; return false; }
            return true;
        };
        EnumChildWindows(parent, cb, IntPtr.Zero);
        return found;
    }
    public static void Click(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        PostMessage(h, WM_LBUTTONDOWN, (IntPtr)0x0001, lp);
        System.Threading.Thread.Sleep(90);
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, lp);
    }
    public static string Info(IntPtr h) {
        if (h == IntPtr.Zero) return "(none)";
        var sb = new StringBuilder(256); GetClassName(h, sb, 256);
        uint pid; GetWindowThreadProcessId(h, out pid);
        string pn = "?"; try { pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch {}
        return pn + "/" + sb;
    }
}
'@
$td = Get-Process TimeDot -ErrorAction SilentlyContinue
if ($td) { Stop-Process -Id $td.Id -Force -Confirm:$false; Start-Sleep -Milliseconds 600 }
$p = Start-Process $Exe -PassThru
Start-Sleep -Seconds 8
$main = [PP]::FindMain([uint32]$p.Id)
$rwh = [PP]::FindRender($main)
$wr = New-Object PP+RECT
[PP]::GetWindowRect($main, [ref]$wr) | Out-Null
$bx = [int](($wr.Left + $wr.Right) / 2); $by = [int](($wr.Top + $wr.Bottom) / 2)
Write-Host ("主窗 0x{0:X} 矩形 ({1},{2})-({3},{4}) 渲染窗 0x{5:X}" -f $main, $wr.Left, $wr.Top, $wr.Right, $wr.Bottom, $rwh)

function Probe([string]$tag) {
    $ex = [PP]::GetWindowLong($main, -20)
    Write-Host ("[{0}] 主窗 EXSTYLE=0x{1:X} WS_EX_TRANSPARENT={2}" -f $tag, $ex, (($ex -band 0x20) -ne 0))
    foreach ($pt in @(@(400,300), @(960,100), @(100,1000))) {
        $pnt = New-Object PP+POINT; $pnt.X = $pt[0]; $pnt.Y = $pt[1]
        $hit = [PP]::WindowFromPoint($pnt)
        Write-Host ("  WindowFromPoint({0},{1}) = {2}" -f $pt[0], $pt[1], [PP]::Info($hit))
    }
}

Probe '启动Free态'
# 展开环形(点击球心,渲染窗坐标=球心-主窗原点)
$cx = $bx - $wr.Left; $cy = $by - $wr.Top
[PP]::Click($rwh, $cx, $cy)
Start-Sleep -Milliseconds 900
Probe '展开环形'
# 收起
[PP]::Click($rwh, $cx, $cy)
Start-Sleep -Milliseconds 1500
Probe '收起后'
Start-Sleep -Seconds 2
Probe '收起后+2s'
Stop-Process -Id $p.Id -Force -Confirm:$false
Write-Host done
