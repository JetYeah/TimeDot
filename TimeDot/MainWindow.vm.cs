using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    // 快捷回复:展示副本 + 搜索词(实时过滤)
    private readonly ObservableCollection<QuickReplyVm> _quickVms = new();
    private string _quickQuery = "";

    // 待办:展示副本(与 TodoStore 排序同步) + 筛选词(全部/当日/当周/待完成/已完成)
    private readonly ObservableCollection<TodoVm> _todoVms = new();
    private string _todoFilter = "All";

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

    // ---------- 时长手动修改(停止计时后点时长弹出) ----------

    private RecordViewModel? _timeEditVm;

    /// <summary>点击行内时长：非进行中才可改，弹出气泡预填当前值(显示格式可直接回车微调)。</summary>
    private void DurationClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not RecordViewModel vm) return;
        if (vm.IsRunning) return; // 进行中不让改：先停止再改
        _timeEditVm = vm;
        TimeEditTitle.Text = $"修改总时长（当前 {vm.DurationText}）";
        TimeEditInput.Text = vm.DurationText;
        TimeEditPopup.PlacementTarget = fe;
        TimeEditPopup.HorizontalOffset = fe.ActualWidth - 244; // 右缘对齐时长右缘,避免溢出屏幕右边界
        TimeEditPopup.IsOpen = true;
        TimeEditInput.SelectAll();
        Dispatcher.BeginInvoke(() => TimeEditInput.Focus(), DispatcherPriority.Input);
    }

    /// <summary>回车提交/Esc 取消；解析失败轻闪输入框不关气泡。</summary>
    private void TimeEditInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseTimeEditPopup(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_timeEditVm == null) { CloseTimeEditPopup(); return; }
        if (!RecordStore.TryParseDuration(TimeEditInput.Text, out var seconds))
        {
            var a = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(150)) { AutoReverse = true };
            TimeEditInput.BeginAnimation(OpacityProperty, a);
            return;
        }
        var id = _timeEditVm.Id;
        CloseTimeEditPopup();
        if (_store.AdjustTotal(id, seconds))
        {
            RefreshList(id); // 行内时长/段数/托盘今日累计随之刷新
            UpdateTrayText();
        }
    }

    private void CloseTimeEditPopup()
    {
        TimeEditPopup.IsOpen = false;
        _timeEditVm = null;
    }

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

    // ---------- 快捷回复(双击球唤出,点击即复制) ----------

    /// <summary>与 QuickReplyStore 就地同步展示副本,避免整表重建打断反馈动画。</summary>
    private void RefreshQuickList()
    {
        var src = _quick.Items;
        for (var i = _quickVms.Count - 1; i >= 0; i--)
            if (src.All(q => q.Id != _quickVms[i].Id)) _quickVms.RemoveAt(i);
        for (var i = 0; i < src.Count; i++)
        {
            var existing = _quickVms.FirstOrDefault(v => v.Id == src[i].Id);
            if (existing == null) _quickVms.Insert(i, new QuickReplyVm(src[i].Id, src[i].Text, _quick.ImagePath(src[i])));
            else
            {
                var cur = _quickVms.IndexOf(existing);
                if (cur != i) _quickVms.Move(cur, i);
            }
        }
        QuickList.ItemsSource = _quickVms;
        RefreshQuickView();
        // 面板开着时新增/删除条目 → 窗口高度跟随内容重排,避免条目被窗口底边裁掉
        // (搜索过滤不重排:打字时窗口不跳动,列表区保持原尺寸出滚动条)
        if (_mode == Mode.Quick) LayoutQuick();
    }

    /// <summary>按搜索词刷新过滤与空态提示。</summary>
    private void RefreshQuickView()
    {
        var view = CollectionViewSource.GetDefaultView(_quickVms);
        view.Filter = string.IsNullOrEmpty(_quickQuery)
            ? null
            : o => o is QuickReplyVm q && q.Text.Contains(_quickQuery, StringComparison.OrdinalIgnoreCase);
        view.Refresh();

        if (_quickVms.Count == 0)
        {
            QuickEmptyHint.Text = "还没有快捷回复 — 上方输入后回车添加";
            QuickEmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            var shown = view.Cast<QuickReplyVm>().Count();
            QuickEmptyHint.Text = "无匹配的快捷回复";
            QuickEmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void QuickSearchChanged(object sender, TextChangedEventArgs e)
    {
        _quickQuery = QuickSearch.Text.Trim();
        RefreshQuickView();
    }

    /// <summary>搜索框回车:复制首条匹配(高频场景:打两个字母+回车即发送)。</summary>
    private void QuickSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (CollectionViewSource.GetDefaultView(_quickVms).Cast<QuickReplyVm>().FirstOrDefault() is not { } first) return;
        CopyQuick(first);
        FlashQuickHint(first.IsImage ? "已复制图片" : $"已复制：{TrimFor(first.Text, 18)}");
    }

    private void QuickAddClick(object sender, RoutedEventArgs e) => AddQuick();

    private void QuickInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddQuick(); e.Handled = true; return; }
        // Ctrl+V:剪贴板有图片时直接粘成图片条目(截图工具复制的位图/资源管理器复制的图片文件)
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && AddQuickImageFromClipboard())
            e.Handled = true;
    }

    private void AddQuick()
    {
        var text = QuickInput.Text.Trim();
        if (text.Length == 0)
        {
            // 空输入:轻闪提示,不入库
            var a = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(150)) { AutoReverse = true };
            QuickInput.BeginAnimation(OpacityProperty, a);
            return;
        }
        var dup = _quick.Add(text) == null; // 同文已存在不入库
        QuickInput.Clear();
        RefreshQuickList();
        FlashQuickHint(dup ? "已存在，未重复添加" : "已添加");
        Keyboard.Focus(QuickInput);
    }

    /// <summary>图片按钮:文件选择器挑一张图入库(保留原格式字节,内容去重)。</summary>
    private void QuickImageClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片添加为快捷回复",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var caption = Path.GetFileNameWithoutExtension(dlg.FileName);
            var ext = Path.GetExtension(dlg.FileName);
            var dup = _quick.AddImage(caption, bytes, ext) == null;
            RefreshQuickList();
            FlashQuickHint(dup ? "同一张图片已存在" : "已添加图片");
        }
        catch { FlashQuickHint("图片读取失败"); }
    }

    /// <summary>输入框 Ctrl+V:剪贴板有图则粘成图片条目;无图返回 false 让默认文本粘贴继续。</summary>
    private bool AddQuickImageFromClipboard()
    {
        var pngs = CollectClipboardImages();
        if (pngs.Count == 0) return false;
        var caption = $"图片 {DateTime.Now:MM-dd HH:mm}";
        var dup = _quick.AddImage(caption, pngs[0]) == null; // 同图内容去重(重复粘贴不重复入库)
        RefreshQuickList();
        FlashQuickHint(dup ? "同一张图片已存在" : "已添加图片");
        return true;
    }

    /// <summary>条目点击:复制全文或图片,条目内短暂显示"已复制 ✓"(图片条目用面板提示反馈)。</summary>
    private void QuickCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not QuickReplyVm vm) return;
        CopyQuick(vm);
        if (vm.IsImage) { FlashQuickHint("已复制图片"); return; }
        if (sender is Control c && c.Template?.FindName("qt", c) is TextBlock t)
        {
            var orig = vm.Text;
            t.Text = "已复制 ✓";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.9) };
            timer.Tick += (_, _) => { timer.Stop(); t.Text = orig; };
            timer.Start();
        }
    }

    private void QuickRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not QuickReplyVm vm) return;
        if (_quick.Remove(vm.Id)) RefreshQuickList();
    }

    /// <summary>统一复制入口:图片条目复制图(位图+文件双格式),文本条目复制文本。</summary>
    private void CopyQuick(QuickReplyVm vm)
    {
        if (vm.IsImage) { CopyQuickImage(vm); return; }
        CopyQuickText(vm);
    }

    /// <summary>图片条目复制:同时放位图与文件路径——聊天输入框可直接粘图,资源管理器/支持文件粘贴的 IM 拿文件。</summary>
    private void CopyQuickImage(QuickReplyVm vm)
    {
        try
        {
            if (vm.ImagePath == null || !File.Exists(vm.ImagePath)) { FlashQuickHint("图片文件缺失"); return; }
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // 立即解码并释放文件句柄(文件还要留着)
            bi.UriSource = new Uri(vm.ImagePath);
            bi.EndInit();
            bi.Freeze();
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { vm.ImagePath });
            data.SetImage(bi);
            Clipboard.SetDataObject(data, true);
            SendEmotion("30"); // 开心(收到复制)
        }
        catch { /* 剪贴板被占用不致崩溃 */ }
    }

    private void CopyQuickText(QuickReplyVm vm)
    {
        try { Clipboard.SetText(vm.Text); SendEmotion("30"); } // 开心(收到复制)
        catch { /* 剪贴板被占用不致崩溃 */ }
    }

    /// <summary>空态提示短暂改写文案后恢复(反馈通道,平时隐藏)。</summary>
    private void FlashQuickHint(string msg)
    {
        QuickEmptyHint.Text = msg;
        QuickEmptyHint.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) => { timer.Stop(); RefreshQuickView(); };
        timer.Start();
    }

    private static string TrimFor(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ---------- 待办(环形菜单唤出) ----------

    /// <summary>与 TodoStore 就地同步展示副本(排序:未完成在前),随后套用当前筛选。</summary>
    private void RefreshTodoList()
    {
        var src = _todo.Items;
        for (var i = _todoVms.Count - 1; i >= 0; i--)
            if (src.All(t => t.Id != _todoVms[i].Id)) _todoVms.RemoveAt(i);
        for (var i = 0; i < src.Count; i++)
        {
            var existing = _todoVms.FirstOrDefault(v => v.Id == src[i].Id);
            if (existing == null) _todoVms.Insert(i, new TodoVm(src[i].Id, src[i].Text, src[i].Done, src[i].Created));
            else
            {
                var cur = _todoVms.IndexOf(existing);
                if (cur != i) { _todoVms.Move(cur, i); cur = i; }
                // 完成状态/文本变化:就地替换以刷新模板触发器(VM 只读)——Move 分支同样
                // 要查:勾选/重新打开的条目必然移位,只 Move 不替换会永远显示旧状态
                if (_todoVms[cur].Done != src[i].Done || _todoVms[cur].Text != src[i].Text)
                    _todoVms[cur] = new TodoVm(src[i].Id, src[i].Text, src[i].Done, src[i].Created);
            }
        }
        TodoList.ItemsSource = _todoVms;
        ApplyTodoFilter();
        var open = src.Count(t => !t.Done);
        TodoCount.Text = open == 0 && src.Count > 0 ? "全部完成 ✓" : $"{open} 项待办";
        // 面板开着时新增/删除/勾选 → 窗口高度跟随内容重排,避免条目被窗口底边裁掉
        // (筛选切换不重排:窗口尺寸稳定,列表区保持原尺寸出滚动条)
        if (_mode == Mode.Todo) LayoutTodo();
    }

    /// <summary>筛选芯片点击:换词重过滤。</summary>
    private void TodoFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        _todoFilter = fe.Tag as string ?? "All";
        ApplyTodoFilter();
    }

    /// <summary>把当前筛选套到列表视图上,并区分“真空态”与“该筛选下无匹配”两种空提示。</summary>
    private void ApplyTodoFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_todoVms);
        view.Filter = _todoFilter switch
        {
            "Today" => o => o is TodoVm v && CreatedOn(v.Created) == DateTime.Today,
            "Week"  => o => o is TodoVm v && InThisWeek(v.Created),
            "Open"  => o => o is TodoVm v && !v.Done,
            "Done"  => o => o is TodoVm v && v.Done,
            _ => null,
        };
        view.Refresh();
        var shown = view.Cast<TodoVm>().Count();
        TodoEmptyHint.Text = _todoVms.Count == 0 ? "没有待办 — 添加一条开始"
            : shown == 0 ? "该筛选下无待办" : "";
        TodoEmptyHint.Visibility = shown == 0 || _todoVms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>解析 ISO 创建串为本地日期;失败(老数据/空串)返回 null。</summary>
    private static DateTime? CreatedOn(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.Date : null;

    /// <summary>当周:以周一为一周之始(周日归入前一周),含今天在内整周。</summary>
    private static bool InThisWeek(string? iso)
    {
        var d = CreatedOn(iso);
        if (d == null) return false;
        var today = DateTime.Today;
        var dow = (int)today.DayOfWeek; // 0=周日
        var weekStart = today.AddDays(dow == 0 ? -6 : 1 - dow);
        return d >= weekStart;
    }

    private void TodoAddClick(object sender, RoutedEventArgs e) => AddTodo();

    private void TodoInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddTodo(); e.Handled = true; }
    }

    private void AddTodo()
    {
        var text = TodoInput.Text.Trim();
        if (text.Length == 0)
        {
            // 空输入:轻闪提示,不入库
            var a = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(150)) { AutoReverse = true };
            TodoInput.BeginAnimation(OpacityProperty, a);
            return;
        }
        _todo.Add(text);
        TodoInput.Clear();
        RefreshTodoList();
        Keyboard.Focus(TodoInput);
    }

    /// <summary>整行点击:翻转完成状态(完成后置尾,已完成的重新打开归位)。</summary>
    private void TodoToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TodoVm vm) return;
        if (_todo.Toggle(vm.Id))
        {
            RefreshTodoList();
            if (_todo.Items.FirstOrDefault(t => t.Id == vm.Id)?.Done == true) SendEmotion("30"); // 开心(又完成一件)
        }
    }

    private void TodoRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TodoVm vm) return;
        if (_todo.Remove(vm.Id)) RefreshTodoList();
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
        // 剪贴板查询裹异常:剪贴板被其他进程独占时 Contains* 抛 COMException,
        // 不捕获会打断所有按键(含普通输入)
        var clipHasImage = false;
        try { clipHasImage = !Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()); }
        catch { /* 按无图处理,走默认粘贴 */ }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control
            && clipHasImage
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

    // 截图流程进行中:此刻主窗被主动藏起会触发 Deactivated,须防误判为用户点走而连锁收起面板
    private bool _shotBusy;

    /// <summary>相机按钮:藏起球和面板后弹全屏框选层,拖动选区即截图(Esc/右键取消)。</summary>
    private async void ShotClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RecordViewModel vm) return;
        _shotBusy = true;
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
        finally { _shotBusy = false; Visibility = Visibility.Visible; }
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
            if (!File.Exists(shot.FullPath))
            {
                LogShot("打开截图失败,文件不存在: " + shot.FullPath);
                return;
            }
            // 内置查看器:不依赖系统图片关联(本机"照片"应用注册损坏时
            // Process.Start 会抛"没有关联程序",原先空 catch 吞掉即点击无反应)
            new ShotViewer(shot.FullPath, shot.Tip) { Owner = this }.Show();
        }
        catch (Exception ex) { LogShot("打开截图异常: " + ex); }
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

    private string _shotsKey = " "; // 上次同步的引用序列(初始不等于任何合法值,强制首刷)

    public void SyncShots()
    {
        // 脏检查:引用序列(文件名)未变就跳过——RefreshList 每次全行 Rebind,
        // 搜索打字时若无条件重建,UI 线程会重复解码全部缩略图造成卡顿
        var key = string.Join('\n', _r.Screenshots.Select(x => x.File));
        if (key == _shotsKey) return;
        _shotsKey = key;
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

/// <summary>待办展示项(完成状态参与模板触发器;Created 供筛选,CreatedText 供显示)。</summary>
public sealed class TodoVm
{
    public string Id { get; }
    public string Text { get; }
    public bool Done { get; }
    public string Created { get; }
    public string CreatedText { get; }

    public TodoVm(string id, string text, bool done, string created)
    {
        Id = id; Text = text; Done = done; Created = created;
        CreatedText = FormatCreated(created);
    }

    /// <summary>人类友好格式:今天/昨天带时刻,同年只记月日,往年缩略年份。</summary>
    private static string FormatCreated(string? iso)
    {
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return "";
        var today = DateTime.Today;
        if (dt.Date == today) return $"今天 {dt:HH:mm}";
        if (dt.Date == today.AddDays(-1)) return $"昨天 {dt:HH:mm}";
        return dt.Year == today.Year ? dt.ToString("MM-dd HH:mm") : dt.ToString("yy-MM-dd");
    }
}

/// <summary>快捷回复展示项:文本条目或图片条目(缩略图小尺寸解码)。</summary>
public sealed class QuickReplyVm
{
    public string Id { get; }
    public string Text { get; }       // 文本原文或图片标题(搜索与图旁小字)
    public string? ImagePath { get; } // 图片条目:完整文件路径;文本条目为 null
    public ImageSource? Thumb { get; }
    public bool IsImage => ImagePath != null;

    public QuickReplyVm(string id, string text, string? imagePath = null)
    {
        Id = id;
        Text = text;
        ImagePath = imagePath;
        if (imagePath != null)
        {
            try
            {
                if (File.Exists(imagePath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad; // 立即解码并释放文件句柄
                    bi.DecodePixelHeight = 110;                 // 缩略图按需解码,大图不占内存(行高上限 52@2x)
                    bi.UriSource = new Uri(imagePath);
                    bi.EndInit();
                    bi.Freeze();
                    Thumb = bi;
                }
            }
            catch { /* 文件损坏时仅不显示缩略图 */ }
        }
    }
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
