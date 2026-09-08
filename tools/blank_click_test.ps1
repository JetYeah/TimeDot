# blank_click_test.ps1 — 展开→点空白→验证收起是否触发
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class BC {
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
Get-Process TimeDot -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Milliseconds 600
$p = Start-Process 'D:\ClaudeCode\TimeDot-main\dist\TimeDot.exe' -PassThru
Start-Sleep -Seconds 8
$main = [BC]::FindMain([uint32]$p.Id)
$rwh = [BC]::FindRender($main)
$wait = 0
while ($rwh -eq [IntPtr]::Zero -and $wait -lt 20) { Start-Sleep -Seconds 1; $rwh = [BC]::FindRender($main); $wait++ }
Write-Host ("渲染窗就绪等待 ${wait}s: 0x{0:X}" -f $rwh)
$wr = New-Object BC+RECT
[BC]::GetWindowRect($main, [ref]$wr) | Out-Null
$bx = [int](($wr.Left+$wr.Right)/2); $by = [int](($wr.Top+$wr.Bottom)/2)
# PostMessage 目标是渲染窗,坐标须相对渲染窗原点(恒定窗口方案下渲染窗原点=球位置,非主窗原点)
$rr = New-Object BC+RECT
[BC]::GetWindowRect($rwh, [ref]$rr) | Out-Null
$cx = $bx - $rr.Left; $cy = $by - $rr.Top
Write-Host ("球心=($bx,$by) 主窗=0x{0:X}" -f $main)

function RingWarm() {
    $bmp = New-Object System.Drawing.Bitmap(236, 236)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($bx-118), ($by-118), 0, 0, (New-Object System.Drawing.Size(236,236)))
    $g.Dispose()
    $script:warm = 0
    for ($y = 0; $y -lt 236; $y += 3) {
        for ($x = 0; $x -lt 236; $x += 3) {
            $dx = $x - 118; $dy = $y - 118
            $d = [Math]::Sqrt($dx*$dx + $dy*$dy)
            if ($d -ge 55 -and $d -le 95) {
                $c = $bmp.GetPixel($x, $y)
                if ($c.R -ge 210 -and $c.G -ge 205 -and $c.B -ge 185) { $script:warm++ }
            }
        }
    }
    $bmp.Dispose()
    return $script:warm
}

# 1. 点击球心展开
[BC]::Click($rwh, $cx, $cy)
Start-Sleep -Milliseconds 900
$w1 = RingWarm
Write-Host ("展开后环带暖色: $w1 (应 >1000)")
# 2. 点击空白(主窗客户区坐标,远离球与环形)
[BC]::Click($main, 300, 500)
Start-Sleep -Milliseconds 1300
$w2 = RingWarm
Write-Host ("点空白后环带暖色: $w2 (应 <100 = 已收起)")
# 3. 再探测展开态 NCHITTEST(此时应已 Free)
$lp = [IntPtr](((300 -shl 16) -bor 400))
$r = [BC]::SendMessage($main, 0x0084, [IntPtr]::Zero, $lp)
Write-Host ("Free 态 (400,300) NCHITTEST = {0} (-1=穿透)" -f $r)
Stop-Process -Id $p.Id -Force -Confirm:$false
Write-Host done
