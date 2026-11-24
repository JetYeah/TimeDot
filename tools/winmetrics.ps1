# Print TimeDot main window metrics: GetWindowRect, GetClientRect, DPI, styles. ASCII only.
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class WM {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  public static void Dump(uint pid) {
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      RECT wr, cr; GetWindowRect(h, out wr); GetClientRect(h, out cr);
      int style = GetWindowLong(h, -16), ex = GetWindowLong(h, -20);
      var tb = new StringBuilder(128); GetClassName(h, tb, 128);
      Console.WriteLine("top hwnd={0} class={1} vis={2} window=({3},{4})-({5},{6}) {7}x{8} client={9}x{10} dpi={11} style=0x{12:X8} ex=0x{13:X8}",
        h, tb, IsWindowVisible(h) ? 1 : 0, wr.L, wr.T, wr.R, wr.B, wr.R - wr.L, wr.B - wr.T, cr.R - cr.L, cr.B - cr.T, GetDpiForWindow(h), style, ex);
      if (!IsWindowVisible(h)) return true;
      EnumChildWindows(h, (c, l2) => {
        RECT cw; GetWindowRect(c, out cw); RECT cc; GetClientRect(c, out cc);
        var sb = new StringBuilder(64); GetClassName(c, sb, 64);
        Console.WriteLine("  child={0} class={1} parent={2} vis={3} window=({4},{5})-({6},{7}) {8}x{9} client={10}x{11} style=0x{12:X8}",
          c, sb, GetParent(c), IsWindowVisible(c) ? 1 : 0, cw.L, cw.T, cw.R, cw.B, cw.R - cw.L, cw.B - cw.T, cc.R - cc.L, cc.B - cc.T, GetWindowLong(c, -16));
        return true; }, IntPtr.Zero);
      return true; }, IntPtr.Zero);
  }
}
"@
$proc = Get-Process TimeDot -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "TimeDot not running"; exit 0 }
[WM]::Dump([uint32]$proc.Id)
