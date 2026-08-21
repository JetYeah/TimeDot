using TimeDot.Core;

namespace TimeDot.Tests;

internal static class Program
{
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
        if (!cond) _fail++;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "TimeDotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Main()
    {
        // ---------- 新增与持久化 ----------
        var dir = TempDir();
        var store = new RecordStore(dir);
        var a = store.Add("P-1024", "张三");
        Thread.Sleep(600); // 防抖落盘
        Check(File.Exists(Path.Combine(dir, "records.jsonl")), "add 后防抖生成 jsonl 文件");
        store.Dispose(); // Dispose 即全量落盘
        var store2 = new RecordStore(dir);
        Check(store2.Records.Count == 1 && store2.Records[0].Project == "P-1024"
              && store2.Records[0].User == "张三", "重启后记录恢复(含中文)");

        // ---------- 计时:开始/停止/多段累加 ----------
        var s2 = store2;
        s2.ToggleStart(a.Id);
        Check(s2.RunningRecord?.Id == a.Id, "点击开始 → 有进行中会话");
        Thread.Sleep(1200);
        s2.ToggleStart(a.Id);
        Check(s2.RunningRecord == null && s2.Records[0].Sessions.Count == 1
              && s2.Records[0].Sessions[0].End != null, "再次点击 → 记录结束时间");
        Thread.Sleep(100);
        s2.ToggleStart(a.Id);
        Thread.Sleep(1200);
        s2.ToggleStart(a.Id);
        var rec = s2.Records[0];
        Check(rec.Sessions.Count == 2, "多次点击 → 多段会话");
        Check(rec.TotalSeconds(DateTime.Now) >= 2.2, "总时长为多段累加(≥2.2s)");

        // ---------- 误触保护 ----------
        var c = s2.Add("P-x", "u");
        s2.ToggleStart(c.Id);
        s2.ToggleStart(c.Id); // 1 秒内立刻再点 → 丢弃空段
        var cr = s2.Records.First(r => r.Id == c.Id);
        Check(cr.Sessions.Count == 0 && s2.RunningRecord?.Id != c.Id, "极短段视为误触被丢弃");

        // ---------- 全库单一计时 ----------
        var b = s2.Add("P-2048", "李四");
        s2.ToggleStart(b.Id);
        Thread.Sleep(1100);
        s2.ToggleStart(a.Id);
        Check(s2.Records.First(r => r.Id == b.Id).RunningSession == null
           && s2.RunningRecord?.Id == a.Id, "在别条开始会自动停止上一条");
        Thread.Sleep(1100); // 让 a 持续一段时间后保持运行,模拟中途重启

        // ---------- 重启后继续计时(绝对时间) ----------
        s2.Dispose();
        var s3 = new RecordStore(dir);
        Check(s3.RunningRecord?.Id == a.Id, "重启后进行中的计时仍在");
        Thread.Sleep(300);
        Check(s3.RunningRecord!.TotalSeconds(DateTime.Now) >= 1.4, "重启后时长基于绝对时间继续累计");
        s3.ToggleStart(a.Id);
        s3.Dispose();

        // ---------- 标签 / 删除 ----------
        var s4 = new RecordStore(dir);
        s4.AddTag(a.Id, "调试");
        s4.AddTag(a.Id, "OLED 驱动");
        s4.AddTag(a.Id, "调试"); // 重复添加应被拒绝
        s4.AddTag(a.Id, "   "); // 纯空白应被拒绝
        s4.Dispose();
        var s5 = new RecordStore(dir);
        var ar5 = s5.Records.First(r => r.Id == a.Id);
        Check(ar5.Tags.Count == 2 && ar5.Tags.Contains("调试") && ar5.Tags.Contains("OLED 驱动"),
              "标签写入并恢复(重复/空白被拒)");
        var cnt = s5.Records.Count;
        s5.Delete(c.Id);
        s5.Dispose();
        var s6 = new RecordStore(dir);
        Check(s6.Records.Count == cnt - 1, "删除生效");

        // ---------- 搜索 ----------
        var s7 = new RecordStore(dir);
        Check(s7.Search("P-1024").Any(r => r.Id == a.Id), "按项目号搜索");
        Check(s7.Search("李四").Any(r => r.Id == b.Id), "按用户搜索");
        Check(s7.Search("OLED").Any(r => r.Id == a.Id), "按标签搜索");
        Check(s7.Search("2048").Count() == 1, "子串命中唯一记录");
        Check(s7.Search("今天").Any(r => r.Id == a.Id), "关键词「今天」命中今日会话");
        var old = new WorkRecord { Project = "OLD-1", User = "old", Created = "2020-01-01T09:00:00",
            Sessions = new() { new() { Start = "2020-01-01T09:00:00", End = "2020-01-01T10:00:00" } } };
        File.AppendAllText(Path.Combine(dir, "records.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(old) + "\n");
        var s8 = new RecordStore(dir);
        Check(s8.Search("20200101").Any(r => r.Project == "OLD-1"), "紧凑日期 20200101 命中");
        Check(s8.Search("2020-01-01").Any(r => r.Project == "OLD-1"), "ISO 日期命中");
        Check(!s8.Search("今天").Any(r => r.Project == "OLD-1"), "「今天」不命中 2020 年记录");
        Check(!s8.Search("不存在的关键字").Any(), "无结果搜索返回空");

        // ---------- 标签删除 / 自由备注 / 截图 ----------
        s8.RemoveTag(a.Id, "调试");
        s8.Dispose();
        var s8b = new RecordStore(dir);
        Check(!s8b.Records.First(r => r.Id == a.Id).Tags.Contains("调试"), "标签删除跨重启生效");

        var dirM = TempDir();
        File.WriteAllLines(Path.Combine(dirM, "records.jsonl"), new[] {
            """{"Id":"m1","Project":"P-m","User":"u","Note":"调试 OLED 驱动","Created":"2026-08-01T09:00:00.000","Sessions":[]}""",
        });
        var sm = new RecordStore(dirM);
        var mr = sm.Records.First(r => r.Id == "m1");
        Check(mr.Note == "调试 OLED 驱动" && mr.Tags.Count == 0, "旧版单行备注原样保留为文字段落");
        sm.UpdateNote("m1", "第一行\n第二行");
        sm.Dispose();
        var sm2 = new RecordStore(dirM);
        Check(sm2.Records.First(r => r.Id == "m1").Note == "第一行\n第二行", "多行备注写入并恢复");
        Check(sm2.Search("第二行").Any(r => r.Id == "m1"), "按备注文字搜索");

        // 1x1 PNG:验证截图文件写入、引用恢复与删除清理
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var shot = sm2.AddScreenshot("m1", png);
        Check(shot != null && File.Exists(Path.Combine(dirM, "screenshots", "m1", shot.File)),
              "截图文件写入记录专属目录");
        sm2.Dispose();
        var sm3 = new RecordStore(dirM);
        Check(sm3.Records.First(r => r.Id == "m1").Screenshots.Count == 1, "截图引用跨重启恢复");
        sm3.DeleteScreenshot("m1", sm3.Records.First(r => r.Id == "m1").Screenshots[0].Id);
        sm3.Dispose();
        var sm4 = new RecordStore(dirM);
        Check(sm4.Records.First(r => r.Id == "m1").Screenshots.Count == 0
              && !Directory.Exists(Path.Combine(dirM, "screenshots", "m1")), "删除截图连同空目录清理");
        sm4.Dispose();

        // ---------- 标签历史建议(最近在前,排除已有) ----------
        var dirH = TempDir();
        var sh = new RecordStore(dirH);
        var rh1 = sh.Add("P-h1", "u");
        var rh2 = sh.Add("P-h2", "u");
        sh.AddTag(rh1.Id, "调试");
        sh.AddTag(rh2.Id, "联调");
        sh.AddTag(rh1.Id, "调试"); // 重复添加不应在历史里出现两次
        sh.Dispose();
        var sh2 = new RecordStore(dirH);
        sh2.LoadConfig();
        var sug = sh2.SuggestTags(sh2.Records.First(r => r.Id == rh1.Id).Tags);
        Check(sug.Count == 1 && sug[0] == "联调", "标签建议:最近在前且排除该行已有标签");
        Check(sh2.Config.TagHistory.SequenceEqual(new[] { "联调", "调试" }), "标签历史跨重启持久且最近在前");
        sh2.Dispose();

        // ---------- 损坏行容错 / 备份 ----------
        File.AppendAllText(Path.Combine(dir, "records.jsonl"), "{ 这一行是坏数据\n");
        var s9 = new RecordStore(dir);
        Check(s9.Records.All(r => r.Project.Length > 0), "坏行被跳过不致崩溃");
        s9.SaveNow();
        Check(File.Exists(Path.Combine(dir, "records.jsonl.bak")), "原子写保留 .bak 备份");

        // ---------- 跨天切分(纯模型计算) ----------
        var m = new WorkRecord
        {
            Sessions = new() { new() { Start = "2026-08-18T23:00:00", End = "2026-08-19T01:00:00" } }
        };
        var today = m.SecondsInDay(new DateTime(2026, 8, 19), new DateTime(2026, 8, 20));
        Check(Math.Abs(today - 3600) < 0.01, "23:00→01:00 的会话只给今天记 1h");
        Check(Math.Abs(m.TotalSeconds(DateTime.Now) - 7200) < 0.01 || m.TotalSeconds(DateTime.Now) > 7200,
              "总时长为完整 2h(或因当前时间更晚而更大)");

        // ---------- 评审修复回归 ----------
        var dir2 = TempDir();
        // a) 日期形态项目号不被日期语义劫持(union 修复)
        File.WriteAllLines(Path.Combine(dir2, "records.jsonl"), new[] {
            """{"Id":"d1","Project":"20240115","User":"u1","Note":"","Created":"2026-08-01T09:00:00.000","Sessions":[]}""",
        });
        var sa = new RecordStore(dir2);
        Check(sa.Search("20240115").Any(r => r.Id == "d1"), "项目号恰为日期形态仍可按项目号搜到");
        sa.Dispose();

        // b) Sessions:null 的合法 JSON 行不炸启动,归一化为空表
        var dir3 = TempDir();
        File.WriteAllLines(Path.Combine(dir3, "records.jsonl"), new[] {
            """{"Id":"n1","Project":"P-n","User":"u","Note":null,"Created":"2026-08-01T09:00:00.000","Sessions":null}""",
        });
        var sb2 = new RecordStore(dir3);
        Check(sb2.Records.Count == 1 && sb2.Records[0].Sessions.Count == 0, "Sessions:null 行容错加载");
        sb2.Dispose();

        // c) 主文件损坏(有效行少于 meta 记录的落盘基准)→ 采用备份;未变更前 SaveNow 不覆盖备份
        var dir4 = TempDir();
        File.WriteAllLines(Path.Combine(dir4, "records.jsonl.bak"), new[] {
            """{"Id":"b1","Project":"P-b1","User":"u","Note":"","Created":"2026-08-01T09:00:00.000","Sessions":[]}""",
            """{"Id":"b2","Project":"P-b2","User":"u","Note":"","Created":"2026-08-01T10:00:00.000","Sessions":[]}""",
        });
        File.WriteAllText(Path.Combine(dir4, "records.meta.json"), """{"count":2}""");
        File.WriteAllText(Path.Combine(dir4, "records.jsonl"), "{ 只剩坏数据\n");
        var sc = new RecordStore(dir4);
        Check(sc.Records.Count == 2 && sc.Records.All(r => r.Id.StartsWith('b')), "主文件被截断时回退备份");
        sc.Dispose(); // 无变更,不应回写
        var mainAfter = File.ReadAllText(Path.Combine(dir4, "records.jsonl"));
        Check(mainAfter.Contains("坏数据") || mainAfter.Trim().Length == 0, "无变更时 SaveNow 不用残缺主文件覆盖");
        var sd = new RecordStore(dir4);
        sd.Add("P-new", "u");
        sd.Dispose();
        Check(File.ReadAllText(Path.Combine(dir4, "records.jsonl")).Contains("P-new")
           && File.ReadAllText(Path.Combine(dir4, "records.jsonl")).Contains("P-b1"), "真实变更后正常落盘(3 条)");

        // d) 正常删除不会被备份回滚误伤(删除后主文件行数 < bak 行数是合法状态)
        var se = new RecordStore(dir4);
        var cntBefore = se.Records.Count;
        se.Delete(se.Records.First(r => r.Project == "P-b1").Id);
        se.Dispose();
        var sf = new RecordStore(dir4);
        Check(sf.Records.Count == cntBefore - 1 && sf.Records.All(r => r.Project != "P-b1"), "删除跨重启持久生效");
        sf.Dispose();

        // ---------- 配置 ----------
        var s10 = new RecordStore(dir);
        s10.Config.BallX = 123; s10.Config.DockSide = "left";
        s10.SaveConfig();
        var s11 = new RecordStore(dir);
        s11.LoadConfig();
        Check(s11.Config.BallX == 123 && s11.Config.DockSide == "left", "配置往返");

        Console.WriteLine(_fail == 0 ? "\n全部通过" : $"\n{_fail} 项失败");
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }
}
