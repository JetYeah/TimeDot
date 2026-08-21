using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TimeDot;

/// <summary>
/// 全屏框选截图层:按住拖动画出选区,松手后先隐藏遮罩再抓屏(Esc/右键取消)。
/// 纯 WPF 内容、无 HwndHost 子控件,这里用 AllowsTransparency 是安全的
/// (分层窗口的输入陷阱只影响 WebView2 等子 HWND 控件)。
/// </summary>
internal sealed class SnipWindow : Window
{
    private const string HintDefault = "按住鼠标拖动框选截图区域 · Esc 取消";

    private readonly Canvas _root = new();
    private readonly Rectangle _rect = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x45, 0x22)),
        StrokeThickness = 1.5,
        Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xC0, 0x45, 0x22)),
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _hint = new()
    {
        Text = HintDefault,
        FontSize = 15,
        Foreground = Brushes.White,
        Opacity = 0.85,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private Point _start;
    private bool _dragging;

    /// <summary>截图 PNG 字节;用户取消时为 null。</summary>
    public byte[]? Result { get; private set; }

    public SnipWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        Cursor = Cursors.Cross;
        // 覆盖整个虚拟屏幕(多显示器时全含)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)); // 半透明遮罩

        _root.Children.Add(_hint);
        _root.Children.Add(_rect);
        Content = _root;

        MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _hint.Text = HintDefault; // 恢复默认提示(上一次误触时改过)
            _start = e.GetPosition(_root);
            _rect.Visibility = Visibility.Visible;
            UpdateRect(_start, _start);
            CaptureMouse(); // 拖拽期间持续接收事件,光标飘出窗口也不丢
        };
        MouseMove += (_, e) =>
        {
            if (_dragging) UpdateRect(_start, e.GetPosition(_root));
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            Finish(_start, e.GetPosition(_root));
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void UpdateRect(Point a, Point b)
    {
        Canvas.SetLeft(_rect, Math.Min(a.X, b.X));
        Canvas.SetTop(_rect, Math.Min(a.Y, b.Y));
        _rect.Width = Math.Abs(a.X - b.X);
        _rect.Height = Math.Abs(a.Y - b.Y);
    }

    private async void Finish(Point a, Point b)
    {
        try
        {
            // 先把 DIP 选区换算成物理屏幕像素(PointToScreen 返回物理坐标,天然处理 DPI)
            var pa = PointToScreen(a);
            var pb = PointToScreen(b);
            var x = (int)Math.Round(Math.Min(pa.X, pb.X));
            var y = (int)Math.Round(Math.Min(pa.Y, pb.Y));
            var w = (int)Math.Round(Math.Abs(pa.X - pb.X));
            var h = (int)Math.Round(Math.Abs(pa.Y - pb.Y));
            MainWindow.LogShot($"松手: 物理选区 ({x},{y}) {w}x{h}");
            if (w < 4 || h < 4)
            {
                // 单击未拖动:不关闭、不清空选区,提示需要按住拖动
                _rect.Visibility = Visibility.Collapsed;
                _hint.Text = "请按住鼠标拖动出一个矩形选区";
                return;
            }
            Hide();
            await Task.Delay(250); // 等遮罩从屏幕上消失,避免把遮罩截进画面
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            Result = ms.ToArray();
            MainWindow.LogShot($"抓屏成功: {Result.Length} 字节");
        }
        catch (Exception ex)
        {
            MainWindow.LogShot("抓屏异常: " + ex.Message);
        }
        Close();
    }
}
