using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeDot.Core;

public sealed class RecordStore : IDisposable
{
    public static readonly string DefaultDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeDot");

    public string DataDir { get; }

    private readonly string RecordsPath;
    private readonly string ConfigPath;
    private readonly string BackupPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<WorkRecord> _records = new();
    private readonly Action? _onChanged;
    private System.Threading.Timer? _debounce;
    private readonly object _lock = new();

    /// <summary>数据或配置发生变化(含防抖落盘完成)后回调,UI 借此刷新。</summary>
    public event Action? Changed;

    public IReadOnlyList<WorkRecord> Records { get { lock (_lock) return _records.ToList(); } }

    public RecordStore(string? dataDir = null, Action? onChanged = null)
    {
        DataDir = dataDir ?? DefaultDataDir;
        RecordsPath = Path.Combine(DataDir, "records.jsonl");
        ConfigPath = Path.Combine(DataDir, "config.json");
        BackupPath = Path.Combine(DataDir, "records.jsonl.bak");
        MetaPath = Path.Combine(DataDir, "records.meta.json");
        _onChanged = onChanged;
        Load();
    }

    // ---------- 持久化 ----------

    private bool _mutated;      // 本次会话是否发生过变更
    private bool _fromBackup;   // 当前数据是否来自备份(避免无变更时用残缺主文件覆盖好备份)
    private readonly string MetaPath;

    /// <summary>逐行解析一个 JSONL 文件,返回有效记录;损坏行记入 dropped.log 并跳过。</summary>
    private List<WorkRecord> ParseFile(string path)
    {
        var list = new List<WorkRecord>();
        var dropped = new List<string>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var s = line.Trim();
            if (s.Length == 0) continue;
            try
            {
                var r = JsonSerializer.Deserialize<WorkRecord>(s, JsonOpts);
                if (r == null) continue;
                r.Project ??= "";
                r.User ??= "";
                r.Tags ??= new List<string>();
                r.Screenshots ??= new List<Screenshot>();
                // Note 现在是自由文字段落:旧版单行备注原样保留,不再拆分为标签
                r.Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
                r.Sessions ??= new List<Session>();
                // 起止时间解析失败的段是坏数据:会以 MinValue 算出天文时长,直接丢弃
                r.Sessions.RemoveAll(x => x.StartTime == DateTime.MinValue || (!x.Running && x.EndTime == DateTime.MinValue && x.End is not null));
                if (r.Project.Length > 0 || r.User.Length > 0 || r.Sessions.Count > 0)
                    list.Add(r);
                else
                    dropped.Add(s);
            }
            catch (Exception) { dropped.Add(s); } // 任意形态的坏行都不中断整体加载
        }
        if (dropped.Count > 0)
        {
            try { File.AppendAllText(Path.Combine(DataDir, "dropped.log"),
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} 解析 {path} 丢弃 {dropped.Count} 行 ---\n" +
                string.Join("\n", dropped) + "\n"); } catch { }
        }
        return list;
    }

    /// <summary>读取上次成功落盘的记录数;无 meta(旧版本/首次)返回 -1 表示不启用备份比对。</summary>
    private int ReadSavedCount()
    {
        try
        {
            if (File.Exists(MetaPath) &&
                JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(MetaPath, Encoding.UTF8))
                is { } d && d.TryGetValue("count", out var n))
                return n;
        }
        catch { }
        return -1;
    }

    private void WriteSavedCount(int n)
    {
        try { File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Dictionary<string, int> { ["count"] = n }), Encoding.UTF8); } catch { }
    }

    private void Load()
    {
        Directory.CreateDirectory(DataDir);
        var mainOk = File.Exists(RecordsPath);
        var main = mainOk ? ParseFile(RecordsPath) : new List<WorkRecord>();
        var savedCount = ReadSavedCount();
        // 判据:主文件有效行数 < 上次成功落盘的行数 ⇒ 主文件被外部截断/损坏。
        // 不能直接拿 bak 比行数:删除记录后主文件合法地比 bak 少一行,会误回滚。
        if (mainOk && savedCount >= 0 && main.Count < savedCount && File.Exists(BackupPath))
        {
            var bak = ParseFile(BackupPath);
            if (bak.Count > main.Count)
            {
                _records.AddRange(bak);
                _fromBackup = true;
                return;
            }
        }
        _records.AddRange(main);
        if (!mainOk && File.Exists(BackupPath))
        {
            _records.AddRange(ParseFile(BackupPath));
            _fromBackup = true;
        }
    }

    /// <summary>立即原子落盘:先写 .tmp 再替换,.bak 保留上一版。任何一次写失败都不破坏原文件。</summary>
    public void SaveNow()
    {
        lock (_lock)
        {
            if (_fromBackup && !_mutated) return; // 数据未变更时不覆盖完好备份
            Directory.CreateDirectory(DataDir);
            var tmp = RecordsPath + ".tmp";
            var sb = new StringBuilder();
            foreach (var r in _records)
                sb.AppendLine(JsonSerializer.Serialize(r, JsonOpts));
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(RecordsPath)) File.Replace(tmp, RecordsPath, BackupPath);
            else File.Move(tmp, RecordsPath);
            WriteSavedCount(_records.Count); // 落盘成功后记录基准,供下次启动比对损坏
            _fromBackup = false;
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            if (_debounce == null)
                _debounce = new System.Threading.Timer(_ =>
                {
                    try { SaveNow(); } catch { /* 磁盘异常不致崩溃 */ }
                    lock (_lock) { _debounce?.Dispose(); _debounce = null; }
                    Changed?.Invoke();
                }, null, 400, Timeout.Infinite);
            else
                _debounce.Change(400, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        try { SaveNow(); } catch { }
        lock (_lock) { _debounce?.Dispose(); _debounce = null; }
    }

    // ---------- 记录操作 ----------

    public WorkRecord Add(string project, string user)
    {
        var r = new WorkRecord
        {
            Project = project.Trim(),
            User = user.Trim(),
            Created = Now(),
        };
        lock (_lock) { _records.Add(r); _mutated = true; }
        ScheduleSave();
        return r;
    }

    /// <summary>开始/切换计时:同一时刻全库仅一条进行中的段;在别条上开始会自动停止当前段。</summary>
    public bool ToggleStart(string id)
    {
        WorkRecord? target = null, runningOwner = null;
        lock (_lock)
        {
            foreach (var r in _records)
            {
                if (r.Id == id) target = r;
                if (r.RunningSession != null) runningOwner = r;
            }
            if (target == null) return false;

            if (runningOwner == target)
            {
                var s = target.RunningSession!;
                if (DateTime.Now - s.StartTime < TimeSpan.FromSeconds(1))
                    target.Sessions.Remove(s); // 极短段视为误触,直接丢弃,不产生空段
                else
                    s.End = Now();
            }
            else
            {
                runningOwner?.RunningSession?.Let(x => x.End = Now());
                target.Sessions.Add(new Session { Start = Now() });
            }
            _mutated = true;
        }
        ScheduleSave();
        return true;
    }

    /// <summary>更新自由文字备注(可多行)。</summary>
    public bool UpdateNote(string id, string note)
    {
        var v = note ?? "";
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            if (r == null || r.Note == v) return false;
            r.Note = v;
            _mutated = true;
        }
        ScheduleSave();
        return true;
    }

    // ---------- 截图 ----------

    /// <summary>截图文件根目录:每条记录一个子目录(以记录 Id 命名)。</summary>
    public string ShotsDir => Path.Combine(DataDir, "screenshots");

    /// <summary>追加一张截图:PNG 写入该记录的截图目录,记录里只存引用。</summary>
    public Screenshot? AddScreenshot(string id, byte[] png)
    {
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            if (r == null) return null;
            var shot = new Screenshot { File = "", Created = Now() };
            shot.File = shot.Id + ".png";
            var dir = Path.Combine(ShotsDir, r.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, shot.File), png);
            r.Screenshots.Add(shot);
            _mutated = true;
            ScheduleSave();
            return shot;
        }
    }

    /// <summary>删除一张截图:移除引用并删除文件(目录空了顺手移除)。</summary>
    public bool DeleteScreenshot(string id, string shotId)
    {
        string? path = null;
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            var shot = r?.Screenshots.FirstOrDefault(s => s.Id == shotId);
            if (r == null || shot == null) return false;
            r.Screenshots.Remove(shot);
            _mutated = true;
            path = Path.Combine(ShotsDir, r.Id, shot.File);
        }
        ScheduleSave();
        try
        {
            File.Delete(path);
            var d = Path.GetDirectoryName(path);
            if (d != null && Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0)
                Directory.Delete(d);
        }
        catch { /* 文件缺失不阻断 */ }
        return true;
    }

    /// <summary>取某条记录截图的完整磁盘路径(缩略图加载用)。</summary>
    public string ShotPath(WorkRecord r, Screenshot s) => Path.Combine(ShotsDir, r.Id, s.File);

    /// <summary>标签建议:输入历史(最近在前)优先,再按记录活跃度补充现有标签;排除已拥有的。</summary>
    public List<string> SuggestTags(IEnumerable<string> exclude)
    {
        var ex = exclude.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        lock (_lock)
        {
            foreach (var t in Config.TagHistory)
                if (!ex.Contains(t) && !result.Contains(t)) result.Add(t);
            foreach (var r in _records.OrderByDescending(r => r.LastActivityTime))
                foreach (var t in r.Tags)
                    if (!ex.Contains(t) && !result.Contains(t)) result.Add(t);
        }
        return result.Take(30).ToList();
    }

    /// <summary>添加一个短语标签(去空白、去重),并前移标签历史(最近在前)。</summary>
    public bool AddTag(string id, string tag)
    {
        var t = (tag ?? "").Trim();
        if (t.Length == 0) return false;
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            if (r == null || r.Tags.Contains(t, StringComparer.Ordinal)) return false;
            r.Tags.Add(t);
            _mutated = true;
            Config.TagHistory.RemoveAll(x => x == t);
            Config.TagHistory.Insert(0, t);
            if (Config.TagHistory.Count > 50) Config.TagHistory.RemoveRange(50, Config.TagHistory.Count - 50);
        }
        ScheduleSave();
        try { SaveConfig(); } catch { /* 历史写失败不影响记录 */ }
        return true;
    }

    /// <summary>删除一个短语标签。</summary>
    public bool RemoveTag(string id, string tag)
    {
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            if (r == null || !r.Tags.Remove(tag)) return false;
            _mutated = true;
        }
        ScheduleSave();
        return true;
    }

    /// <summary>删除记录:连同其截图目录一起清理。</summary>
    public bool Delete(string id)
    {
        string? shotDir = null;
        lock (_lock)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            if (r == null) return false;
            _records.Remove(r);
            _mutated = true;
            shotDir = Path.Combine(ShotsDir, r.Id);
        }
        ScheduleSave();
        try { if (Directory.Exists(shotDir)) Directory.Delete(shotDir, true); } catch { }
        return true;
    }

    // ---------- 查询 ----------

    /// <summary>关键词过滤:子串匹配项目/用户/备注/标签/各日期;支持 今天|昨天|today|yesterday|本周|week。</summary>
    public IEnumerable<WorkRecord> Search(string query)
    {
        var q = (query ?? "").Trim();
        IEnumerable<WorkRecord> rs = Records;
        if (q.Length == 0) return rs;

        foreach (var token in q.Split(new[] { ' ', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token;
            DateTime? dayLo = null;
            DateTime? dayHi = null;
            var today = DateTime.Today;
            if (t is "今天" or "today" or "今日") { dayLo = today; dayHi = today.AddDays(1); }
            else if (t is "昨天" or "yesterday" or "昨日") { dayLo = today.AddDays(-1); dayHi = today; }
            else if (t is "本周" or "week" or "这周") { dayLo = today.AddDays(-(int)today.DayOfWeek + (today.DayOfWeek == DayOfWeek.Sunday ? -6 : 1)); dayHi = today.AddDays(1); }
            else if (t.Length == 8 && uint.TryParse(t, out var ymd) && t.StartsWith("20"))
            {
                var yy = (int)(ymd / 10000); var mm = (int)(ymd / 100 % 100); var dd = (int)(ymd % 100);
                if (mm is >= 1 and <= 12 && dd is >= 1 and <= 31) { try { dayLo = new DateTime(yy, mm, dd); dayHi = dayLo.Value.AddDays(1); } catch { } }
            }

            // 日期形态 token 与子串命中取并集:纯 8 位数字也可能是项目号(如 20240115),不能被日期语义独占
            rs = rs.Where(r => r.Haystack().Contains(t, StringComparison.OrdinalIgnoreCase)
                || (dayLo != null && (r.Sessions.Any(s => s.StartTime < dayHi && (s.Running ? DateTime.Now : s.EndTime) > dayLo)
                                   || (r.CreatedTime >= dayLo && r.CreatedTime < dayHi))));
        }
        return rs;
    }

    /// <summary>排序:进行中优先,其余按最近活动倒序。</summary>
    public IEnumerable<WorkRecord> Sorted()
    {
        return Records.OrderByDescending(r => r.RunningSession != null)
                      .ThenByDescending(r => r.LastActivityTime);
    }

    public double TodaySeconds() =>
        Records.Sum(r => r.SecondsInDay(DateTime.Today, DateTime.Today.AddDays(1)));

    public WorkRecord? RunningRecord => Records.FirstOrDefault(r => r.RunningSession != null);

    // ---------- 配置 ----------

    public AppConfig Config { get; private set; } = new();

    public void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8), JsonOpts) ?? new AppConfig();
        }
        catch { Config = new AppConfig(); }
    }

    public void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, JsonOpts), Encoding.UTF8);
            if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
            else File.Move(tmp, ConfigPath);
        }
        catch { /* 配置写失败不影响记录 */ }
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
}

internal static class Ext
{
    public static void Let<T>(this T? x, Action<T> f) where T : class { if (x != null) f(x); }
}
