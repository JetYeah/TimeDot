using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeDot.Core;

/// <summary>一条快捷回复(点击即复制到剪贴板):文本条目或图片条目。</summary>
public sealed class QuickReply
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = ""; // 文本原文;图片条目为标题(文件名/粘贴时间),供搜索与展示
    public string Image { get; set; } = ""; // 图片文件名(存于 quickreply-images/ 下),空即文本条目
    public string Hash { get; set; } = "";  // 图片内容 SHA1,按内容去重(重复粘贴不重复入库)
    public string Created { get; set; } = "";

    public bool IsImage => !string.IsNullOrWhiteSpace(Image);
}

/// <summary>
/// 快捷回复仓库:独立于工时记录,存于 DataDir/quickreplies.json。
/// 量级小(几十条),直接整文件同步读写,不需要 JSONL 追加与防抖。
/// </summary>
public sealed class QuickReplyStore
{
    private readonly string _path;
    private readonly string _imageDir;
    private readonly List<QuickReply> _items = new();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public QuickReplyStore(string? dataDir = null)
    {
        var dir = dataDir ?? RecordStore.DefaultDataDir;
        _path = Path.Combine(dir, "quickreplies.json");
        _imageDir = Path.Combine(dir, "quickreply-images");
        Load();
    }

    /// <summary>图片条目的存储目录(按需创建)。</summary>
    public string ImageDir => _imageDir;

    /// <summary>图片条目的完整文件路径;非图片条目返回 null。</summary>
    public string? ImagePath(QuickReply q) => q.IsImage ? Path.Combine(_imageDir, q.Image) : null;

    public IReadOnlyList<QuickReply> Items { get { lock (_lock) return _items.ToList(); } }

    /// <summary>追加一条文本(去空白;全量去重:同文已存在时返回 null 不重复入库)。</summary>
    public QuickReply? Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        lock (_lock)
        {
            if (_items.Any(x => x.Text == text)) return null;
            var q = new QuickReply { Text = text, Created = DateTime.Now.ToString("o") };
            _items.Insert(0, q); // 新的在前
            Save();
            return q;
        }
    }

    /// <summary>追加一条图片:字节落盘到 quickreply-images/,按内容 SHA1 去重(同图已存在返回 null)。
    /// 写盘失败(只读目录等)返回 null 不入库,保证条目与文件一致。</summary>
    public QuickReply? AddImage(string caption, byte[] data, string ext = ".png")
    {
        if (data.Length == 0) return null;
        caption = (caption ?? "").Trim();
        ext = NormalizeExt(ext);
        var hash = Convert.ToHexString(SHA1.HashData(data));
        lock (_lock)
        {
            if (_items.Any(x => x.Hash == hash)) return null; // 同图已存在不入库(重复粘贴/重选同一张)
            var q = new QuickReply
            {
                Text = caption.Length > 0 ? caption : $"图片 {DateTime.Now:MM-dd HH:mm}",
                Image = $"img-{hash[..12]}{ext}",
                Hash = hash,
                Created = DateTime.Now.ToString("o"),
            };
            try
            {
                Directory.CreateDirectory(_imageDir);
                File.WriteAllBytes(Path.Combine(_imageDir, q.Image), data);
            }
            catch { return null; }
            _items.Insert(0, q);
            Save();
            return q;
        }
    }

    private static string NormalizeExt(string ext)
    {
        ext = (ext ?? "").ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" ? ext : ".png";
    }

    /// <summary>按 Id 删除;图片条目连带删除文件;不存在返回 false。</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var q = _items.FirstOrDefault(x => x.Id == id);
            if (q == null) return false;
            _items.Remove(q);
            if (q.IsImage)
            {
                try { File.Delete(Path.Combine(_imageDir, q.Image)); } catch { /* 文件缺失不影响删条目 */ }
            }
            Save();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<QuickReply>>(File.ReadAllText(_path), JsonOpts);
            if (list == null) return;
            _items.AddRange(list.Where(x => !string.IsNullOrWhiteSpace(x.Text) || x.IsImage));
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
