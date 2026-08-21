using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TimeDot.Core;

namespace TimeDot;

public partial class MainWindow
{
    private readonly ObservableCollection<RecordViewModel> _vms = new();
    private string _searchQuery = "";

    // ---------- 列表刷新 ----------

    private void RefreshList(string? flashId = null)
    {
        var hits = _searchQuery.Length == 0 ? null : _store.Search(_searchQuery).Select(x => x.Id).ToHashSet();
        var sel = _store.Sorted().Where(r => hits == null || hits.Contains(r.Id)).ToList();

        // 就地同步,避免整表重建打断正在编辑的备注
        for (var i = _vms.Count - 1; i >= 0; i--)
            if (sel.All(r => r.Id != _vms[i].Id)) _vms.RemoveAt(i);
        for (var i = 0; i < sel.Count; i++)
        {
            var existing = _vms.FirstOrDefault(v => v.Id == sel[i].Id);
            if (existing == null) _vms.Insert(i, new RecordViewModel(sel[i], _store));
            else
            {
                var cur = _vms.IndexOf(existing);
                if (cur != i) _vms.Move(cur, i);
                existing.Rebind(sel[i]);
            }
        }

        List.ItemsSource = _vms;
        EmptyHint.Visibility = _vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var vm in _vms) vm.RefreshStats(DateTime.Now);

        if (flashId != null) FlashRow(flashId);
        UpdateRunningVisuals();
    }

    private void FlashRow(string id)
    {
        var vm = _vms.FirstOrDefault(v => v.Id == id);
        if (vm == null) return;
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(vm) is not ContentPresenter cp) return;
        if (FindVisualChild<Border>(cp) is not { } row) return;
        row.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var r = FindVisualChild<T>(child);
            if (r != null) return r;
        }
        return null;
    }

    // ---------- 快速新建 ----------

    private void AddClick(object sender, RoutedEventArgs e) => AddRecord();

    private void ProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Keyboard.Focus(InUser); e.Handled = true; }
    }

    private void UserKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddRecord(); e.Handled = true; }
    }

    private void AddRecord()
    {
        var project = InProject.Text.Trim();
        var user = InUser.Text.Trim();
        if (project.Length == 0)
        {
            // 空项目号:轻闪提示,不生成空记录
            var a = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(150)) { AutoReverse = true };
            InProject.BeginAnimation(OpacityProperty, a);
            Keyboard.Focus(InProject);
            return;
        }
        var r = _store.Add(project, user);
        InProject.Clear();
        InUser.Clear();
        SendEmotion("30"); // 开心(新增记录)
        // 1.5 秒后恢复平静/专注
        _ = Task.Delay(1500).ContinueWith(_ =>
            Dispatcher.Invoke(() => SendEmotion(_store.RunningRecord != null ? "22" : "02")));
        RefreshList(r.Id);
        Keyboard.Focus(InProject);
    }

    // ---------- 计时 / 删除 / 备注 ----------

    private void TimerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordViewModel vm) return;
        var wasRunning = _store.RunningRecord != null;
        _store.ToggleStart(vm.Id);
        var nowRunning = _store.RunningRecord != null;
        // 计时状态变化时触发对应表情
        if (nowRunning && !wasRunning)
        {
            SendEmotion("22"); // 专注
            SendAction("spin");
        }
        else if (!nowRunning && wasRunning)
        {
            SendEmotion("02"); // 平静
            SendAction("burst", 16); // 纸屑庆祝
        }
        RefreshList();
        UpdateTrayText();
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordViewModel vm) return;
        if (!vm.ConfirmDelete)
        {
            vm.ConfirmDelete = true;
            SendEmotion("11"); // 紧张(删除确认)
            return; // 第一次点:进入待确认;3 秒未复点自动撤销
        }
        _store.Delete(vm.Id);
        SendEmotion("02"); // 恢复平静
        RefreshList();
    }

    private void NoteLostFocus(object sender, RoutedEventArgs e) { } // 保留挂点:旧备注框已升级为标签区

    // ---------- 标签气泡 ----------

    private RecordViewModel? _tagPopupVm;

    /// <summary>行首“标签”按钮:弹出气泡,可输入新标签或从历史标签中点选。</summary>
    private void TagBtnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RecordViewModel vm) return;
        _tagPopupVm = vm;
        TagPopupInput.Clear();
        RefreshTagSuggest();
        TagPopup.PlacementTarget = fe;
        TagPopup.HorizontalOffset = fe.ActualWidth - 244; // 右缘对齐按钮右缘,避免溢出屏幕右边界
        TagPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => TagPopupInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>重建气泡里的历史标签建议(最近填过的在前,排除该行已有的)。</summary>
    private void RefreshTagSuggest()
    {
        TagPopupSuggest.Children.Clear();
        if (_tagPopupVm == null) return;
        foreach (var t in _store.SuggestTags(_tagPopupVm.Tags))
        {
            var chip = new Button { Content = t, Tag = t, Style = (Style)FindResource("SuggestChip") };
            chip.Click += SuggestChipClick;
            TagPopupSuggest.Children.Add(chip);
        }
        TagPopupSuggestLabel.Visibility = TagPopupSuggest.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SuggestChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && _tagPopupVm != null)
        {
            _tagPopupVm.AddTag(t);
            CloseTagPopup();
        }
    }

    private void TagPopupInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseTagPopup(); e.Handled = true; return; }
        if (e.Key == Key.Enter)
        {
            var t = TagPopupInput.Text.Trim();
            if (t.Length > 0 && _tagPopupVm != null) _tagPopupVm.AddTag(t);
            CloseTagPopup();
            e.Handled = true;
        }
    }

    private void CloseTagPopup()
    {
        TagPopup.IsOpen = false;
        _tagPopupVm = null;
    }

    private void TagRemoveClick(object sender, RoutedEventArgs e)
    {
        // 标签芯片的 DataContext 是标签字符串,沿可视树向上找到行视图模型
        if (sender is not FrameworkElement fe || fe.DataContext is not string tag) return;
        DependencyObject? d = fe;
        while (d != null)
        {
            if (d is FrameworkElement f && f.DataContext is RecordViewModel vm)
            {
                vm.RemoveTag(tag);
                return;
            }
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
    }

    // ---------- 截图 ----------

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    /// <summary>截图诊断日志(exe 同目录 web.log,与 WebView2 诊断共用;SnipWindow 也用它)。</summary>
    internal static void LogShot(string msg)
    {
        try { File.AppendAllText(WebLogPath, $"{DateTime.Now:HH:mm:ss.fff} [shot] {msg}\n"); } catch { }
    }

    /// <summary>全局 Ctrl+V:剪贴板有图片(位图或图片文件)且焦点在某行内时,把图粘成该行的截图。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control
            && !Clipboard.ContainsText()
            && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList())
            && FindRowVm(Keyboard.FocusedElement as DependencyObject ?? this) is { } vm)
        {
            PasteShotTo(vm);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>从剪贴板收集图片:位图(截屏工具复制)或图片文件(资源管理器复制);统一编码为 PNG 字节。</summary>
    private List<byte[]> CollectClipboardImages()
    {
        var list = new List<byte[]>();
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource src)
            {
                list.Add(EncodePng(src));
            }
            else if (Clipboard.ContainsFileDropList())
            {
                foreach (var f in Clipboard.GetFileDropList().Cast<string>())
                    if (TryReadImageFile(f, out var png)) list.Add(png);
            }
        }
        catch (Exception ex) { LogShot("剪贴板读取失败: " + ex.Message); }
        return list;
    }

    private static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static bool TryReadImageFile(string file, out byte[] png)
    {
        png = Array.Empty<byte>();
        try
        {
            if (!ImageExts.Contains(Path.GetExtension(file).ToLowerInvariant())) return false;
            var img = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            png = EncodePng(img);
            return png.Length > 0;
        }
        catch (Exception ex) { LogShot($"读取图片文件失败 {file}: {ex.Message}"); return false; }
    }

    /// <summary>把图片作为截图追加到指定记录(仅 Ctrl+V 粘贴剪贴板场景)。</summary>
    private void PasteShotTo(RecordViewModel vm)
    {
        var added = 0;
        foreach (var png in CollectClipboardImages())
            if (_store.AddScreenshot(vm.Id, png) != null) added++;
        LogShot($"粘贴完成: 成功{added}张");
        if (added > 0)
        {
            vm.SyncShots();
            SendEmotion("30"); // 开心(收到截图)
            _ = Task.Delay(1500).ContinueWith(_ =>
                Dispatcher.Invoke(() => SendEmotion(_store.RunningRecord != null ? "22" : "02")));
        }
    }

    /// <summary>相机按钮:藏起球和面板后弹全屏框选层,拖动选区即截图(Esc/右键取消)。</summary>
    private async void ShotClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RecordViewModel vm) return;
        try
        {
            LogShot("相机点击: 进入框选截图");
            // 先把球和面板从屏幕上藏掉,避免被截进画面;等一帧重绘
            Visibility = Visibility.Collapsed;
            await Task.Delay(250);
            // 不能用 ShowDialog:框选层抓屏前会先 Hide 自己,
            // 而模态窗口一旦 Hide,ShowDialog 会提前返回拿到空结果。
            // 改用 Show + Closed 事件异步等待真正的完成时机。
            var snip = new SnipWindow();
            var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            snip.Closed += (_, _) => done.TrySetResult(snip.Result);
            snip.Show();
            var png = await done.Task;
            LogShot($"框选结束: {(png == null ? "取消" : $"{png.Length} 字节")}");
            if (png is { Length: > 0 } && _store.AddScreenshot(vm.Id, png) != null)
            {
                vm.SyncShots();
                SendEmotion("30"); // 开心(收到截图)
                _ = Task.Delay(1500).ContinueWith(_ =>
                    Dispatcher.Invoke(() => SendEmotion(_store.RunningRecord != null ? "22" : "02")));
            }
        }
        catch (Exception ex) { LogShot("截图异常: " + ex); }
        finally { Visibility = Visibility.Visible; }
    }

    private void ShotRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ScreenshotVm shot) return;
        var vm = FindRowVm(fe);
        if (vm == null) return;
        if (_store.DeleteScreenshot(vm.Id, shot.Id)) vm.SyncShots();
    }

    private void ShotOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ScreenshotVm shot) return;
        try
        {
            if (File.Exists(shot.FullPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shot.FullPath) { UseShellExecute = true });
        }
        catch { /* 无关联查看器时不致崩溃 */ }
    }

    private RecordViewModel? FindRowVm(DependencyObject start)
    {
        DependencyObject? d = start;
        while (d != null)
        {
            if (d is FrameworkElement f && f.DataContext is RecordViewModel vm) return vm;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---------- 搜索 ----------

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = InSearch.Text.Trim();
        BtnClear.Visibility = _searchQuery.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { InSearch.Clear(); e.Handled = true; }
    }

    private void ClearSearchClick(object sender, RoutedEventArgs e)
    {
        InSearch.Clear();
        Keyboard.Focus(InSearch);
    }

    private void CollapseClick(object sender, RoutedEventArgs e) => CollapsePanel(true);
}

/// <summary>行视图模型:只承载展示态,真实数据在 RecordStore。</summary>
public sealed class RecordViewModel : INotifyPropertyChanged
{
    private readonly RecordStore _store;
    private WorkRecord _r;
    private DispatcherTimer? _confirmReset;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public RecordViewModel(WorkRecord r, RecordStore store) { _r = r; _store = store; SyncTags(); SyncShots(); }

    public override string ToString() => $"{Project} · {User}"; // 供 UIA/无障碍命名

    public string Id => _r.Id;
    public string Project => _r.Project;
    public string User => _r.User;

    /// <summary>短语标签列表(与 _r.Tags 同步的展示副本)。</summary>
    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>提交一个标签(去空白/去重)并刷新展示副本。</summary>
    public void AddTag(string tag)
    {
        if (_store.AddTag(_r.Id, tag)) SyncTags();
    }

    public void RemoveTag(string tag)
    {
        if (_store.RemoveTag(_r.Id, tag)) SyncTags();
    }

    private void SyncTags()
    {
        Tags.Clear();
        foreach (var t in _r.Tags) Tags.Add(t);
    }

    /// <summary>自由文字备注(可多行),经绑定实时写库。</summary>
    public string Note
    {
        get => _r.Note ?? "";
        set
        {
            var v = value ?? "";
            if ((_r.Note ?? "") == v) return;
            _store.UpdateNote(_r.Id, v); // 防抖落盘在 store 内
            Raise(nameof(Note));
        }
    }

    /// <summary>截图缩略图列表(与 _r.Screenshots 同步的展示副本)。</summary>
    public ObservableCollection<ScreenshotVm> Shots { get; } = new();

    public void SyncShots()
    {
        Shots.Clear();
        foreach (var s in _r.Screenshots) Shots.Add(new ScreenshotVm(s, _store.ShotPath(_r, s)));
    }

    public bool IsRunning => _r.RunningSession != null;

    public string DurationText { get; private set; } = "0:00";

    public string MetaText { get; private set; } = "";

    public bool ConfirmDelete
    {
        get => _confirmReset != null;
        set
        {
            if (value)
            {
                _confirmReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _confirmReset.Tick += (_, _) => { _confirmReset!.Stop(); _confirmReset = null; Raise(nameof(ConfirmDelete)); };
                _confirmReset.Start();
            }
            else
            {
                _confirmReset?.Stop();
                _confirmReset = null;
            }
            Raise(nameof(ConfirmDelete));
        }
    }

    /// <summary>外部数据变化(如排序、重开)后重挂到新的记录实例。</summary>
    public void Rebind(WorkRecord r) { _r = r; RefreshStats(DateTime.Now); SyncTags(); SyncShots(); Raise(nameof(Project)); Raise(nameof(User)); Raise(nameof(Note)); }

    public void RefreshStats(DateTime now)
    {
        var d = MainWindow.FmtHms(_r.TotalSeconds(now));
        if (d != DurationText) { DurationText = d; Raise(nameof(DurationText)); }

        var n = _r.Sessions.Count;
        var last = _r.LastActivityTime;
        var when = last.Date == DateTime.Today ? $"今天 {last:HH:mm}"
                 : last.Date == DateTime.Today.AddDays(-1) ? $"昨天 {last:HH:mm}"
                 : $"{last:MM-dd HH:mm}";
        var meta = n == 0 ? $"创建于 {(_r.CreatedTime.Date == DateTime.Today ? "今天" : _r.CreatedTime.ToString("MM-dd"))}"
                          : $"{n} 段 · 最近 {when}";
        if (meta != MetaText) { MetaText = meta; Raise(nameof(MetaText)); }

        var run = IsRunning;
        if (_lastRun != run) { _lastRun = run; Raise(nameof(IsRunning)); }
    }

    private bool _lastRun;
}

/// <summary>截图展示项:小尺寸解码的缩略图 + 全路径(点击打开原图)。</summary>
public sealed class ScreenshotVm
{
    public string Id { get; }
    public string FullPath { get; }
    public ImageSource? Thumb { get; }
    public string Tip { get; }

    public ScreenshotVm(Screenshot s, string fullPath)
    {
        Id = s.Id;
        FullPath = fullPath;
        Tip = $"截图 · {s.CreatedTime:MM-dd HH:mm} · 点击查看";
        try
        {
            if (File.Exists(fullPath))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad; // 立即解码并释放文件句柄
                bi.DecodePixelWidth = 160;                  // 缩略图按需解码,大图不占内存
                bi.UriSource = new Uri(fullPath);
                bi.EndInit();
                bi.Freeze();
                Thumb = bi;
            }
        }
        catch { /* 文件损坏时仅不显示缩略图 */ }
    }
}
