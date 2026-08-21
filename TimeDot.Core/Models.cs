using System.Text.Json.Serialization;

namespace TimeDot.Core;

/// <summary>一段计时区间。Start/End 均为本地时间 ISO 字符串(yyyy-MM-ddTHH:mm:ss),End 为 null 表示进行中。</summary>
public sealed class Session
{
    public string Start { get; set; } = "";
    public string? End { get; set; }

    [JsonIgnore]
    public DateTime StartTime => DateTime.TryParse(Start, out var t) ? t : DateTime.MinValue;

    [JsonIgnore]
    public DateTime EndTime => DateTime.TryParse(End ?? "", out var t) ? t : DateTime.MinValue;

    /// <summary>是否正在进行(尚未结束)。</summary>
    [JsonIgnore]
    public bool Running => End is null;

    /// <summary>该段截至 at 时刻的时长(秒)。进行中的段按 at 截断。</summary>
    public double ElapsedSeconds(DateTime at) => Math.Max(0, ((Running ? at : EndTime) - StartTime).TotalSeconds);
}

/// <summary>一条工时记录:项目号 + 用户 + 多段计时 + 备注。</summary>
public sealed class WorkRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Project { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>自由文字段落(可多行),与短语标签互补;旧版单行备注加载后原样保留在此。</summary>
    public string? Note { get; set; }
    /// <summary>短语标签列表:逐个添加/删除,可按标签检索。</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>截图列表:图片文件存于 DataDir/screenshots/{Id}/ 下,此处仅存引用。</summary>
    public List<Screenshot> Screenshots { get; set; } = new();
    public string Created { get; set; } = "";
    public List<Session> Sessions { get; set; } = new();

    [JsonIgnore]
    public DateTime CreatedTime => DateTime.TryParse(Created, out var t) ? t : DateTime.MinValue;

    [JsonIgnore]
    public Session? RunningSession => Sessions.FirstOrDefault(s => s.Running);

    [JsonIgnore]
    public DateTime LastActivityTime
    {
        get
        {
            var last = Sessions.Count == 0 ? DateTime.MinValue : Sessions.Max(s => s.Running ? DateTime.Now : s.EndTime);
            return last > CreatedTime ? last : CreatedTime;
        }
    }

    /// <summary>总时长(秒):已结束各段之和,加上进行中段的当前值。</summary>
    public double TotalSeconds(DateTime at) => Sessions.Sum(s => s.ElapsedSeconds(at));

    /// <summary>与 [dayStart, dayEnd) 重叠的时长(秒),用于"今日累计"。</summary>
    public double SecondsInDay(DateTime dayStart, DateTime dayEnd)
    {
        double sum = 0;
        foreach (var s in Sessions)
        {
            var a = s.StartTime;
            var b = s.Running ? DateTime.Now : s.EndTime;
            if (b <= dayStart || a >= dayEnd) continue;
            var lo = a < dayStart ? dayStart : a;
            var hi = b > dayEnd ? dayEnd : b;
            if (hi > lo) sum += (hi - lo).TotalSeconds;
        }
        return sum;
    }

    /// <summary>搜索用文本:项目号、用户、备注、标签、创建日期、各段日期(ISO 与紧凑两种形式)。</summary>
    public string Haystack()
    {
        var dates = new List<string> { Created };
        foreach (var s in Sessions)
        {
            dates.Add(s.Start);
            if (s.End != null) dates.Add(s.End);
        }
        var compact = string.Join("\n", dates.Select(d => d.Replace("-", "").Replace(":", "").Replace("T", "").Replace(" ", "")));
        return $"{Project}\n{User}\n{Note ?? ""}\n{string.Join("\n", Tags)}\n{string.Join("\n", dates)}\n{compact}";
    }
}

/// <summary>一条截图引用:图片本体是 DataDir/screenshots/{记录Id}/ 下的 PNG 文件。</summary>
public sealed class Screenshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>文件名(相对记录的截图目录)。</summary>
    public string File { get; set; } = "";
    public string Created { get; set; } = "";

    [JsonIgnore]
    public DateTime CreatedTime => DateTime.TryParse(Created, out var t) ? t : DateTime.MinValue;
}

/// <summary>窗口/吸附位置等应用配置。</summary>
public sealed class AppConfig
{
    public double BallX { get; set; } = -1;
    public double BallY { get; set; } = -1;
    /// <summary>吸附边:none/left/right/top/bottom;ball 自由态时为 none。</summary>
    public string DockSide { get; set; } = "none";
    /// <summary>沿边位置(左侧边为 Y,顶底边为 X)。</summary>
    public double DockOffset { get; set; } = 400;
    /// <summary>空闲多少秒后自动吸附(<=0 关闭)。</summary>
    public int AutoDockSeconds { get; set; } = 60;
    public bool StartWithWindows { get; set; } = false;
    /// <summary>标签输入历史(最近在前,最多 50 个),供添加标签时按最近优先建议。</summary>
    public List<string> TagHistory { get; set; } = new();
}
