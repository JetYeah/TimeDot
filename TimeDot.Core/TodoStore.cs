using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeDot.Core;

/// <summary>一条待办事项。BlockedBy 为前置依赖(NextTodo 思想:A 完成后才能做本条)。</summary>
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string Created { get; set; } = "";
    public string? DoneAt { get; set; }
    /// <summary>前置待办的 Id 列表:全部完成后本条才"可做";不存在的前置(已删)视为通过。</summary>
    public List<string> BlockedBy { get; set; } = new();
}

/// <summary>删除快照:撤销删除时恢复条目本体与双向依赖(谁被我挡过)。</summary>
public sealed record TodoRemovalSnapshot(TodoItem Item, List<string> WasBlockedBy);

/// <summary>
/// 待办仓库:独立于工时记录,存于 DataDir/todos.json。
/// 与快捷回复同套路:量级小,整文件同步读写。
/// 先后关系(NextTodo):事项间可设前置依赖组成有向无环图——
/// 前置未全完成的为"等待中",全部完成后自动变"可做";成环拒绝;删除连带拆线。
/// </summary>
public sealed class TodoStore
{
    private readonly string _path;
    private readonly List<TodoItem> _items = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public TodoStore(string? dataDir = null)
    {
        _path = Path.Combine(dataDir ?? RecordStore.DefaultDataDir, "todos.json");
        Load();
    }

    /// <summary>展示排序:未完成在前(可做在前、等待中在后,均新的在前),已完成置尾(最近完成在前)——
    /// 打开面板先看到"接下来能做的事"(NextTodo 的"接下来"视图)。</summary>
    public IReadOnlyList<TodoItem> Items
    {
        get
        {
            lock (_lock)
            {
                var open = _items.Where(t => !t.Done).ToList();
                var ready = open.Where(t => IsReady(t, _items))
                                .OrderByDescending(t => t.Created ?? "");
                var waiting = open.Where(t => !IsReady(t, _items))
                                  .OrderByDescending(t => t.Created ?? "");
                var done = _items.Where(t => t.Done)
                                 .OrderByDescending(t => t.DoneAt ?? "");
                return ready.Concat(waiting).Concat(done).ToList();
            }
        }
    }

    /// <summary>未完成的原始条目(不排序,供批量状态计算)。</summary>
    public IReadOnlyList<TodoItem> RawItems
    {
        get { lock (_lock) return _items.ToList(); }
    }

    // ---------- 依赖(先后关系) ----------

    /// <summary>可做 = 未完成且所有前置都已完成(不存在的前置视为已通过)。</summary>
    public static bool IsReady(TodoItem t, IReadOnlyList<TodoItem> all)
    {
        if (t.Done) return false;
        if (t.BlockedBy.Count == 0) return true;
        var map = all.ToDictionary(x => x.Id);
        return t.BlockedBy.All(b => !map.TryGetValue(b, out var p) || p.Done);
    }

    /// <summary>未完成前置数(等待提示"等待 N 项前置")。</summary>
    public static int OpenBlockers(TodoItem t, IReadOnlyList<TodoItem> all)
    {
        if (t.Done) return 0;
        var map = all.ToDictionary(x => x.Id);
        return t.BlockedBy.Count(b => map.TryGetValue(b, out var p) && !p.Done);
    }

    /// <summary>未完成前置的文本列表(等待条目展示"在等谁",顺序与 BlockedBy 一致)。</summary>
    public static List<string> OpenBlockerTexts(TodoItem t, IReadOnlyList<TodoItem> all)
    {
        var map = all.ToDictionary(x => x.Id);
        return t.BlockedBy.Where(b => map.TryGetValue(b, out var p) && !p.Done)
                          .Select(b => map[b].Text)
                          .ToList();
    }

    /// <summary>完成 blocker 后能解锁几项:被它挡住、且其余前置也已全部通过的未完成项数。
    /// 已完成的 blocker 挡不了任何人,一律返回 0(不显示"解锁 N")。</summary>
    public static int UnlockCount(string blockerId, IReadOnlyList<TodoItem> all)
    {
        var map = all.ToDictionary(x => x.Id);
        if (map.TryGetValue(blockerId, out var self) && self.Done) return 0;
        return all.Count(x => !x.Done
                               && x.BlockedBy.Contains(blockerId)
                               && x.BlockedBy.Where(b => b != blockerId)
                                             .All(b => !map.TryGetValue(b, out var p) || p.Done));
    }

    /// <summary>加一条依赖:blocker 是 dependent 的前置(blocker 完成后才能做 dependent)。
    /// 成环或指向自身返回 false;已存在则幂等返回 true。</summary>
    public bool SetDependency(string blockerId, string dependentId)
    {
        lock (_lock)
        {
            if (blockerId == dependentId) return false;
            var blocker = _items.FirstOrDefault(x => x.Id == blockerId);
            var dependent = _items.FirstOrDefault(x => x.Id == dependentId);
            if (blocker == null || dependent == null) return false;
            if (dependent.BlockedBy.Contains(blockerId)) return true; // 幂等

            // 环检测:从 blocker 沿其 BlockedBy 向上走,若能到达 dependent,加这条边就成环
            var seen = new HashSet<string>();
            var stack = new Stack<string>(blocker.BlockedBy);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == dependentId) return false; // 成环
                if (!seen.Add(cur)) continue;
                if (_items.FirstOrDefault(x => x.Id == cur) is { } p)
                    foreach (var up in p.BlockedBy) stack.Push(up);
            }

            dependent.BlockedBy.Add(blockerId);
            Save();
            return true;
        }
    }

    /// <summary>拆除依赖:不再是 dependent 的前置。不存在返回 false(幂等安全)。</summary>
    public bool ClearDependency(string blockerId, string dependentId)
    {
        lock (_lock)
        {
            var dependent = _items.FirstOrDefault(x => x.Id == dependentId);
            if (dependent == null) return false;
            var ok = dependent.BlockedBy.Remove(blockerId);
            if (ok) Save();
            return ok;
        }
    }

    // ---------- 增删改 ----------

    /// <summary>添加一条待办(去空白,空文本不入库)。</summary>
    public TodoItem? Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        lock (_lock)
        {
            var t = new TodoItem { Text = text, Created = DateTime.Now.ToString("o") };
            _items.Add(t);
            Save();
            return t;
        }
    }

    /// <summary>批量添加(多行粘贴拆条:一段话一次入库)。返回成功添加的条数。</summary>
    public int AddMany(IEnumerable<string> lines)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var raw in lines)
            {
                var text = raw.Trim();
                if (text.Length == 0) continue;
                _items.Add(new TodoItem { Text = text, Created = DateTime.Now.ToString("o") });
                added++;
            }
            if (added > 0) Save();
        }
        return added;
    }

    /// <summary>翻转完成状态(完成记时间,重新打开清空时间)。
    /// 等待中的项也允许直接完成(NextTodo 跳步:现实里常有;重开后其后续自动回到等待中)。</summary>
    public bool Toggle(string id)
    {
        lock (_lock)
        {
            var t = _items.FirstOrDefault(x => x.Id == id);
            if (t == null) return false;
            t.Done = !t.Done;
            t.DoneAt = t.Done ? DateTime.Now.ToString("o") : null;
            Save();
            return true;
        }
    }

    /// <summary>按 Id 删除;不存在返回 false。</summary>
    public bool Remove(string id) => RemoveWithUndo(id, out _) != null;

    /// <summary>删除并返回撤销快照:删除条目的克隆 + 被它挡过的项 Id 列表
    /// (NextTodo 规则:删除事项时连着它的依赖一起拆——引用双向都要能恢复)。</summary>
    public TodoRemovalSnapshot? RemoveWithUndo(string id, out List<string> wasBlockedBy)
    {
        wasBlockedBy = new List<string>();
        lock (_lock)
        {
            var t = _items.FirstOrDefault(x => x.Id == id);
            if (t == null) return null;
            var snapshot = new TodoRemovalSnapshot(
                new TodoItem { Id = t.Id, Text = t.Text, Done = t.Done, Created = t.Created, DoneAt = t.DoneAt, BlockedBy = new List<string>(t.BlockedBy) },
                wasBlockedBy);

            // 连带拆线:所有被本条挡着的项,把本条从它们的前置里移除并记下 Id
            foreach (var other in _items.Where(x => x.Id != id && x.BlockedBy.Contains(id)))
            {
                other.BlockedBy.Remove(id);
                wasBlockedBy.Add(other.Id);
            }
            _items.Remove(t);
            Save();
            return snapshot;
        }
    }

    /// <summary>恢复一次删除:本体按原 Id 回库(保持他人对它的引用语义),
    /// 前置引用只留仍存在的项,同时把"被它挡过的项"的引用接回来。重复恢复幂等返回 null。</summary>
    public TodoItem? Restore(TodoRemovalSnapshot s)
    {
        lock (_lock)
        {
            if (s == null || _items.Any(x => x.Id == s.Item.Id)) return null;
            var t = new TodoItem
            {
                Id = s.Item.Id,
                Text = s.Item.Text,
                Done = s.Item.Done,
                Created = s.Item.Created,
                DoneAt = s.Item.DoneAt,
                BlockedBy = s.Item.BlockedBy.Where(b => _items.Any(x => x.Id == b)).Distinct().ToList(),
            };
            _items.Add(t);
            foreach (var dep in s.WasBlockedBy)
                if (_items.FirstOrDefault(x => x.Id == dep) is { } d && !d.BlockedBy.Contains(t.Id))
                    d.BlockedBy.Add(t.Id);
            Save();
            return t;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(_path), JsonOpts);
            if (list == null) return;
            _items.AddRange(list.Where(x => !string.IsNullOrWhiteSpace(x.Text)));
            // 防御:自引用/重复引用剔除(历史数据容错)
            foreach (var t in _items) t.BlockedBy = t.BlockedBy.Where(b => b != t.Id).Distinct().ToList();
        }
        catch
        {
            // 文件损坏时静默从空开始,不阻断启动(下次保存会覆盖坏文件)
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // tmp→Replace 原子替换:直接覆盖写在进程崩溃/断电时会留截断的 json,
            // 下次启动整文件解析失败(静默清空)。与 RecordStore 同纪律。
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, JsonOpts));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch
        {
            // 只读目录等场景写不进不影响内存态
        }
    }
}

