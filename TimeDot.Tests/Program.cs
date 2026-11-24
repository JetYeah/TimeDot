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
        Check(s7.Search("2048").Any(r => r.Id == b.Id), "子串命中项目号");
        // (原断言 .Count()==1 对运行时刻敏感:当天时间戳紧凑形态恰含 "2048"(如 20:48 分)
        //  时另一条记录也会命中,偶发 FAIL——改为存在性断言,保留子串匹配意图)
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

        // ---------- 快捷回复 ----------
        var qdir = TempDir();
        var qs = new QuickReplyStore(qdir);
        Check(qs.Items.Count == 0, "快捷回复初始为空");
        var q1 = qs.Add("  稍后回复您  ");
        Check(q1 != null && qs.Items[0].Text == "稍后回复您", "添加时去首尾空白");
        Check(qs.Add("稍后回复您") == null && qs.Items.Count == 1, "同文重复添加被拒绝");
        qs.Add("会议中,请留言");
        qs.Remove(q1!.Id);
        Check(qs.Items.Count == 1 && qs.Items[0].Text == "会议中,请留言", "按 Id 删除");
        Check(qs.Remove("不存在") == false, "删除不存在的 Id 返回 false");
        var qs2 = new QuickReplyStore(qdir);
        Check(qs2.Items.Count == 1 && qs2.Items[0].Text == "会议中,请留言", "重启后快捷回复恢复(含中文)");
        File.WriteAllText(Path.Combine(qdir, "quickreplies.json"), "{ 坏数据");
        var qs3 = new QuickReplyStore(qdir);
        Check(qs3.Items.Count == 0, "文件损坏时静默从空开始");

        // ---------- 快捷回复·图片条目 ----------
        var qidir = TempDir();
        var qi = new QuickReplyStore(qidir);
        var imgBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5 }; // 伪 PNG 字节,验存储/去重足够
        var qimg1 = qi.AddImage("  笑脸.png  ", imgBytes);
        Check(qimg1 != null && qimg1.IsImage && qimg1.Text == "笑脸.png", "图片入库:标题去首尾空白");
        Check(File.Exists(qi.ImagePath(qimg1!)), "图片字节落盘 quickreply-images/");
        Check(qi.AddImage("副本", imgBytes) == null, "同图内容去重被拒");
        var qimg2 = qi.AddImage("", imgBytes.Concat(new byte[] { 9 }).ToArray(), ".webp");
        Check(qimg2 != null && qimg2.Text.StartsWith("图片 ") && qimg2.Image.EndsWith(".webp"),
              "无标题自动生成;合法扩展名保留");
        Check(qi.AddImage("x", Array.Empty<byte>(), ".exe") == null, "空字节被拒");
        var imgPath = qi.ImagePath(qimg1!);
        qi.Remove(qimg1!.Id);
        Check(!File.Exists(imgPath), "删图片条目连带删文件");
        var qi2 = new QuickReplyStore(qidir);
        Check(qi2.Items.Count == 1 && qi2.Items[0].Hash == qimg2!.Hash && qi2.Items[0].IsImage,
              "图片条目跨重启持久");
        qi2.Remove(qimg2.Id);
        Check(new QuickReplyStore(qidir).Items.Count == 0, "图片条目可全部清空");

        // ---------- 待办 ----------
        var tdir = TempDir();
        var ts = new TodoStore(tdir);
        Check(ts.Items.Count == 0, "待办初始为空");
        var t1 = ts.Add("  写周报  ");
        Check(t1 != null && ts.Items[0].Text == "写周报", "待办添加去空白");
        ts.Add("修复登录 bug");
        ts.Add("准备评审材料");
        Check(ts.Items.Count(t => !t.Done) == 3, "三项未完成");
        ts.Toggle(t1!.Id);
        Check(ts.Items[0].Text == "准备评审材料" && ts.Items[^1].Text == "写周报"
              && ts.Items[^1].Done, "完成后置尾且记 DoneAt");
        ts.Toggle(t1.Id);
        Check(ts.Items.Any(t => t.Text == "写周报" && !t.Done), "重新打开后归位未完成区");
        ts.Remove(t1.Id);
        Check(ts.Items.Count == 2 && ts.Items.All(t => t.Id != t1.Id), "按 Id 删除待办");
        var t2 = ts.Add("记录创建时间");
        Check(t2 != null && DateTime.TryParse(t2.Created, System.Globalization.CultureInfo.InvariantCulture,
              System.Globalization.DateTimeStyles.None, out var created)
              && Math.Abs((created - DateTime.Now).TotalMinutes) < 1, "添加即自动记录创建时间");
        var ts2 = new TodoStore(tdir);
        Check(ts2.Items.Count == 3 && ts2.Items[0].Done == false, "重启后待办恢复(含排序)");
        File.WriteAllText(Path.Combine(tdir, "todos.json"), "{ 坏数据");
        var ts3 = new TodoStore(tdir);
        Check(ts3.Items.Count == 0, "待办文件损坏时静默从空开始");

        // ---------- 待办先后依赖(NextTodo 思想) ----------
        var ddir = TempDir();
        var ds = new TodoStore(ddir);
        var tA = ds.Add("设计稿")!;
        var tB = ds.Add("前端开发")!;
        var tC = ds.Add("联调")!;
        Check(ds.SetDependency(tA.Id, tB.Id), "加依赖:A 是 B 的前置");
        Check(ds.SetDependency(tA.Id, tB.Id), "重复加依赖幂等成功");
        Check(!ds.SetDependency(tA.Id, tA.Id), "指向自身的依赖被拒");
        Check(ds.RawItems.First(x => x.Id == tB.Id).BlockedBy.Contains(tA.Id), "依赖已持久化在 BlockedBy");

        // 环检测:A→B 后再 B→A 必须拒绝;更长链 C→A…也拒绝
        Check(!ds.SetDependency(tB.Id, tA.Id), "成环依赖被拒(2 节点)");
        ds.SetDependency(tB.Id, tC.Id); // B 是 C 的前置:A→B→C
        Check(!ds.SetDependency(tC.Id, tB.Id), "成环依赖被拒(3 节点)");
        Check(!ds.SetDependency(tC.Id, tA.Id), "成环依赖被拒(长链)");

        // 派生状态:可做/等待中;解锁计数
        var all = ds.RawItems;
        Check(TodoStore.IsReady(tA, all) && !TodoStore.IsReady(tB, all) && !TodoStore.IsReady(tC, all),
              "可做=A,等待=B/C");
        Check(TodoStore.OpenBlockers(tB, all) == 1 && TodoStore.OpenBlockers(tC, all) == 1, "等待中各压 1 项前置");
        var btexts = TodoStore.OpenBlockerTexts(tB, all);
        Check(btexts.Count == 1 && btexts[0] == "设计稿", "等待项可显示在等谁(前置文本)");
        Check(TodoStore.UnlockCount(tA.Id, all) == 1, "完成 A 只解锁 B(C 仍等 B)");
        Check(ds.Items[0].Id == tA.Id && ds.Items[^1].Done == false, "排序:可做在前,等待在后");

        // 完成 A → B 变可做,C 仍等待(完成一件,下一件出现)
        ds.Toggle(tA.Id);
        all = ds.RawItems;
        Check(TodoStore.IsReady(tB, all) && !TodoStore.IsReady(tC, all), "A 完成后 B 解锁,C 仍等待");
        Check(TodoStore.UnlockCount(tA.Id, all) == 0, "完成后不再解锁任何项");
        // 跳步:等待中的 C 也可直接完成(现实常有)
        ds.Toggle(tC.Id);
        Check(ds.Items.First(t => t.Id == tC.Id).Done, "等待中允许跳步直接完成");
        // 重开已完成项:其后续可能因此变回等待中(NextTodo 规则)
        ds.Toggle(tA.Id); // A 重新打开
        all = ds.RawItems;
        Check(!TodoStore.IsReady(tB, all) && TodoStore.OpenBlockers(tB, all) == 1,
              "A 重开后其后续 B 变回等待中");
        // 还原:A 完成(跳过重新等待 C 的过程,C 已是完成态)
        ds.Toggle(tA.Id);
        all = ds.RawItems;
        Check(TodoStore.IsReady(tB, all), "A 再次完成后 B 又解锁");

        // 拆除依赖
        Check(ds.ClearDependency(tB.Id, tC.Id), "拆除 B→C 依赖");
        Check(!ds.RawItems.First(x => x.Id == tC.Id).BlockedBy.Contains(tB.Id), "拆除后 C 不再被挡");
        Check(!ds.ClearDependency(tB.Id, tC.Id), "重复拆除返回 false");

        // 删除连带拆线 + 撤销恢复(含双向依赖)
        ds = new TodoStore(TempDir());
        var tP = ds.Add("P")!;
        var tQ = ds.Add("Q")!;
        ds.SetDependency(tP.Id, tQ.Id);
        var snap = ds.RemoveWithUndo(tP.Id, out var blocked);
        Check(snap != null && blocked.Count == 1 && blocked[0] == tQ.Id, "删除返回快照与被挡名单");
        var raw = ds.RawItems;
        Check(TodoStore.IsReady(raw.First(x => x.Id == tQ.Id), raw), "前置被删后 Q 视为可做");
        Check(!ds.RawItems.First(x => x.Id == tQ.Id).BlockedBy.Contains(tP.Id), "删除连带清理引用");
        var restored = ds.Restore(snap!);
        raw = ds.RawItems;
        Check(restored != null && restored.Id == tP.Id
              && raw.First(x => x.Id == tQ.Id).BlockedBy.Contains(tP.Id)
              && !TodoStore.IsReady(raw.First(x => x.Id == tQ.Id), raw), "撤销恢复:原 Id 回库且双向依赖接回");
        Check(ds.Restore(snap!) == null, "重复恢复幂等返回 null");

        // 重启往返:BlockedBy 序列化不丢
        var dts = new TodoStore(ddir);
        Check(dts.RawItems.First(x => x.Id == tA.Id).Done
              && dts.RawItems.First(x => x.Id == tB.Id).BlockedBy.Contains(tA.Id)
              && dts.RawItems.First(x => x.Id == tC.Id).BlockedBy.Count == 0,
              "重启后依赖与状态往返不丢");

        // 多行批量入库:拆分/空行跳过
        var many = new TodoStore(TempDir());
        Check(many.AddMany(new[] { "  ", "任务一", "", "任务二", "  任务三  " }) == 3, "多行拆分入库(空行跳过)");
        Check(many.Items.Count == 3 && many.Items.Any(t => t.Text == "任务三"), "批量入库去空白");

        // ---------- 手动修改总时长 ----------
        Check(RecordStore.TryParseDuration("1:30", out var du1) && Math.Abs(du1 - 90) < 0.01, "解析 1:30 = 90s");
        Check(RecordStore.TryParseDuration("1:30:00", out var du2) && Math.Abs(du2 - 5400) < 0.01, "解析 1:30:00 = 5400s");
        Check(RecordStore.TryParseDuration("90m", out var du3) && Math.Abs(du3 - 5400) < 0.01, "解析 90m");
        Check(RecordStore.TryParseDuration("1.5h", out var du4) && Math.Abs(du4 - 5400) < 0.01, "解析 1.5h");
        Check(RecordStore.TryParseDuration("45s", out var du5) && Math.Abs(du5 - 45) < 0.01, "解析 45s");
        Check(RecordStore.TryParseDuration("90", out var du6) && Math.Abs(du6 - 5400) < 0.01, "纯数字按分钟");
        Check(!RecordStore.TryParseDuration("abc", out _), "非法输入被拒");
        Check(!RecordStore.TryParseDuration("-5", out _), "负数被拒");
        Check(!RecordStore.TryParseDuration("1:99", out _), "秒位越界被拒");
        Check(!RecordStore.TryParseDuration("", out _), "空串被拒");

        var dirAd = TempDir();
        var sad = new RecordStore(dirAd);
        var rad = sad.Add("P-ad", "u");
        sad.ToggleStart(rad.Id); Thread.Sleep(1100); sad.ToggleStart(rad.Id); // 1 段 ~1s
        Check(sad.AdjustTotal(rad.Id, 3600)
           && Math.Abs(sad.Records[0].TotalSeconds(DateTime.Now) - 3600) < 1, "手动改为整 1h");
        sad.ToggleStart(rad.Id);
        Check(!sad.AdjustTotal(rad.Id, 7200), "进行中不允许修改");
        sad.ToggleStart(rad.Id);
        sad.ToggleStart(rad.Id); Thread.Sleep(1100); sad.ToggleStart(rad.Id); // 再来一段 ~1s
        sad.AdjustTotal(rad.Id, 1); // 目标低于其他段(1h):最后一段压到 0
        var radj = sad.Records.First(r => r.Id == rad.Id);
        Check(Math.Abs(radj.Sessions[^1].ElapsedSeconds(DateTime.Now)) < 0.01
           && Math.Abs(radj.TotalSeconds(DateTime.Now) - 3600) < 1, "目标过小时最后一段压到 0,总量保住其他段");
        var rempty = sad.Add("P-empty", "u");
        Check(sad.AdjustTotal(rempty.Id, 1800)
           && Math.Abs(sad.Records.First(r => r.Id == rempty.Id).TotalSeconds(DateTime.Now) - 1800) < 1,
           "无段记录补录 30m");
        Check(!sad.AdjustTotal("不存在", 60) && !sad.AdjustTotal(rad.Id, 0), "非法 id / 零时长被拒");
        sad.Dispose();
        var sad2 = new RecordStore(dirAd);
        Check(Math.Abs(sad2.Records.First(r => r.Id == rad.Id).TotalSeconds(DateTime.Now) - 3600) < 1
           && Math.Abs(sad2.Records.First(r => r.Id == rempty.Id).TotalSeconds(DateTime.Now) - 1800) < 1,
           "手动修改的时长跨重启持久");
        sad2.Dispose();

        Console.WriteLine(_fail == 0 ? "\n全部通过" : $"\n{_fail} 项失败");
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }
}
