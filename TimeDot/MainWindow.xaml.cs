using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TimeDot.Core;

namespace TimeDot;

public partial class MainWindow : Window
{
    // ---------- 几何常量(DIP) ----------
    private const double BallSz = 84;            // 球可视宿主(含外环)
    private const double PanelW = 402;           // 面板总宽(382 内容 + 2*10 阴影边距)
    private const double StripLen = 72;          // 吸边条长度
    private const double StripThick = 10;        // 吸边条厚度
    private const double DockSnapDist = 44;      // 拖拽松手时距边多近判定吸附
    private const double DragThreshold = 4;      // 位移超过此值(DIP)视为拖拽而非点击

    private enum Mode { Free, Expanded, Docked }
    private enum Side { None, Left, Right, Top, Bottom }

    private readonly RecordStore _store;
    private Mode _mode = Mode.Free;
    private Side _dockSide = Side.None;

    // 球心位置(工作区坐标,DIP)
    private double _cx, _cy;
    // 吸边时沿边位置(strip 中心)
    private double _dockOffset = 400;

    // 拖拽状态
    private bool _dragging, _dragMoved;
    private Point _dragWinOrigin, _dragCursorOriginPhys;
    private double _dpiScale = 1.0;

    // 小条沿边拖拽状态
    private bool _stripDragging, _stripMoved;
    private double _stripCursorOriginPhys; // 沿边轴的物理游标起点

    private DateTime _lastActivity = DateTime.Now;
    private readonly DispatcherTimer _autoDockTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tickCount;

    // WebView2 emotion-ball 状态
    private bool _webReady;
    private string _pendingEmotion = "02";
    private string _lastEmotion = "";

    // ---------- Win32 互操作:获取光标物理屏幕坐标(拖拽用) ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // ---------- 球交互:JS 侧捕获鼠标,通过 postMessage 通知 C# 驱动窗口 ----------

    /// <summary>JS 发 dragstart:记录拖拽起点(窗口位置 + 光标物理坐标)。</summary>
    private void WebDragStart()
    {
        _dragging = true;
        _dragMoved = false;
        _dragWinOrigin = new Point(Left, Top);
        GetCursorPos(out var p);
        _dragCursorOriginPhys = new Point(p.X, p.Y);
        _lastActivity = DateTime.Now;
    }

    /// <summary>JS 发 dragmove:按当前光标绝对物理坐标驱动窗口移动(幂等)。</summary>
    private void WebDragMove()
    {
        if (!_dragging) return;
        _lastActivity = DateTime.Now;
        GetCursorPos(out var p);
        var dx = (p.X - _dragCursorOriginPhys.X) / _dpiScale;
        var dy = (p.Y - _dragCursorOriginPhys.Y) / _dpiScale;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) > DragThreshold) _dragMoved = true;
        if (!_dragMoved) return;

        var wa = WorkArea;
        Left = Clamp(_dragWinOrigin.X + dx, wa.Left - BallSz / 2 + 6, wa.Right - BallSz / 2 - 6);
        Top = Clamp(_dragWinOrigin.Y + dy, wa.Top - BallSz / 2 + 6, wa.Bottom - BallSz / 2 - 6);
        // 窗口矩形可能仍是展开态的尺寸(收起不缩窗),球心要按 Margin 偏移还原
        _cx = Left + BallHost.Margin.Left + BallSz / 2;
        _cy = Top + BallHost.Margin.Top + BallSz / 2;
    }

    /// <summary>JS 发 dragend:未移动视为点击(展开/收起);移动了则判定吸边或保存位置。</summary>
    private void WebDragEnd()
    {
        if (!_dragging) return;
        _dragging = false;
        if (!_dragMoved)
        {
            if (_mode == Mode.Free) ExpandPanel();
            else if (_mode == Mode.Expanded) CollapsePanel(true);
            return;
        }
        if (_mode == Mode.Expanded) { SavePosition(); return; } // 展开态拖动仅移动,不吸附

        var wa = WorkArea;
        double dl = _cx - wa.Left, dr = wa.Right - _cx, dt = _cy - wa.Top, db = wa.Bottom - _cy;
        var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        if (min < DockSnapDist)
        {
            _dockSide = min == dl ? Side.Left : min == dr ? Side.Right : min == dt ? Side.Top : Side.Bottom;
            _dockOffset = _dockSide is Side.Left or Side.Right ? _cy : _cx;
            EnterDocked(true);
        }
        else SavePosition();
    }

    public MainWindow(RecordStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += OnLoaded;

        _autoDockTimer.Tick += (_, _) => AutoDockCheck();
        _tick.Tick += (_, _) => OnTick();
        Deactivated += (_, _) => { if (_mode == Mode.Expanded) CollapsePanel(true); };
        PreviewMouseMove += (_, _) => _lastActivity = DateTime.Now;
        MouseEnter += (_, _) => _lastActivity = DateTime.Now;
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key != Key.Escape || _mode != Mode.Expanded) return;
            // 焦点在搜索框时,Esc 交给搜索框语义(清空搜索),不收起面板
            if (ReferenceEquals(Keyboard.FocusedElement, InSearch)) return;
            CollapsePanel(true);
        };
        StripHost.LostMouseCapture += (s, e) => _stripDragging = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1.0;

        var wa = SystemParameters.WorkArea;
        var cfg = _store.Config;
        _cx = cfg.BallX >= 0 && cfg.BallY >= 0 ? Clamp(cfg.BallX, wa.Left + 46, wa.Right - 46)
                                               : wa.Right - 120;
        _cy = cfg.BallY >= 0 && cfg.BallY >= 0 ? Clamp(cfg.BallY, wa.Top + 46, wa.Bottom - 46)
                                               : wa.Top + 150;
        _dockOffset = cfg.DockOffset;

        if (cfg.DockSide is "left" or "right" or "top" or "bottom")
        {
            _dockSide = Enum.Parse<Side>(cfg.DockSide, true);
            _mode = Mode.Docked;
            StopLoops();
            ApplyDockedLayout();
        }
        else
        {
            LayoutBallOnly();
            StartLoops();
        }

        RefreshList();
        UpdateRunningVisuals();
        BuildNoiseTexture();
        _ = InitBallWeb();
        _autoDockTimer.Start();
        _tick.Start();
    }

    /// <summary>生成 96×96 灰噪点并平铺,给面板"磨砂玻璃"的颗粒感(一次性渲染并冻结)。</summary>
    private void BuildNoiseTexture()
    {
        const int n = 96;
        var rnd = new Random(20260820);
        var rtb = new RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            for (var i = 0; i < 2600; i++)
            {
                var v = (byte)rnd.Next(110, 245);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(v, v, v)), null,
                    new Rect(rnd.Next(n), rnd.Next(n), 1.2, 1.2));
            }
        }
        rtb.Render(dv);
        rtb.Freeze();
        var brush = new ImageBrush(rtb)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, n, n),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        NoiseLayer.Background = brush;
    }

    internal void DebugShowExpanded()
    {
        Loaded += async (_, _) =>
        {
            await Task.Delay(150);
            if (_mode == Mode.Free) ExpandPanel(focusInput: false);
        };
    }

    // ---------- 布局 ----------

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    private Rect WorkArea => SystemParameters.WorkArea;

    /// <summary>自由态:窗口恰好包住球。</summary>
    private void LayoutBallOnly()
    {
        Width = BallSz; Height = BallSz;
        Left = _cx - BallSz / 2; Top = _cy - BallSz / 2;
        BallHost.Margin = new Thickness(0);
        PanelHost.Visibility = Visibility.Collapsed;
        StripHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>展开态:窗口 = 球 ∪ 面板。优先下展,空间不足翻上;水平夹紧。球保持视觉不动(按 margin 重新对齐)。</summary>
    private void LayoutExpanded()
    {
        PanelHost.Visibility = Visibility.Visible;
        PanelHost.Measure(new Size(PanelW, double.PositiveInfinity));
        var panelH = Math.Min(PanelHost.DesiredSize.Height, WorkArea.Height - 20);

        var ballR = new Rect(_cx - BallSz / 2, _cy - BallSz / 2, BallSz, BallSz);
        double pTop = ballR.Bottom + 4;
        if (pTop + panelH > WorkArea.Bottom) pTop = Math.Max(WorkArea.Top + 2, ballR.Top - 4 - panelH);
        double pLeft = Clamp(_cx - PanelW / 2, WorkArea.Left + 2, WorkArea.Right - PanelW - 2);

        var win = Rect.Union(ballR, new Rect(pLeft, pTop, PanelW, panelH));
        Width = win.Width; Height = win.Height;
        Left = win.Left; Top = win.Top;
        BallHost.Margin = new Thickness(ballR.Left - win.Left, ballR.Top - win.Top, 0, 0);
        PanelHost.Margin = new Thickness(pLeft - win.Left, pTop - win.Top, 0, 0);
        StripHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>吸边条目标矩形(纯计算,不应用)。</summary>
    private Rect DockedTarget()
    {
        var wa = WorkArea;
        return _dockSide switch
        {
            Side.Left => new Rect(wa.Left, Clamp(_dockOffset, wa.Top + StripLen / 2 + 6, wa.Bottom - StripLen / 2 - 6) - StripLen / 2, StripThick, StripLen),
            Side.Right => new Rect(wa.Right - StripThick, Clamp(_dockOffset, wa.Top + StripLen / 2 + 6, wa.Bottom - StripLen / 2 - 6) - StripLen / 2, StripThick, StripLen),
            Side.Top => new Rect(Clamp(_dockOffset, wa.Left + StripLen / 2 + 6, wa.Right - StripLen / 2 - 6) - StripLen / 2, wa.Top, StripLen, StripThick),
            _ => new Rect(Clamp(_dockOffset, wa.Left + StripLen / 2 + 6, wa.Right - StripLen / 2 - 6) - StripLen / 2, wa.Bottom - StripThick, StripLen, StripThick),
        };
    }

    private void ApplyDockedLayout()
    {
        var r = DockedTarget();
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
        BallHost.Visibility = Visibility.Collapsed;
        PanelHost.Visibility = Visibility.Collapsed;
        StripHost.Visibility = Visibility.Visible;
        StripHost.Margin = new Thickness(0);
        var vertical = _dockSide is Side.Left or Side.Right;
        StripHost.Width = vertical ? StripThick : StripLen;
        StripHost.Height = vertical ? StripLen : StripThick;
        StripCore.Width = vertical ? 3 : StripLen - 16;
        StripCore.Height = vertical ? StripLen - 16 : 3;
        StripCore.Opacity = _store.RunningRecord != null ? 0.8 : 0.35;
    }

    // ---------- 展开与收起 ----------
    // 注:收起时不缩窗,只藏面板 —— 窗口矩形保持,球原地不动,透明区域天然点击穿透。

    private void ExpandPanel(bool focusInput = true)
    {
        if (_mode != Mode.Free) return; // 吸边态由小条交互负责唤出,展开态不重复展开
        BallHost.Visibility = Visibility.Visible;
        _mode = Mode.Expanded;
        LayoutExpanded();
        // emotion-ball 自行处理动画,无需 WPF Storyboard
        PanelCard.BeginAnimation(OpacityProperty, null); // 清掉收起时的本地动画,交给展开 storyboard
        ((Storyboard)FindResource("PanelIn")).Begin(this, true);

        RefreshList(); // 收起期间跳过了每秒刷新,重展开时先补一次,避免陈旧时长/排序
        if (focusInput)
        {
            Activate();
            Keyboard.Focus(InProject);
            InProject.SelectAll();
        }
        _lastActivity = DateTime.Now;
    }

    private void CollapsePanel(bool animate)
    {
        if (_mode != Mode.Expanded) return;
        _mode = Mode.Free;
        StartLoops(); // 恢复呼吸动画(小球窗口面,开销可忽略)
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        if (animate)
        {
            fade.Completed += (_, _) => PanelHost.Visibility = Visibility.Collapsed;
            PanelCard.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            PanelCard.BeginAnimation(OpacityProperty, null);
            PanelHost.Visibility = Visibility.Collapsed;
        }
        SavePosition();
    }

    // ---------- 吸边 ----------

    private void EnterDocked(bool animate)
    {
        if (_mode == Mode.Expanded) CollapsePanel(false);
        _mode = Mode.Docked;
        StopLoops();
        BallHost.Visibility = Visibility.Collapsed;
        StripHost.Visibility = Visibility.Visible;
        if (animate)
        {
            var target = DockedTarget();
            PrepareStripVisuals();
            AnimateWindowTo(target, 190);
        }
        else ApplyDockedLayout();
        SavePosition();
        _lastActivity = DateTime.Now;
    }

    private void PrepareStripVisuals()
    {
        var vertical = _dockSide is Side.Left or Side.Right;
        StripHost.Margin = new Thickness(0);
        StripHost.Width = vertical ? StripThick : StripLen;
        StripHost.Height = vertical ? StripLen : StripThick;
        StripCore.Width = vertical ? 3 : StripLen - 16;
        StripCore.Height = vertical ? StripLen - 16 : 3;
    }

    private void LeaveDock(bool animate = true)
    {
        if (_mode != Mode.Docked) return;
        _mode = Mode.Free;
        _cx = Clamp(UndockBallCx(), WorkArea.Left + BallSz / 2, WorkArea.Right - BallSz / 2);
        _cy = Clamp(UndockBallCy(), WorkArea.Top + BallSz / 2, WorkArea.Bottom - BallSz / 2);
        StripHost.Visibility = Visibility.Collapsed;
        BallHost.Visibility = Visibility.Visible;
        var target = new Rect(_cx - BallSz / 2, _cy - BallSz / 2, BallSz, BallSz);
        if (animate) { BallHost.Margin = new Thickness(0); AnimateWindowTo(target, 190); }
        else LayoutBallOnly();
        StartLoops();
        SavePosition();
    }

    /// <summary>托盘/小条唤出:吸边则弹出球;自由态则只确认球可见。</summary>
    internal void ShowBall()
    {
        if (_mode == Mode.Docked) LeaveDock();
        _lastActivity = DateTime.Now;
    }

    private double UndockBallCx() => _dockSide switch
    {
        Side.Left => WorkArea.Left + BallSz / 2 + 10,
        Side.Right => WorkArea.Right - BallSz / 2 - 10,
        _ => _dockOffset,
    };

    private double UndockBallCy() => _dockSide switch
    {
        Side.Top => WorkArea.Top + BallSz / 2 + 10,
        Side.Bottom => WorkArea.Bottom - BallSz / 2 - 10,
        _ => _dockOffset,
    };

    private Side NearestSide()
    {
        var wa = WorkArea;
        double dl = _cx - wa.Left, dr = wa.Right - _cx, dt = _cy - wa.Top, db = wa.Bottom - _cy;
        var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        return min == dl ? Side.Left : min == dr ? Side.Right : min == dt ? Side.Top : Side.Bottom;
    }

    private void AutoDockCheck()
    {
        var sec = _store.Config.AutoDockSeconds;
        if (sec <= 0 || _mode != Mode.Free) return;
        if (IsMouseOver) { _lastActivity = DateTime.Now; return; }
        if ((DateTime.Now - _lastActivity).TotalSeconds < sec) return;
        _dockSide = NearestSide();
        _dockOffset = _dockSide is Side.Left or Side.Right ? _cy : _cx;
        EnterDocked(true);
    }

    // ---------- 球动画循环 ----------

    private void StartLoops()
    {
        // emotion-ball 动画由 WebView2 内部驱动,无需 WPF 动画循环
    }

    private void StopLoops()
    {
        // emotion-ball 动画由 WebView2 内部驱动,无需 WPF 动画循环
    }

    // ---------- 窗口位置补间(代际令牌:新动画可随时取消旧动画) ----------

    private int _animGen;
    private bool _animBusy;

    private void AnimateWindowTo(Rect to, int ms) => _ = AnimateWindowAsync(to, ms, ++_animGen);

    private async Task AnimateWindowAsync(Rect to, int ms, int gen)
    {
        if (_animBusy && gen != _animGen) return; // 已被更新的一代取代
        _animBusy = true;
        try
        {
            var from = new Rect(Left, Top, Width, Height);
            var t0 = Environment.TickCount64;
            var dur = (double)ms;
            while (true)
            {
                if (gen != _animGen) return; // 被新动画抢占,立即让位
                var p = Math.Min(1, (Environment.TickCount64 - t0) / dur);
                var e = 1 - Math.Pow(1 - p, 3); // easeOutCubic
                Left = from.Left + (to.Left - from.Left) * e;
                Top = from.Top + (to.Top - from.Top) * e;
                Width = from.Width + (to.Width - from.Width) * e;
                Height = from.Height + (to.Height - from.Height) * e;
                if (p >= 1) break;
                await Task.Delay(12);
            }
        }
        finally { if (gen == _animGen) _animBusy = false; }
    }

    // ---------- 球拖拽(逻辑已移至 WebDragStart/Move/End,由 JS 消息驱动) ----------

    private void HoverScale(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ---------- 小条交互:点击唤出,拖动沿边换位 ----------

    private bool StripVertical => _dockSide is Side.Left or Side.Right;

    private void StripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _mode != Mode.Docked) return;
        _stripDragging = true;
        _stripMoved = false;
        var p = StripHost.PointToScreen(e.GetPosition(StripHost));
        _stripCursorOriginPhys = StripVertical ? p.Y : p.X;
        StripHost.CaptureMouse();
        _lastActivity = DateTime.Now;
        e.Handled = true;
    }

    private void StripMove(object sender, MouseEventArgs e)
    {
        if (!_stripDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = StripHost.PointToScreen(e.GetPosition(StripHost));
        var alongPhys = StripVertical ? p.Y : p.X;
        var delta = (alongPhys - _stripCursorOriginPhys) / _dpiScale;
        if (!_stripMoved && Math.Abs(delta) > 4) _stripMoved = true;
        if (!_stripMoved) return;
        _dockOffset += delta;
        _stripCursorOriginPhys = alongPhys;
        // 只动窗口的沿边坐标,厚度方向贴边不动
        var r = DockedTarget();
        if (StripVertical) Top = r.Top; else Left = r.Left;
    }

    private void StripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stripDragging) return;
        _stripDragging = false;
        var moved = _stripMoved;
        StripHost.ReleaseMouseCapture();
        if (!moved) ShowBall();
        else SavePosition();
    }

    private void StripEnter(object sender, MouseEventArgs e)
    {
        // 悬停:纸感象牙提亮为完全不透明(与 XAML 常态 #E8F4F0E6 同色系)
        StripHost.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF7, 0xF5, 0xEF));
        AnimateStripCore(0.9);
    }

    private void StripLeave(object sender, MouseEventArgs e)
    {
        // 恢复 XAML 常态:象牙纸色(与初始定义一致)
        StripHost.Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xF4, 0xF0, 0xE6));
        AnimateStripCore(_store.RunningRecord != null ? 0.8 : 0.35);
    }

    /// <summary>动画结束后清除时钟并落本地值,否则 HoldEnd 会永久遮蔽后续本地赋值。</summary>
    private void AnimateStripCore(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140));
        a.Completed += (_, _) =>
        {
            StripCore.BeginAnimation(OpacityProperty, null);
            StripCore.Opacity = to;
        };
        StripCore.BeginAnimation(OpacityProperty, a);
    }

    // ---------- 每秒刷新 ----------

    private void OnTick()
    {
        var now = DateTime.Now;
        if (_mode == Mode.Expanded)
        {
            // 仅展开时才全量刷新列表与统计:每秒 LINQ + 日期解析随记录数线性增长,常驻态不该付这笔钱
            foreach (var vm in _vms) vm.RefreshStats(now);
            FooterText.Text = $"共 {_store.Records.Count} 条 · 今日 {FmtShort(_store.TodaySeconds())}";
        }
        UpdateRunningVisuals();
        if (++_tickCount % 5 == 0) UpdateTrayText();
        // 空闲时周期性裁剪工作集,控制常驻内存(展开交互时不做,避免页面抖动)
        if (_tickCount % 300 == 0 && _mode != Mode.Expanded && !IsMouseOver) TrimWorkingSet();
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static void TrimWorkingSet()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
        }
        catch { /* 权限或句柄异常时静默跳过 */ }
    }

    private void UpdateRunningVisuals()
    {
        var r = _store.RunningRecord;
        var running = r != null;
        // 通过 emotion-ball 表情反映状态
        SendEmotion(running ? "22" : "02");
        if (_mode == Mode.Docked && !StripHost.IsMouseOver) StripCore.Opacity = running ? 0.8 : 0.35;
    }

    internal void UpdateTrayText()
    {
        if (App.Current is not App app || app.Tray == null) return;
        var r = _store.RunningRecord;
        var txt = r == null ? "TimeDot · 空闲" : $"TimeDot · {r.Project} {FmtHms(r.TotalSeconds(DateTime.Now))}";
        if (app.Tray.Text != txt) app.Tray.Text = txt.Length > 63 ? txt[..63] : txt;
    }

    private void SavePosition()
    {
        var cfg = _store.Config;
        cfg.BallX = _cx; cfg.BallY = _cy;
        cfg.DockSide = _mode == Mode.Docked ? _dockSide.ToString().ToLowerInvariant() : "none";
        cfg.DockOffset = _dockOffset;
        _store.SaveConfig();
    }

    // ---------- WebView2 emotion-ball 通信 ----------

    /// <summary>初始化 WebView2 并加载 emotion-ball 页面。</summary>
    private async Task InitBallWeb()
    {
        try
        {
            // 透明背景需在初始化前设置才生效
            BallWeb.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            await BallWeb.EnsureCoreWebView2Async();

            var webDir = Path.Combine(AppContext.BaseDirectory, "Web");
            var cwv = BallWeb.CoreWebView2;

            // 通过 WebResourceRequested 拦截,将 https://app.local/* 映射到本地 Web 目录
            cwv.AddWebResourceRequestedFilter("https://app.local/*", CoreWebView2WebResourceContext.All);
            cwv.WebResourceRequested += (_, e) =>
            {
                try
                {
                    var uri = new Uri(e.Request.Uri);
                    var localPath = uri.AbsolutePath.TrimStart('/');
                    var filePath = Path.Combine(webDir, localPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(filePath))
                    {
                        var stream = File.OpenRead(filePath);
                        var mime = GetMimeType(filePath);
                        e.Response = cwv.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}");
                    }
                }
                catch { }
            };

            // 诊断日志:导航状态(写到 exe 旁边,避免 AppData 权限问题)
            cwv.NavigationCompleted += (_, e) =>
            {
                try { File.AppendAllText(WebLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} [nav] ok={e.IsSuccess} err={e.WebErrorStatus}\n"); } catch { }
                // 页面加载完成后才标记就绪并补发初始表情:
                // 过早 PostWebMessageAsJson 会因页面尚未注册监听而丢失
                _webReady = true;
                SendEmotion(_pendingEmotion);
            };

            // 监听 JS 回传的消息(交互事件由 JS 侧捕获后转发)
            cwv.WebMessageReceived += (_, e) =>
            {
                try
                {
                    try { File.AppendAllText(WebLogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} [msg] {e.WebMessageAsJson}\n"); } catch { }
                    var msg = JsonDocument.Parse(e.WebMessageAsJson);
                    if (!msg.RootElement.TryGetProperty("type", out var t)) return;
                    switch (t.GetString())
                    {
                        case "dragstart":
                            Dispatcher.BeginInvoke(WebDragStart); break;
                        case "dragmove":
                            Dispatcher.BeginInvoke(WebDragMove); break;
                        case "dragend":
                            Dispatcher.BeginInvoke(WebDragEnd); break;
                        case "enter": // 悬停放大
                            Dispatcher.BeginInvoke(() => { _lastActivity = DateTime.Now; HoverScale(1.06); }); break;
                        case "leave": // 离开恢复
                            Dispatcher.BeginInvoke(() => HoverScale(1.0)); break;
                        case "move": // 球上移动,续防自动吸边
                            Dispatcher.BeginInvoke(() => _lastActivity = DateTime.Now); break;
                    }
                }
                catch { /* 解析失败静默忽略 */ }
            };

            if (Directory.Exists(webDir))
                cwv.Navigate("https://app.local/ball.html");
        }
        catch (Exception ex)
        {
            // WebView2 初始化失败时记录日志(写到 exe 旁边),不影响主流程
            try { File.AppendAllText(WebLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} WebView2 init: {ex}\n"); } catch { }
        }
    }

    /// <summary>WebView2 诊断日志路径(exe 同目录)。</summary>
    private static string WebLogPath => Path.Combine(AppContext.BaseDirectory, "web.log");

    /// <summary>根据文件扩展名返回 MIME 类型。</summary>
    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js"   => "application/javascript; charset=utf-8",
        ".css"  => "text/css; charset=utf-8",
        ".svg"  => "image/svg+xml",
        ".png"  => "image/png",
        ".ico"  => "image/x-icon",
        _       => "application/octet-stream"
    };

    /// <summary>向 emotion-ball 推送表情状态。</summary>
    internal void SendEmotion(string emotionId)
    {
        if (emotionId == _lastEmotion) return; // 避免重复发送
        _lastEmotion = emotionId;
        if (!_webReady) { _pendingEmotion = emotionId; return; }
        try
        {
            var msg = JsonSerializer.Serialize(new { emotionId });
            BallWeb.CoreWebView2.PostWebMessageAsJson(msg);
        }
        catch { /* WebView2 未就绪或通信异常时静默忽略 */ }
    }

    /// <summary>向 emotion-ball 推送特效动作。</summary>
    internal void SendAction(string action, int param = 0)
    {
        if (!_webReady) return;
        try
        {
            var msg = param > 0
                ? JsonSerializer.Serialize(new { action, count = param })
                : JsonSerializer.Serialize(new { action });
            BallWeb.CoreWebView2.PostWebMessageAsJson(msg);
        }
        catch { }
    }

    // ---------- 时间格式 ----------

    internal static string FmtHms(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }

    internal static string FmtShort(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m" : $"{t.Seconds}s";
    }
}
