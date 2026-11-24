# Find screen points whose top-level window belongs to mstsc (exposed remote desktop area),
# and report the pixel colour there. Read-only. ASCII only.
# Usage: powershell -File tools\find_mstsc_spot.ps1 [-Step 120]
param([int]$Step = 120, [switch]$All)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class FP {
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  public static string Owner(int x, int y) {
    var h = WindowFromPoint(new PT { X = x, Y = y });
    if (h == IntPtr.Zero) return "none";
    var top = GetAncestor(h, 2);
    uint pid; GetWindowThreadProcessId(top, out pid);
    var sb = new StringBuilder(128); GetClassName(top, sb, 128);
    RECT r; GetWindowRect(top, out r);
    return pid + " " + sb + " (" + r.L + "," + r.T + ")-(" + r.R + "," + r.B + ")";
  }
}
"@
$mstscIds = @(Get-Process mstsc -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0, 0, 0, 0, $b.Size); $g.Dispose()
for ($y = $Step; $y -lt $b.Height - 60; $y += $Step) {
    $row = ""
    for ($x = $Step; $x -lt $b.Width - 60; $x += $Step) {
        $o = [FP]::Owner($x, $y)
        $parts = $o.Split(' ')
        $isM = ($mstscIds.Count -gt 0) -and ($parts[0] -eq [string]$mstscIds[0])
        $c = $bmp.GetPixel($x, $y)
        $tag = if ($isM) { "M" } elseif ($o -eq "none") { "-" } else { "." }
        if ($isM) { $row += ("`n  M ({0},{1}) rgb={2},{3},{4}" -f $x, $y, $c.R, $c.G, $c.B) }
        elseif ($All) { $row += ("`n  . ({0},{1}) {2}" -f $x, $y, $o) }
    }
    if ($row.Length -gt 0) { Write-Output ("y=" + $y + $row) }
}
$bmp.Dispose()
