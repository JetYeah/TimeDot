using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using TimeDot.Core;

namespace TimeDot;

public partial class App : Application
{
    private static Mutex? _mutex;
    private RecordStore? _store;
    private System.Windows.Forms.NotifyIcon? _tray;

    internal System.Windows.Forms.NotifyIcon? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "TimeDot.Singleton", out var isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (s, ev) =>
        {
            // 任何未捕获 UI 异常都记录且不崩溃 —— 常驻工具优先保活
            try { File.AppendAllText(Path.Combine(RecordStore.DefaultDataDir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ev.Exception}\n"); } catch { }
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            try { File.AppendAllText(Path.Combine(RecordStore.DefaultDataDir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ev.ExceptionObject}\n"); } catch { }
        };

        _store = new RecordStore();
        _store.LoadConfig();

        // 关机/注销/进程退出时同步落盘,兜住 400ms 防抖窗口内的最后一批变更
        SessionEnding += (s, ev) => { try { _store?.SaveNow(); _store?.SaveConfig(); } catch { } };
        AppDomain.CurrentDomain.ProcessExit += (s, ev) => { try { _store?.SaveNow(); } catch { } };
        Exit += (s, ev) => { try { _store?.SaveNow(); } catch { } };

        var win = new MainWindow(_store);
        if (e.Args.Contains("--expanded", StringComparer.OrdinalIgnoreCase))
            win.DebugShowExpanded();
        win.Show();

        InitTray(win);
    }

    private void InitTray(MainWindow win)
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "TimeDot 计时悬浮球",
            Icon = BuildIcon(),
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var mShow = new System.Windows.Forms.ToolStripMenuItem("显示悬浮球");
        mShow.Click += (s, e) => win.ShowBall();
        var mAutoDock = new System.Windows.Forms.ToolStripMenuItem("空闲自动收边")
        {
            CheckOnClick = true,
            Checked = _store!.Config.AutoDockSeconds > 0,
        };
        mAutoDock.Click += (s, e) =>
        {
            _store.Config.AutoDockSeconds = mAutoDock.Checked ? 60 : 0;
            _store.SaveConfig();
        };
        var mAutoStart = new System.Windows.Forms.ToolStripMenuItem("开机自启动")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled(),
        };
        mAutoStart.Click += (s, e) => SetAutoStart(mAutoStart.Checked);
        var mExit = new System.Windows.Forms.ToolStripMenuItem("退出");
        mExit.Click += (s, e) => Quit();
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { mShow, mAutoDock, mAutoStart,
            new System.Windows.Forms.ToolStripSeparator(), mExit });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => win.ShowBall();
    }

    internal void Quit()
    {
        try
        {
            _store?.SaveNow();
            _store?.SaveConfig();
        }
        catch { }
        _tray?.Dispose();
        Shutdown();
    }

    // ---------- 开机自启(HKCU Run) ----------

    private static Microsoft.Win32.RegistryKey RunKey(bool writable) =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable)!;

    internal static bool IsAutoStartEnabled() =>
        RunKey(false).GetValue("TimeDot") is string v && v.Contains("TimeDot", StringComparison.OrdinalIgnoreCase);

    internal static void SetAutoStart(bool on)
    {
        using var k = RunKey(true);
        if (on)
        {
            var exe = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            k.SetValue("TimeDot", $"\"{exe}\"");
        }
        else
            k.DeleteValue("TimeDot", false);
    }

    // ---------- 托盘图标(运行时绘制,免 .ico 资源;与 app.ico 同款) ----------

    /* 笑眼多边形:收自表情引擎 rings.js 的 EXPR 11(开心笑眼),
     * 归一化到球心坐标系(单位=球直径),绘制时 px = cx + nx*d */
    private static readonly float[] EyeLeft =
    {
        -0.163f, -0.197f, -0.113f, -0.148f, -0.095f, -0.057f, -0.074f, 0.015f, -0.045f, 0.084f, -0.051f, 0.154f,
        -0.136f, 0.151f, -0.177f, 0.091f, -0.203f, 0.021f, -0.222f, -0.051f, -0.229f, -0.143f, -0.182f, -0.196f
    };
    private static readonly float[] EyeRight =
    {
        0.085f, -0.269f, 0.153f, -0.237f, 0.185f, -0.146f, 0.208f, -0.071f, 0.230f, 0.004f, 0.211f, 0.076f,
        0.121f, 0.094f, 0.076f, 0.034f, 0.054f, -0.041f, 0.032f, -0.116f, 0.014f, -0.210f, 0.066f, -0.265f
    };

    private static void FillEye(System.Drawing.Graphics g, System.Drawing.SolidBrush ink, float[] flat, float cx, float cy, float d)
    {
        var pts = new System.Drawing.PointF[flat.Length / 2];
        for (var i = 0; i < pts.Length; i++)
            pts[i] = new System.Drawing.PointF(cx + flat[i * 2] * d, cy + flat[i * 2 + 1] * d);
        g.FillPolygon(ink, pts);
    }

    private static System.Drawing.Icon BuildIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            // 暖奶油球体 + 细朱砂描边 + EXPR 11 笑眼(无嘴巴),与悬浮球及纸感主题一致
            using var cream = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 246, 239, 228));
            g.FillEllipse(cream, 2f, 2f, 28f, 28f);
            using var rim = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 192, 69, 34), 1.33f);
            g.DrawEllipse(rim, 2.67f, 2.67f, 26.66f, 26.66f);
            using var ink = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 26, 26, 26));
            FillEye(g, ink, EyeLeft, 16f, 16f, 28f);
            FillEye(g, ink, EyeRight, 16f, 16f, 28f);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
