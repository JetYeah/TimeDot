# e2e_test.ps1 — 完整链路:点球展开→点空白→验证收起(全部用 config 真实球位)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class E2E {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
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
}
'@
$cfg = Get-Content "$env:APPDATA\TimeDot\config.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$bx = [int]$cfg.BallX; $by = [int]$cfg.BallY
Write-Host ("config 球位: ($bx,$by) DockSide={0}" -f $cfg.DockSide)
Get-Process TimeDot -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Milliseconds 600
$p = Start-Process 'D:\ClaudeCode\TimeDot-main\dist\TimeDot.exe' -PassThru
Start-Sleep -Seconds 10
$main = [E2E]::FindMain([uint32]$p.Id)
$rwh = [E2E]::FindRender($main)
$rr = New-Object E2E+RECT
[E2E]::GetWindowRect($rwh, [ref]$rr) | Out-Null
Write-Host ("渲染窗 ({0},{1})-({2},{3})" -f $rr.Left, $rr.Top, $rr.Right, $rr.Bottom)

function RingWarm() {
    $bmp = New-Object System.Drawing.Bitmap(236, 236)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($bx-118), ($by-118), 0, 0, (New-Object System.Drawing.Size(236,236)))
    $g.Dispose()
    $script:w = 0
    for ($y = 0; $y -lt 236; $y += 3) {
        for ($x = 0; $x -lt 236; $x += 3) {
            $dx = $x - 118; $dy = $y - 118
            $d = [Math]::Sqrt($dx*$dx + $dy*$dy)
            if ($d -ge 55 -and $d -le 95) {
                $c = $bmp.GetPixel($x, $y)
                if ($c.R -ge 210 -and $c.G -ge 205 -and $c.B -ge 185) { $script:w++ }
            }
        }
    }
    $bmp.Dispose()
    return $script:w
}

# 点击球心(相对渲染窗)
$cx = $bx - $rr.Left; $cy = $by - $rr.Top
[E2E]::Click($rwh, $cx, $cy)
Start-Sleep -Milliseconds 900
$w1 = RingWarm
Write-Host "展开后环带暖色: $w1 (期望 >1000)"
# 点击空白(远离球 400px,主窗客户区坐标=屏幕坐标,窗口原点 0,0)
[E2E]::Click($main, ($bx - 400), ($by + 200))
Start-Sleep -Milliseconds 1300
$w2 = RingWarm
Write-Host "点空白后环带暖色: $w2 (期望 <100 = 已收起)"
# 最终穿透
$lp = [IntPtr](((300 -shl 16) -bor 400))
$r = [E2E]::SendMessage($main, 0x0084, [IntPtr]::Zero, $lp)
Write-Host ("Free 态 (400,300) NCHITTEST = {0}" -f $r)
Stop-Process -Id $p.Id -Force -Confirm:$false
Write-Host done
