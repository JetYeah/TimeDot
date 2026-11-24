# Experiment: apply a circular window region to the WebView2 Chrome_WidgetWin_0 child hwnd of the
# running TimeDot and capture the result. Reversible: run with -Reset to remove the region.
param([switch]$Reset, [switch]$Toggle, [string]$Out = "probe_region.png")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class RG {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
  public static IntPtr MainH = IntPtr.Zero; public static RECT MainR = new RECT();
  public static IntPtr Web = IntPtr.Zero; public static RECT WebR = new RECT();
  public static void Find(uint pid) {
    int bestArea = 0;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r); int area = (r.R - r.L) * (r.B - r.T);
      if (area > bestArea) { bestArea = area; MainH = h; MainR = r; }
      return true; }, IntPtr.Zero);
    EnumChildWindows(MainH, (h, l) => {
      var sb = new StringBuilder(64); GetClassName(h, sb, 64);
      if (sb.ToString() == "Chrome_WidgetWin_0") { Web = h; GetWindowRect(h, out WebR); return false; }
      return true; }, IntPtr.Zero);
  }
}
"@
$proc = Get-Process TimeDot -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "TimeDot not running"; exit 0 }
[RG]::Find([uint32]$proc.Id)
Write-Output ("main={0} ({1},{2})-({3},{4})  web={5} ({6},{7})-({8},{9})" -f [RG]::MainH, [RG]::MainR.L, [RG]::MainR.T, [RG]::MainR.R, [RG]::MainR.B, [RG]::Web, [RG]::WebR.L, [RG]::WebR.T, [RG]::WebR.R, [RG]::WebR.B)
if ($Reset) {
  [RG]::SetWindowRgn([RG]::Web, [IntPtr]::Zero, $true) | Out-Null
  Write-Output "region reset"
} elseif ($Toggle) {
  [RG]::ShowWindow([RG]::Web, 0) | Out-Null; Start-Sleep -Milliseconds 40; [RG]::ShowWindow([RG]::Web, 5) | Out-Null
  Write-Output "web hwnd hide 40ms -> show"
} else {
  $w = [RG]::WebR.R - [RG]::WebR.L; $hh = [RG]::WebR.B - [RG]::WebR.T
  $rgn = [RG]::CreateEllipticRgn(0, 0, $w, $hh)
  $ok = [RG]::SetWindowRgn([RG]::Web, $rgn, $true)
  Write-Output ("SetWindowRgn ellipse {0}x{1} ok={2}" -f $w, $hh, $ok)
}
Start-Sleep -Milliseconds 400
$r = [RG]::MainR; $mw = $r.R - $r.L; $mh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $mw, $mh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size); $g.Dispose()
$path = Join-Path $env:APPDATA ("TimeDot\" + $Out)
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Output ("saved " + $path)
