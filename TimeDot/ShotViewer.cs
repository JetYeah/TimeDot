using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeDot;

/// <summary>
/// 内置截图大图查看器:不依赖系统图片关联——部分机器"照片"应用注册损坏
/// (AppX ProgId 缺 Shell\Open\command 键),Process.Start 打开 PNG 会抛
/// "没有应用程序与此文件的指定操作进行关联",若被空 catch 吞掉就表现为点击无反应。
/// 交互:滚轮缩放(以光标为中心)、按住拖动平移、双击复位、Esc 关闭。
/// </summary>
internal sealed class ShotViewer : Window
{
    private readonly ScrollViewer _sv = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        Cursor = Cursors.Hand,
    };
    private readonly ScaleTransform _zoom = new();
    private readonly Image _img = new()
    {
        Stretch = Stretch.None, // 原始像素渲染,缩放统一交给 LayoutTransform
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _status = new();
    private readonly BitmapImage _bi = new();
    private string _meta = "";
    private string? _flash; // 复制成功等临时提示,短暂显示后恢复常规状态文案
    private Point _dragStart, _scrollStart;
    private bool _dragging;
    private double _fitScale = 1;

    public ShotViewer(string fullPath, string tip)
    {
        Title = "截图预览 · TimeDot";
        Width = Math.Min(920, SystemParameters.WorkArea.Width * 0.7);
        Height = Math.Min(640, SystemParameters.WorkArea.Height * 0.7);
        MinWidth = 360;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xEE, 0xE6)); // 纸感底色

        var bi = _bi;
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad; // 立即解码并释放文件句柄
        bi.UriSource = new Uri(fullPath);
        bi.EndInit();
        bi.Freeze();
        _img.Source = bi;
        _img.LayoutTransform = _zoom;

        // 内容比视口小时 ScrollViewer 会按对齐居中;比视口大时正常滚动,两种状态都成立
        _sv.Content = _img;

        // 底部状态条:左侧截图信息 + 缩放比例/操作提示,右侧复制按钮
        _meta = $"{tip} · {bi.PixelWidth}×{bi.PixelHeight}";
        _status.FontSize = 11.5;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x65, 0x5A));
        _status.Margin = new Thickness(14, 8, 8, 10);
        _status.VerticalAlignment = VerticalAlignment.Center;
        var copyBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE8C8", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12,
            },
            ToolTip = "复制图片 (Ctrl+C)",
            Focusable = false,
            Cursor = Cursors.Hand,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copyBtn.Click += (_, _) => CopyImage();
        var barContent = new DockPanel();
        DockPanel.SetDock(copyBtn, Dock.Right);
        barContent.Children.Add(copyBtn);
        barContent.Children.Add(_status);
        UpdateStatus();
        var bar = new Border
        {
            Child = barContent,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF5, 0xEF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xC0, 0x45, 0x22)),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_sv);
        Content = root;

        _sv.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            SetZoom(_zoom.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(_sv));
        };
        _img.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) // 双击复位(Image 非 Control,没有 MouseDoubleClick)
            {
                SetZoom(_fitScale, new Point(_sv.ViewportWidth / 2, _sv.ViewportHeight / 2));
                return;
            }
            _dragging = true;
            _dragStart = e.GetPosition(_sv);
            _scrollStart = new Point(_sv.HorizontalOffset, _sv.VerticalOffset);
            _img.CaptureMouse();
        };
        _img.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetPosition(_sv);
            // 反向补偿位移:拖右图走右。负值会被 ScrollViewer 自动钳到 0
            _sv.ScrollToHorizontalOffset(_scrollStart.X - (p.X - _dragStart.X));
            _sv.ScrollToVerticalOffset(_scrollStart.Y - (p.Y - _dragStart.Y));
        };
        _img.MouseLeftButtonUp += (_, _) => { _dragging = false; _img.ReleaseMouseCapture(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); return; }
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                CopyImage();
            }
        };

        // 首次布局完成后按窗口尺寸自适应缩放(ContentRendered 时视口尺寸已就绪)
        ContentRendered += (_, _) =>
        {
            if (_fitScale >= 1) return; // 只做一次
            double w = _sv.ViewportWidth - 32, h = _sv.ViewportHeight - 32;
            if (w <= 0 || h <= 0) return;
            _fitScale = Math.Min(1, Math.Min(w / bi.PixelWidth, h / bi.PixelHeight));
            SetZoom(_fitScale, new Point(0, 0));
        };
    }

    /// <summary>复制整图到剪贴板(可粘贴到聊天/文档,与系统看图软件行为一致)。</summary>
    private void CopyImage()
    {
        try
        {
            Clipboard.SetImage(_bi);
            Flash("已复制整图到剪贴板");
        }
        catch (Exception ex) { Flash("复制失败: " + ex.Message); }
    }

    /// <summary>状态条短暂显示提示,1.6 秒后恢复常规文案。</summary>
    private void Flash(string msg)
    {
        _flash = msg;
        UpdateStatus();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (_, _) => { t.Stop(); _flash = null; UpdateStatus(); };
        t.Start();
    }

    private void UpdateStatus()
    {
        _status.Text = _flash
            ?? $"{_meta} · {Math.Round(_zoom.ScaleX * 100)}% · 滚轮缩放 · 拖动平移 · 双击复位 · Ctrl+C 复制 · Esc 关闭";
    }

    /// <summary>设置缩放并以 vp(视口坐标)为锚点保持该点对应的内容不动。</summary>
    private void SetZoom(double s, Point vp)
    {
        s = Math.Clamp(s, 0.05, 10);
        var old = _zoom.ScaleX;
        // 光标处的"内容坐标"(内容小于视口居中时 offset 恒 0,误差可忽略——此时内容会自动回中)
        double cx = (_sv.HorizontalOffset + vp.X) / old;
        double cy = (_sv.VerticalOffset + vp.Y) / old;
        _zoom.ScaleX = _zoom.ScaleY = s;
        _sv.UpdateLayout();
        _sv.ScrollToHorizontalOffset(cx * s - vp.X);
        _sv.ScrollToVerticalOffset(cy * s - vp.Y);
        UpdateStatus();
    }
}
