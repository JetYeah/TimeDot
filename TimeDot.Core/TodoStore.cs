using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeDot.Core;

/// <summary>一条待办事项。</summary>
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string Created { get; set; } = "";
    public string? DoneAt { get; set; }
}

/// <summary>
/// 待办仓库:独立于工时记录,存于 DataDir/todos.json。
/// 与快捷回复同套路:量级小,整文件同步读写。
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

    /// <summary>未完成在前(新→旧),已完成置尾(最近完成在前):打开面板先看到要做的。</summary>
    public IReadOnlyList<TodoItem> Items
    {
        get
        {
            lock (_lock)
                return _items.OrderBy(t => t.Done) // false(未完成)在前
                    .ThenByDescending(t => t.Done ? (t.DoneAt ?? "") : (t.Created ?? ""))
                    .ToList();
        }
    }

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

    /// <summary>翻转完成状态(完成记时间,重新打开清空时间)。</summary>
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
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var t = _items.FirstOrDefault(x => x.Id == id);
            if (t == null) return false;
            _items.Remove(t);
            Save();
            return true;
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
