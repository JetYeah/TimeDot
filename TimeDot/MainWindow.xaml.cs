using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfPath = System.Windows.Shapes.Path;
using Microsoft.Web.WebView2.Core;
using TimeDot.Core;

namespace TimeDot;

public partial class MainWindow : Window
{
    // ---------- 几何常量(DIP) ----------
    private const double BallSz = 84;            // 球可视宿主(含外环)
    private const double PanelW = 402;           // 面板总宽(382 内容 + 2*10 阴影边距)
    private const double QuickW = 342;          // 快捷回复面板总宽(同上口径)
    private const double TodoW = 342;           // 待办面板总宽(同上口径)
    private const double RadialOut = 60;        // 环形菜单外半径(球边缘到扇区外缘)
    private const double RadialPad = 16;         // 扇区外缘到窗口边的余量(容纳投影,避免被窗口硬切)
    private const double StripLen = 72;          // 吸边条长度
    private const double StripThick = 10;        // 吸边条厚度
    private const double DockSnapDist = 44;      // 拖拽松手时距边多近判定吸附
    private const double DragThreshold = 4;      // 位移超过此值(DIP)视为拖拽而非点击

    private enum Mode { Free, Expanded, Docked, Quick, Todo, Radial }
    private enum Side { None, Left, Right, Top, Bottom }

    private readonly RecordStore _store;
    private readonly QuickReplyStore _quick;
    private readonly TodoStore _todo;
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

    // 球外空气墙治理:光标在 WebView2 方形内、圆形外时让其点击穿透到下方应用
    private readonly DispatcherTimer _passTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _webPass;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    // ---------- 残影清理(强制 DWM 重合成指定屏幕区域) ----------
    // 覆盖窗必须有真实像素内容(用 UpdateLayeredWindow 显式供给),否则 DWM 不会合成它——
    // 此前用 STATIC + SetLayeredWindowAttributes(alpha=1) 的覆盖窗从生到死无内容(无 WM_PAINT),
    // 销毁也不产生合成层变化,清理等同空操作(残影始终清不掉的硬伤)。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, ref RECT rect, bool erase);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, ref RECT rect, IntPtr hrgn, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint ULW_ALPHA = 2;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public uint biCompression; public int biSizeImage, biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

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
            if (_mode == Mode.Free) ExpandRadial();
            else if (_mode == Mode.Radial) CollapseRadial(true);
            else if (_mode == Mode.Expanded && !_panelPinned) CollapsePanel(true);
            else if (_mode == Mode.Quick && !_quickPinned) CollapseQuick(true);
            else if (_mode == Mode.Todo && !_todoPinned) CollapseTodo(true);
            return;
        }
        if (_mode is Mode.Expanded or Mode.Quick or Mode.Todo or Mode.Radial) { SavePosition(); return; } // 展开态拖动仅移动,不吸附

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

    public MainWindow(RecordStore store, QuickReplyStore quick, TodoStore todo)
    {
        _store = store;
        _quick = quick;
        _todo = todo;
        InitializeComponent();
        Loaded += OnLoaded;

        _autoDockTimer.Tick += (_, _) => AutoDockCheck();
        _tick.Tick += (_, _) => OnTick();
        _passTimer.Tick += (_, _) => UpdateWebPass();
        // 环形菜单期间替身球顶替真球(WebView2),点替身 = 再点球收起菜单
        BallStandin.MouseLeftButtonUp += (_, _) =>
        {
            if (_mode == Mode.Radial) CollapseRadial(true);
        };
        Deactivated += (_, _) =>
        {
            if (_mode == Mode.Expanded && !_panelPinned) CollapsePanel(true);
            if (_mode == Mode.Quick && !_quickPinned) CollapseQuick(true);
            if (_mode == Mode.Todo && !_todoPinned) CollapseTodo(true);
            if (_mode == Mode.Radial) CollapseRadial(true); // 失焦也淡出后再缩窗,防 DWM 残影
        };
        PreviewMouseMove += (_, _) => _lastActivity = DateTime.Now;
        MouseEnter += (_, _) => _lastActivity = DateTime.Now;
        // 失焦收起一律走淡出:扇区像素直接消失+同帧缩窗是 DWM 残影(图形残留)的主要来源
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key != Key.Escape) return;
            // 焦点在搜索框时,Esc 交给搜索框语义(清空搜索),不收起面板
            if (_mode == Mode.Expanded)
            {
                if (ReferenceEquals(Keyboard.FocusedElement, InSearch)) return;
                if (_panelPinned) return; // 固定中,Esc 不收起
                CollapsePanel(true);
            }
            else if (_mode == Mode.Quick)
            {
                if (ReferenceEquals(Keyboard.FocusedElement, QuickSearch))
                {
                    if (QuickSearch.Text.Length > 0) QuickSearch.Clear();
                    return;
                }
                if (_quickPinned) return; // 固定中,Esc 不收起
                CollapseQuick(true);
            }
            else if (_mode == Mode.Todo)
            {
                if (_todoPinned) return;
                CollapseTodo(true);
            }
            else if (_mode == Mode.Radial) CollapseRadial(true); // 淡出后再缩窗:同帧清像素+缩窗会留 DWM 残影
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
            LayoutBallOnly(shrink: true); // 启动首布局:窗口缩为球大小
            StartLoops();
        }

        RefreshList();
        RefreshQuickList();
        RefreshTodoList();
        UpdateRunningVisuals();
        BuildNoiseTexture();
        _ = InitBallWeb();
        _autoDockTimer.Start();
        _tick.Start();
        _passTimer.Start();
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

    /// <summary>主窗整窗点击穿透开关:收起态窗口保持展开矩形(不缩窗)时,大矩形的透明
    /// 区域必须穿透才不挡下方应用。WS_EX_TRANSPARENT 只让"主窗自身"对鼠标透明——
    /// 它的子窗口(WebView 球)仍正常参与命中,球照常可点。展开任何界面前必须先关穿透。</summary>
    private bool _selfPass;
    private void SetSelfPass(bool on)
    {
        if (_selfPass == on) return;
        try
        {
            var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (self == IntPtr.Zero) return;
            int ex = GetWindowLong(self, GWL_EXSTYLE);
            SetWindowLong(self, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
            _selfPass = on;
        }
        catch { /* 窗口句柄未就绪等异常时静默跳过,下次再试 */ }
    }

    /// <summary>收起态布局:窗口缩回恰好包住球(原版行为)。
    /// 收起保持大矩形的方案已放弃:.NET 8 WPF 非 layered 透明窗的"透明区域"合成不
    /// 纯净——绿色下层上稳定显现 +3~+13 的提亮方块(取证实证),洗刷(近透明背景强制
    /// 重绘)只能改变残留色调无法归零;整窗穿透在该 WPF 版本也不可用(layered 被系统
    /// 拒绝、子类化冻结桌面)。缩窗后透明区域只剩 84 窗口的微小四角,视觉可忽略;
    /// 下层显露的残影由 DispelDwmGhost 的多时点强制重绘善后。</summary>
    private void LayoutBallOnly(bool shrink = false)
    {
        var oldRect = new Rect(Left, Top, ActualWidth, ActualHeight);
        var wasBig = oldRect.Width > BallSz + 8 || oldRect.Height > BallSz + 8;
        Width = BallSz; Height = BallSz;
        Left = _cx - BallSz / 2; Top = _cy - BallSz / 2;
        BallHost.Margin = new Thickness(0);
        BallHost.Visibility = Visibility.Visible;
        BallStandin.Visibility = Visibility.Collapsed; // 替身只在环形菜单期间存在
        PanelHost.Visibility = Visibility.Collapsed;
        QuickHost.Visibility = Visibility.Collapsed;
        TodoHost.Visibility = Visibility.Collapsed;
        RadialHost.Visibility = Visibility.Collapsed;
        StripHost.Visibility = Visibility.Collapsed;
        SetSelfPass(false); // 缩窗后 84 恰是球,无需整窗穿透(球外空气墙由 WebView 圆外穿透负责)
        if (wasBig) { DispelDwmGhost(oldRect); PostCollapseGuard(oldRect); }
    }

    /// <summary>窗口缩小/移走后,被遗弃的屏幕区域可能滞留 DWM 旧帧像素(残影)。
    /// 唯一验证有效的手段:在旧矩形上盖一个**有真实像素内容**的覆盖窗——用
    /// UpdateLayeredWindow 显式供给 1×1 近透明 DIB,保证 DWM 必然合成过它;
    /// 存活约 120ms 后销毁,销毁触发该区域确定性的重合成,残影随之消失。
    /// (旧实现的 STATIC 空窗从未有内容,清理等同空操作。)
    /// 收尾补 InvalidateRect(IntPtr.Zero) 全屏失效——现代 Windows 壁纸由 DWM 直绘,
    /// RedrawWindow(GetDesktopWindow) 对桌面无效。覆盖窗不收输入、不夺焦点、不进任务栏。</summary>
    private async void DispelDwmGhost(Rect oldRectDip)
    {
        IntPtr hwnd = IntPtr.Zero, dib = IntPtr.Zero, memDc = IntPtr.Zero;
        try
        {
            var r = new RECT
            {
                Left = (int)Math.Floor(oldRectDip.Left * _dpiScale),
                Top = (int)Math.Floor(oldRectDip.Top * _dpiScale),
                Right = (int)Math.Ceiling(oldRectDip.Right * _dpiScale),
                Bottom = (int)Math.Ceiling(oldRectDip.Bottom * _dpiScale),
            };
            var w = r.Right - r.Left; var h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return;

            await Task.Delay(30); // 稍等缩窗呈现,残影恰在呈现后形成并被覆盖清除

            hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "STATIC", "", WS_POPUP, r.Left, r.Top, w, h,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;

            // 供给真实像素:1×1 近透明 PBGRA DIB,经 ULW_ALPHA 拉伸成整个矩形——
            // 有内容的分层窗口才会被 DWM 真正合成(这是旧实现缺失的关键一步)
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = 1; bmi.bmiHeader.biHeight = -1; // top-down
            bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;
            dib = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib != IntPtr.Zero && bits != IntPtr.Zero)
            {
                // PBGRA 是预乘 alpha 格式:RGB 分量必须 ≤ Alpha,否则 DWM 对其合成行为未定义——
                // 非法预乘像素正是覆盖清理时灵时不灵的隐患;取合法的近全透明值
                System.Runtime.InteropServices.Marshal.WriteInt32(bits, 0x01010101); // alpha=1 近透明,肉眼无感
                memDc = CreateCompatibleDC(IntPtr.Zero);
                var oldObj = SelectObject(memDc, dib);
                var dstPt = new POINT { X = r.Left, Y = r.Top };
                var sz = new SIZE { cx = w, cy = h };
                var srcPt = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER + 每像素 alpha
                UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dstPt, ref sz, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
                SelectObject(memDc, oldObj);
            }
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            await Task.Delay(120); // 存活足量合成帧,确保 DWM 合成过它
            DestroyWindow(hwnd); hwnd = IntPtr.Zero; // 销毁触发该区域重合成 → 残影清除

            // 区域级强制重绘(多时点重试 + 定向窗口):残影的实际形成时刻不稳定(Chromium
            // 最后一帧提交/fade 终帧/D3D 表面收缩各有延迟),单时点清理会扑空(用户实测
            // "前几次好了,点几次又出现"的间歇性);且对 NULL 全区域的 RedrawWindow 部分
            // 应用不完整响应。这里在 ~150/450/900ms 三个时点各清一次,并定向对区域
            // 中心的实际窗口(WindowFromPoint)及其顶层窗口强制重绘——实证 mstsc 远程
            // 桌面在远程画面静止时永不重绘被显露区域,球的旧像素无限期滞留。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(300);
                var rc = r;
                RedrawWindow(IntPtr.Zero, ref rc, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                var mid = new POINT { X = (rc.Left + rc.Right) / 2, Y = (rc.Top + rc.Bottom) / 2 };
                var hit = WindowFromPoint(mid);
                if (hit != IntPtr.Zero)
                {
                    RedrawWindow(hit, ref rc, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    var root = GetAncestor(hit, 2 /* GA_ROOT */);
                    if (root != IntPtr.Zero && root != hit)
                        RedrawWindow(root, ref rc, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                }
            }
        }
        catch { /* 清残影尽力而为,失败不影响主流程 */ }
        finally
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (dib != IntPtr.Zero) DeleteObject(dib);
        }
    }

    // ---------- 收起后残影自检:被动验证清理效果(不位移球窗,不打扰使用) ----------
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr srcDc, int sx, int sy, uint rop);
    private const uint SRCCOPY = 0x00CC0028;

    /// <summary>收起缩窗并清理后,被动验证旧矩形区域是否真干净了:
    /// 清理后截一帧 → 再做一轮覆盖清理 → 再截一帧,两帧有差异 ⇒ 清理前确有
    /// 滞留像素(已被本轮清理);连做三轮直至稳定,结论与残影轮廓写 ghost.log。
    /// (旧版"位移取证"每收起都把球瞬移 500DIP 再移回,既打扰使用,判定还把
    /// 球自身计成差异——永远误报"随窗移走",盲区也只盖住球心 190DIP 半径,
    /// 面板高达 ~750DIP 的下部从未被检验。本方案覆盖整个旧窗口矩形。)</summary>
    private void PostCollapseGuard(Rect oldRectDip) => _ = PostCollapseGuardAsync(oldRectDip);

    private async Task PostCollapseGuardAsync(Rect oldRectDip)
    {
        try
        {
            await Task.Delay(420); // 等首次清理(DispelDwmGhost 内部 ~150ms)尘埃落定
            if (_mode != Mode.Free || _dragging || _stripDragging) return; // 又展开了:区域被新窗口盖住,无需验证

            var d = _dpiScale;
            var r = new RECT
            {
                Left = (int)Math.Floor(oldRectDip.Left * d),
                Top = (int)Math.Floor(oldRectDip.Top * d),
                Right = (int)Math.Ceiling(oldRectDip.Right * d),
                Bottom = (int)Math.Ceiling(oldRectDip.Bottom * d),
            };
            var w = r.Right - r.Left; var h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return;

            // 排除球矩形(收起后球是唯一合法内容,把它从检验中剔除防误报)
            var bl = (int)((_cx - BallSz / 2) * d) - r.Left - 8;
            var bt = (int)((_cy - BallSz / 2) * d) - r.Top - 8;
            var bw = (int)(BallSz * d) + 16; var bh = (int)(BallSz * d) + 16;

            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeDot");
            for (var round = 1; round <= 3; round++)
            {
                var a = CaptureZone(r.Left, r.Top, w, h);  // 清理后的现状(残留若在则在此帧)
                DispelDwmGhost(oldRectDip);                // 再补一轮覆盖清理
                await Task.Delay(260);
                if (_mode != Mode.Free || _dragging || _stripDragging) return;
                var b = CaptureZone(r.Left, r.Top, w, h);  // 二次清理后:应只剩纯背景
                if (a == null || b == null) return;

                var diff = 0;
                var diffImg = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (x >= bl && x < bl + bw && y >= bt && y < bt + bh) continue; // 球区域跳过
                    var i = (y * w + x) * 4;
                    var dd = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                    if (dd >= 40)
                    {
                        diff++;
                        diffImg[i + 3] = 255; diffImg[i] = 220; diffImg[i + 1] = 40; diffImg[i + 2] = 40;
                    }
                }

                if (diff < 200)
                {
                    if (round > 1)
                        System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ghost.log"),
                            $"{DateTime.Now:MM-dd HH:mm:ss.fff} 第{round}轮复检通过(残留已清除) diff={diff} 区域=({r.Left},{r.Top})-({r.Right},{r.Bottom})\n");
                    return; // 区域稳定:首清理后无滞留(或前几轮已清干净)
                }

                // 两帧有差异 ⇒ 清理前确有滞留像素,已在本轮被清理掉;留证并继续复检直至稳定
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ghost.log"),
                    $"{DateTime.Now:MM-dd HH:mm:ss.fff} 检出滞留像素 diff={diff} 第{round}轮已补清理 区域=({r.Left},{r.Top})-({r.Right},{r.Bottom})\n");
                SavePng(System.IO.Path.Combine(dir, $"ghost_stale_{DateTime.Now:HHmmss}.png"), a, w, h);
                SavePng(System.IO.Path.Combine(dir, $"ghost_diff_{DateTime.Now:HHmmss}.png"), diffImg, w, h);
            }
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "ghost.log"),
                $"{DateTime.Now:MM-dd HH:mm:ss.fff} 三轮补清理后仍有滞留像素!请反馈 ghost_stale/ghost_diff 截图\n");
        }
        catch { /* 自检尽力而为,不影响主流程 */ }
    }

    /// <summary>截屏指定物理矩形(含 DWM 合成结果,即肉眼所见),返回 BGRA 像素。</summary>
    private byte[]? CaptureZone(int x, int y, int w, int h)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w; bmi.bmiHeader.biHeight = -h; // top-down
        bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = IntPtr.Zero; var dib = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            var old = SelectObject(memDc, dib);
            BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY);
            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            SelectObject(memDc, old);
            return px;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void SavePng(string path, byte[] px, int w, int h)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
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

    /// <summary>快捷回复面板展开态:与记录面板同套路,优先下展、空间不足翻上、水平夹紧。</summary>
    private void LayoutQuick()
    {
        var oldRect = new Rect(Left, Top, ActualWidth, ActualHeight); // 删减条目后的收缩检测(缩窗可能留 DWM 残影)
        QuickHost.Visibility = Visibility.Visible;
        QuickHost.Measure(new Size(QuickW, double.PositiveInfinity));
        var panelH = Math.Min(QuickHost.DesiredSize.Height, WorkArea.Height - 20);

        var ballR = new Rect(_cx - BallSz / 2, _cy - BallSz / 2, BallSz, BallSz);
        double pTop = ballR.Bottom + 4;
        if (pTop + panelH > WorkArea.Bottom) pTop = Math.Max(WorkArea.Top + 2, ballR.Top - 4 - panelH);
        double pLeft = Clamp(_cx - QuickW / 2, WorkArea.Left + 2, WorkArea.Right - QuickW - 2);

        var win = Rect.Union(ballR, new Rect(pLeft, pTop, QuickW, panelH));
        Width = win.Width; Height = win.Height;
        Left = win.Left; Top = win.Top;
        BallHost.Margin = new Thickness(ballR.Left - win.Left, ballR.Top - win.Top, 0, 0);
        QuickHost.Margin = new Thickness(pLeft - win.Left, pTop - win.Top, 0, 0);
        StripHost.Visibility = Visibility.Collapsed;
        if (win.Height < oldRect.Height - 8) DispelDwmGhost(oldRect);
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
        QuickHost.Visibility = Visibility.Collapsed;
        TodoHost.Visibility = Visibility.Collapsed;
        RadialHost.Visibility = Visibility.Collapsed;
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
    // 注:收起后缩窗回球大小(以球心重新居中,球视觉不动)。
    // 不能保持展开矩形:WindowChrome 方案下透明区域并不点击穿透,大矩形是隐形空气墙。

    private void ExpandPanel(bool focusInput = true)
    {
        if (_mode != Mode.Free) return; // 吸边态由小条交互负责唤出,展开态不重复展开
        SetSelfPass(false);
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

    private bool _panelPinned;

    /// <summary>pin 按钮:固定面板(失焦/Esc/点球都不收起),再点一次解锁。</summary>
    private void PinClick(object sender, RoutedEventArgs e)
    {
        _panelPinned = !_panelPinned;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        if (BtnPin.Template?.FindName("ic", BtnPin) is System.Windows.Controls.TextBlock ic)
        {
            ic.Text = _panelPinned ? "\uE77A" : "\uE718"; // Pinned / Pin
            ic.Foreground = (Brush)FindResource(_panelPinned ? "Accent" : "TextSub");
        }
        BtnPin.ToolTip = _panelPinned ? "取消固定" : "固定面板（失焦不收起）";
    }

    private void CollapsePanel(bool animate)
    {
        if (_mode != Mode.Expanded) return;
        if (_panelPinned) { _panelPinned = false; UpdatePinVisual(); } // 显式收起时解锁
        _mode = Mode.Free;
        StartLoops(); // 恢复呼吸动画(小球窗口面,开销可忽略)
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        if (animate)
        {
            // 淡出终帧必须真正“渲染并呈现”后才能缩窗:Completed 触发时全透明帧往往
            // 尚未上屏,立即缩窗会让 DWM 滞留半透明扇区旧帧(残影必现的根因)
            fade.Completed += async (_, _) =>
            {
                await Task.Delay(60); // ~4 个合成帧
                if (_mode != Mode.Free) return; // 期间用户又打开了别的界面,交给新布局
                PanelHost.Visibility = Visibility.Collapsed;
                LayoutBallOnly(); // 内部 wasBig 检测对旧矩形触发残影清理
            };
            PanelCard.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            PanelCard.BeginAnimation(OpacityProperty, null);
            PanelHost.Visibility = Visibility.Collapsed;
            LayoutBallOnly();
        }
        SavePosition();
    }

    // ---------- 快捷回复面板(双击球唤出,点击即复制) ----------

    private bool _quickPinned;

    /// <summary>JS 发 dblclick(350ms 内连击两次):直达快捷回复(跳过环形菜单)。</summary>
    private void WebDblClick()
    {
        _lastActivity = DateTime.Now;
        if (_mode == Mode.Quick)
        {
            if (!_quickPinned) CollapseQuick(true);
            return;
        }
        if (_mode == Mode.Expanded) CollapsePanel(false);
        if (_mode == Mode.Radial) CollapseRadial(true);
        if (_mode == Mode.Free) ExpandQuick();
    }

    private void ExpandQuick()
    {
        if (_mode != Mode.Free) return; // 吸边态由小条交互负责唤出
        SetSelfPass(false);
        BallHost.Visibility = Visibility.Visible;
        _mode = Mode.Quick;
        LayoutQuick();
        QuickCard.BeginAnimation(OpacityProperty, null); // 清掉收起时的本地动画,交给展开 storyboard
        ((Storyboard)FindResource("QuickIn")).Begin(this, true);

        RefreshQuickList();
        Activate();
        Keyboard.Focus(QuickSearch);
        _lastActivity = DateTime.Now;
    }

    /// <summary>pin 按钮:固定快捷回复面板(失焦/Esc/点球都不收起),再点一次解锁。</summary>
    private void QuickPinClick(object sender, RoutedEventArgs e)
    {
        _quickPinned = !_quickPinned;
        UpdateQuickPinVisual();
    }

    private void UpdateQuickPinVisual()
    {
        if (BtnQuickPin.Template?.FindName("qic", BtnQuickPin) is System.Windows.Controls.TextBlock ic)
        {
            ic.Text = _quickPinned ? "\uE77A" : "\uE718"; // Pinned / Pin
            ic.Foreground = (Brush)FindResource(_quickPinned ? "Accent" : "TextSub");
        }
        BtnQuickPin.ToolTip = _quickPinned ? "取消固定" : "固定面板（失焦不收起）";
    }

    private void QuickCloseClick(object sender, RoutedEventArgs e) => CollapseQuick(true);

    // ---------- 待办面板(环形菜单唤出) ----------

    private bool _todoPinned;

    /// <summary>环形菜单“待办”扇区 → 展开待办面板。</summary>
    private void ExpandTodo()
    {
        if (_mode != Mode.Free) return;
        SetSelfPass(false);
        BallHost.Visibility = Visibility.Visible;
        _mode = Mode.Todo;
        LayoutTodo();
        TodoCard.BeginAnimation(OpacityProperty, null);
        ((Storyboard)FindResource("TodoIn")).Begin(this, true);

        RefreshTodoList();
        Activate();
        Keyboard.Focus(TodoInput);
        _lastActivity = DateTime.Now;
    }

    /// <summary>待办面板展开态:与其他面板同套路,优先下展、空间不足翻上、水平夹紧。</summary>
    private void LayoutTodo()
    {
        var oldRect = new Rect(Left, Top, ActualWidth, ActualHeight); // 删减条目后的收缩检测(缩窗可能留 DWM 残影)
        TodoHost.Visibility = Visibility.Visible;
        TodoHost.Measure(new Size(TodoW, double.PositiveInfinity));
        var panelH = Math.Min(TodoHost.DesiredSize.Height, WorkArea.Height - 20);

        var ballR = new Rect(_cx - BallSz / 2, _cy - BallSz / 2, BallSz, BallSz);
        double pTop = ballR.Bottom + 4;
        if (pTop + panelH > WorkArea.Bottom) pTop = Math.Max(WorkArea.Top + 2, ballR.Top - 4 - panelH);
        double pLeft = Clamp(_cx - TodoW / 2, WorkArea.Left + 2, WorkArea.Right - TodoW - 2);

        var win = Rect.Union(ballR, new Rect(pLeft, pTop, TodoW, panelH));
        Width = win.Width; Height = win.Height;
        Left = win.Left; Top = win.Top;
        BallHost.Margin = new Thickness(ballR.Left - win.Left, ballR.Top - win.Top, 0, 0);
        TodoHost.Margin = new Thickness(pLeft - win.Left, pTop - win.Top, 0, 0);
        StripHost.Visibility = Visibility.Collapsed;
        if (win.Height < oldRect.Height - 8) DispelDwmGhost(oldRect);
    }

    /// <summary>pin 按钮:固定待办面板(失焦/Esc/点球都不收起),再点一次解锁。</summary>
    private void TodoPinClick(object sender, RoutedEventArgs e)
    {
        _todoPinned = !_todoPinned;
        UpdateTodoPinVisual();
    }

    private void UpdateTodoPinVisual()
    {
        if (BtnTodoPin.Template?.FindName("tic", BtnTodoPin) is TextBlock ic)
        {
            ic.Text = _todoPinned ? "\uE77A" : "\uE718"; // Pinned / Pin
            ic.Foreground = (Brush)FindResource(_todoPinned ? "Accent" : "TextSub");
        }
        BtnTodoPin.ToolTip = _todoPinned ? "取消固定" : "固定面板（失焦不收起）";
    }

    private void TodoCloseClick(object sender, RoutedEventArgs e) => CollapseTodo(true);

    private void CollapseTodo(bool animate)
    {
        if (_mode != Mode.Todo) return;
        if (_todoPinned) { _todoPinned = false; UpdateTodoPinVisual(); } // 显式收起时解锁
        _mode = Mode.Free;
        StartLoops();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        if (animate)
        {
            // 同 CollapsePanel:等淡出终帧真正呈现后再缩窗(防 DWM 残影)
            fade.Completed += async (_, _) =>
            {
                await Task.Delay(60);
                if (_mode != Mode.Free) return;
                TodoHost.Visibility = Visibility.Collapsed;
                LayoutBallOnly(); // 内部 wasBig 检测对旧矩形触发残影清理
            };
            TodoCard.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            TodoCard.BeginAnimation(OpacityProperty, null);
            TodoHost.Visibility = Visibility.Collapsed;
            LayoutBallOnly();
        }
        SavePosition();
    }

    // ---------- 环形菜单(单击球唤出,扇区由代码生成) ----------

    /// <summary>扇区定义:角度范围以“正右方为 0°、顺时针为正”。</summary>
    private sealed record RadialSector(double StartDeg, double EndDeg, string Icon, string Label, string Tip)
    {
        public double MidDeg => (StartDeg + EndDeg) / 2;
    }

    /// <summary>三块扇区铺满整圈(各 114°、间隙 6°):视觉是一圈被切开的圆环,而非几块漂浮的弧岛。</summary>
    private static readonly RadialSector[] RadialSectors =
    {
        new(-147, -33, "\uE73E", "待办", "待办清单 · 添加 / 勾选完成"),  // 居中于正上(12 点)
        new(-27, 87, "\uE823", "速记", "工时速记 · 计时与记录"),        // 居中于右下(4~5 点)
        new(93, 207, "\uE8C8", "回复", "快捷回复 · 点击即复制"),        // 居中于左侧(9 点)
    };

    /// <summary>单击球:展开环形菜单;再单击收起。</summary>
    private void ExpandRadial()
    {
        if (_mode != Mode.Free) return;
        SetSelfPass(false);
        // 真球(WebView2)的方形 HWND 在 Win32 层盖住 WPF 渲染——斜向扇区内缘
        // 会被球光晕方形吃掉。展开期间撤下真球,由替身球无缝顶替中心。
        BallHost.Visibility = Visibility.Collapsed;
        BallStandin.Visibility = Visibility.Visible;
        StartStandinBreath();
        _mode = Mode.Radial;
        LayoutRadial();
        RadialCardOpacityReset();
        ((Storyboard)FindResource("RadialIn")).Begin(this, true);
        _lastActivity = DateTime.Now;
    }

    /// <summary>替身球呼吸(弥补真球动画;收起时停止)。</summary>
    private void StartStandinBreath()
    {
        var breath = new DoubleAnimation(1, 1.03, TimeSpan.FromSeconds(1.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        StandinBreath.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, breath);
        StandinBreath.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, breath);
    }

    private void StopStandinBreath()
    {
        StandinBreath.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        StandinBreath.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>清掉收起时的本地动画并复位透明度与缩放(交给 RadialIn)。</summary>
    private void RadialCardOpacityReset()
    {
        RadialHost.BeginAnimation(OpacityProperty, null);
        RadialScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RadialScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>环形菜单布局:窗口为以球为中心的正方形,扇区画在 Canvas 上。</summary>
    private void LayoutRadial()
    {
        RadialHost.Visibility = Visibility.Visible;
        const double size = 2 * (BallSz / 2 + RadialOut + RadialPad); // 球心到窗口边的直径(含投影余量)
        Width = size; Height = size;
        Left = _cx - size / 2; Top = _cy - size / 2;
        BallHost.Margin = new Thickness((size - BallSz) / 2); // 球居中(收起恢复时仍对齐)
        BallStandin.Margin = new Thickness((size - 70) / 2);   // 替身球(70px)同圆心居中
        RadialHost.Margin = new Thickness(0);
        RadialCanvas.Width = size; RadialCanvas.Height = size;
        PanelHost.Visibility = Visibility.Collapsed;
        QuickHost.Visibility = Visibility.Collapsed;
        TodoHost.Visibility = Visibility.Collapsed;
        StripHost.Visibility = Visibility.Collapsed;
        BuildRadialSectors();
    }

    /// <summary>把扇区列表画成 Canvas 上的可点区域:整圈环形分块,带描边与投影,
    /// 与球之间留出明显缝隙——此前用无描边的 Border 裁剪,奶白填充与球融成一片,
    /// 且三块 60° 小弧间隙过大,看着像漂浮的箭头块(“遮罩没做好”的观感来源)。</summary>
    private void BuildRadialSectors()
    {
        RadialCanvas.Children.Clear();
        const double c = BallSz / 2 + RadialOut + RadialPad;     // 画布中心(球心)
        const double r0 = 50, r1 = BallSz / 2 + RadialOut - 4;   // 内缘与球留 8px 缝隙,外缘留边

        var normal = new SolidColorBrush(Color.FromArgb(0xE6, 0xF7, 0xF5, 0xEF)); // 纸感常亮
        var hover = new SolidColorBrush(Color.FromArgb(0xCC, 0xC0, 0x45, 0x22));  // 朱砂高亮
        var stroke = new SolidColorBrush(Color.FromArgb(0x4D, 0xC0, 0x45, 0x22)); // 与小条/气泡同款描边
        var ink = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x1E));
        var accent = new SolidColorBrush(Color.FromRgb(0xC0, 0x45, 0x22));
        var cream = new SolidColorBrush(Color.FromRgb(0xF7, 0xF5, 0xEF));

        foreach (var s in RadialSectors)
        {
            var sector = new WpfPath
            {
                Data = SectorGeometry(c, c, r0, r1, s.StartDeg, s.EndDeg),
                Fill = normal,
                Stroke = stroke,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                Cursor = Cursors.Hand,
                ToolTip = s.Tip,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 14, ShadowDepth = 4,
                    Opacity = 0.24, Direction = 270,
                },
            };
            sector.MouseLeftButtonUp += (_, _) => SectorOpen(s);

            // 扇区标签:图标 + 文字,摆在扇区中点角度上;不参与命中,悬停时随扇区换成奶白
            var iconTb = new TextBlock
            {
                Text = s.Icon, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13, Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
            };
            var textTb = new TextBlock
            {
                Text = s.Label, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = ink, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(iconTb);
            label.Children.Add(textTb);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var mid = RadialPoint(c, c, (r0 + r1) / 2, s.MidDeg);
            var lb = new Border { Child = label, IsHitTestVisible = false };
            Canvas.SetLeft(lb, mid.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(lb, mid.Y - label.DesiredSize.Height / 2);

            sector.MouseEnter += (_, _) =>
            {
                sector.Fill = hover;
                iconTb.Foreground = cream;
                textTb.Foreground = cream;
            };
            sector.MouseLeave += (_, _) =>
            {
                sector.Fill = normal;
                iconTb.Foreground = accent;
                textTb.Foreground = ink;
            };

            RadialCanvas.Children.Add(sector);
            RadialCanvas.Children.Add(lb);
        }
    }

    /// <summary>环形扇区几何:外弧顺时针、内弧逆时针闭合。</summary>
    private static StreamGeometry SectorGeometry(double cx, double cy, double r0, double r1, double startDeg, double endDeg)
    {
        var geo = new StreamGeometry();
        using (var dc = geo.Open())
        {
            dc.BeginFigure(RadialPoint(cx, cy, r1, startDeg), true, true);
            dc.ArcTo(RadialPoint(cx, cy, r1, endDeg), new Size(r1, r1), 0, false, SweepDirection.Clockwise, true, true);
            dc.LineTo(RadialPoint(cx, cy, r0, endDeg), true, false);
            dc.ArcTo(RadialPoint(cx, cy, r0, startDeg), new Size(r0, r0), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        geo.Freeze();
        return geo;
    }

    private static Point RadialPoint(double cx, double cy, double r, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    /// <summary>点选扇区:淡出菜单后进入对应功能页(几何变化与扇区消失隔帧,防残影)。</summary>
    private void SectorOpen(RadialSector s)
    {
        if (_mode != Mode.Radial) return;
        CollapseRadial(true, () =>
        {
            switch (s.Label)
            {
                case "待办": ExpandTodo(); break;
                case "速记": ExpandPanel(); break;
                case "回复": ExpandQuick(); break;
            }
        });
    }

    /// <summary>收起环形菜单。followUp:收起后立即进入的功能页——其布局自带窗口几何,
    /// 跳过中间缩球(少一次 resize)。淡出与几何变化隔一渲染帧:同帧内“清像素+缩窗”
    /// 会让 DWM 把含扇区的旧帧滞留在窗口外区域(图形残留)。</summary>
    private void CollapseRadial(bool animate, Action? followUp = null)
    {
        if (_mode != Mode.Radial) return;
        _mode = Mode.Free;
        // 真球归位、替身退场(瞬间无缝:两者同圆心同尺寸)
        BallStandin.Visibility = Visibility.Collapsed;
        StopStandinBreath();
        BallHost.Visibility = Visibility.Visible;
        StartLoops();

        void Finish()
        {
            if (_mode == Mode.Radial) return; // 收尾前用户又展开了环形:本次收起作废
            var oldRadial = new Rect(Left, Top, Width, Height); // 扇区刚淡出后的旧环形矩形
            RadialHost.Visibility = Visibility.Collapsed;
            if (followUp != null)
            {
                followUp(); // followUp 自带布局与模式切换
                // 直达功能页:窗口从环形矩形直接变形为面板矩形,被遗弃的环形区域
                // (面板矩形盖不住的上缘条带)此前无人清理——补一次覆盖清理,
                // 否则该区域的扇区旧帧可能滞留成 DWM 残影(收起面板时也不会再覆盖它)
                var newRect = new Rect(Left, Top, Width, Height);
                if (!newRect.Contains(oldRadial)) DispelDwmGhost(oldRadial);
            }
            else if (_mode == Mode.Free) LayoutBallOnly(); // 内部 wasBig 检测对旧矩形触发残影清理
        }

        if (animate)
        {
            // GPU 动画:淡出同时向球心缩小(视觉上被吸进球里),全程不逐帧改窗口尺寸,
            // 避免逐帧 resize(拖着 WebView2 一起缩)造成的卡顿感;终帧后再一次性缩窗 + 清理
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var shrinkX = new DoubleAnimation(1, 0.85, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var shrinkY = new DoubleAnimation(1, 0.85, TimeSpan.FromMilliseconds(120))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            // 同 CollapsePanel:Completed 触发时全透明帧往往尚未上屏,必须真实等待
            // ~4 个合成帧再改窗口几何,否则 DWM 滞留半透明扇区旧帧(点开再点收必现残影)
            fade.Completed += async (_, _) =>
            {
                await Task.Delay(60);
                Finish();
            };
            RadialHost.BeginAnimation(OpacityProperty, fade);
            RadialScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX);
            RadialScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        }
        else if (followUp != null)
        {
            RadialHost.BeginAnimation(OpacityProperty, null);
            Dispatcher.BeginInvoke(DispatcherPriority.Render, Finish);
        }
        else
        {
            RadialHost.BeginAnimation(OpacityProperty, null);
            RadialHost.Visibility = Visibility.Collapsed;
            LayoutBallOnly();
        }
        SavePosition();
    }
    private void CollapseQuick(bool animate)
    {
        if (_mode != Mode.Quick) return;
        if (_quickPinned) { _quickPinned = false; UpdateQuickPinVisual(); } // 显式收起时解锁
        _mode = Mode.Free;
        StartLoops(); // 恢复呼吸动画(小球窗口面,开销可忽略)
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        if (animate)
        {
            // 同 CollapsePanel:等淡出终帧真正呈现后再缩窗(防 DWM 残影)
            fade.Completed += async (_, _) =>
            {
                await Task.Delay(60);
                if (_mode != Mode.Free) return;
                QuickHost.Visibility = Visibility.Collapsed;
                LayoutBallOnly(); // 内部 wasBig 检测对旧矩形触发残影清理
            };
            QuickCard.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            QuickCard.BeginAnimation(OpacityProperty, null);
            QuickHost.Visibility = Visibility.Collapsed;
            LayoutBallOnly();
        }
        SavePosition();
    }

    // ---------- 吸边 ----------

    private void EnterDocked(bool animate)
    {
        if (_mode == Mode.Expanded) CollapsePanel(false);
        if (_mode == Mode.Quick) CollapseQuick(false);
        if (_mode == Mode.Todo) CollapseTodo(false);
        if (_mode == Mode.Radial) CollapseRadial(false);
        _mode = Mode.Docked;
        SetSelfPass(false); // 上面的收起链可能开了整窗穿透:小条必须可点,先关掉
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
        else LayoutBallOnly(shrink: true); // 唤出即回到小球窗
        SetSelfPass(false); // 吸附期间穿透可能被误开:球窗由 WebView 圆外穿透负责即可
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
        // 整窗穿透态下 WPF IsMouseOver 恒为 false(鼠标被 Win32 层穿透了),须用光标
        // 物理坐标判定"鼠标在球窗内"——否则悬停球上不动 60 秒会被误判空闲而吸边
        var over = IsMouseOver;
        if (!over && _selfPass)
        {
            var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (self != IntPtr.Zero && GetWindowRect(self, out var sr))
            {
                GetCursorPos(out var cp);
                over = cp.X >= sr.Left && cp.X < sr.Right && cp.Y >= sr.Top && cp.Y < sr.Bottom;
            }
        }
        if (over) { _lastActivity = DateTime.Now; return; }
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
            // 动画后新矩形未完全覆盖旧矩形(吸附↔球互切、面板翻上翻下都会缩小或平移):
            // 被遗弃的旧区域可能滞留 DWM 旧帧(如小条残影贴在球旁),用覆盖窗强制重合成
            if (!to.Contains(from)) DispelDwmGhost(from);
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
            // Runtime 固定:WebView2 Evergreen 自动更新(2026-09-07 凌晨升至 152.0.4191.66)
            // 后鼠标事件不再分发给页面——renderer 动画照常但 JS pointer 事件全灭,球完全
            // 不可点。固定回 151.0.4129.101(长期验证正常)并放宽 TargetCompatibleBrowserVersion
            // 允许 SDK 搭配;目录日后被 EdgeUpdate 清理时回退 Evergreen 默认。
            var pinnedRuntime = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\151.0.4129.101";
            CoreWebView2Environment? env = null;
            if (Directory.Exists(pinnedRuntime))
            {
                try
                {
                    var opts = new CoreWebView2EnvironmentOptions { TargetCompatibleBrowserVersion = "151.0.4129.101" };
                    env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: pinnedRuntime, userDataFolder: null, options: opts);
                }
                catch { env = null; /* 版本不兼容等异常:回退 Evergreen 默认 */ }
            }
            if (env != null) await BallWeb.EnsureCoreWebView2Async(env);
            else await BallWeb.EnsureCoreWebView2Async();

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
                    var stream = OpenWebAsset(webDir, localPath);
                    if (stream != null)
                    {
                        var mime = GetMimeType(localPath);
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
                        case "dblclick": // 双击球 → 快捷回复面板
                            Dispatcher.BeginInvoke(WebDblClick); break;
                    }
                }
                catch { /* 解析失败静默忽略 */ }
            };

            // 直接导航:资源由 OpenWebAsset 提供(磁盘 Web 目录优先,内嵌资源兜底),
            // 不再要求磁盘目录存在(单文件独立分发时只有 exe)
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

    /// <summary>打开 Web 前端资源:优先读磁盘(开发态),否则读内嵌资源(单文件发布独立分发)。</summary>
    private static Stream? OpenWebAsset(string webDir, string localPath)
    {
        var filePath = Path.Combine(webDir, localPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(filePath)) return File.OpenRead(filePath);
        var name = typeof(MainWindow).Namespace + ".Web." + localPath.Replace('/', '.');
        return System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
    }

    /// <summary>
    /// 球外空气墙治理:WebView2 是 84×84 方形窗口,球是圆的,方形四角会挡住下方软件的点击。
    /// 每 40ms 检查光标:在方形内、圆形外(半径 90% 半边长)时给 WebView2 的 hwnd 加
    /// WS_EX_TRANSPARENT 让点击穿透到下方应用;回到圆内自动恢复,球的交互不受影响。
    /// </summary>
    private void UpdateWebPass()
    {
        try
        {
            // 整窗穿透一致性纠偏(40ms 轮询兜底):收起保持大矩形(Free 且窗口大于球)必须
            // 穿透;其他任何态不允许——状态转换遗漏时在这里自动纠正
            var wantSelf = _mode == Mode.Free && (ActualWidth > BallSz + 8 || ActualHeight > BallSz + 8);
            if (wantSelf != _selfPass) SetSelfPass(wantSelf);

            var h = BallWeb.Handle;
            if (h == IntPtr.Zero) return;
            var want = false;
            if (Visibility == Visibility.Visible && BallHost.Visibility == Visibility.Visible)
            {
                if (GetWindowRect(h, out var r))
                {
                    GetCursorPos(out var p);
                    double cx = (r.Left + r.Right) / 2.0, cy = (r.Top + r.Bottom) / 2.0;
                    double rad = (r.Right - r.Left) / 2.0 * 0.90; // 球可视半径约占 88%,留少量余量
                    double dx = p.X - cx, dy = p.Y - cy;
                    bool inSquare = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
                    want = inSquare && dx * dx + dy * dy > rad * rad;
                }
            }
            if (want == _webPass) return;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, want ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
            _webPass = want;
        }
        catch { /* WebView2 未就绪等异常时静默跳过 */ }
    }

    /// <summary>向 emotion-ball 推送表情。</summary>
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
