# TimeDot — 克制风格的桌面工时悬浮球

Windows 常驻悬浮球,快速记录「项目号 + 用户」的工时。球体由 WebView2 渲染,面板为深色毛玻璃质感 + 青色单强调色,无多余装饰。

## 功能

- **悬浮球**:WebView2 渲染的 emotion-ball 球体(眼部表情、彩带特效),无边框透明置顶小窗,可拖到任意位置
- **情绪反馈**:球对操作有情绪反应 —— 新增记录/收到截图时开心,计时中专注,删除确认时紧张,片刻后自动回落
- **边缘吸附**:拖到屏幕边缘松手即吸附成一条 10px 小条;点击小条唤回悬浮球;空闲 60 秒自动吸附到最近边缘(托盘可关)
- **快速录入**:点球展开面板 → 项目号 + 用户 → 回车/「＋ 记录」即生成一条记录
- **固定面板**:面板 pin 按钮固定后,失焦 / Esc / 点球都不收起;再点一次或显式收起时解锁
- **计时**:每条记录**行首**按钮,点击记开始时间,再点记结束时间;多次点击产生多段,总时长累加。同一时刻全库只有一条在计时,开始新条自动停止旧条
- **标签**:行首「标签」按钮弹出气泡,输入新标签或从历史标签点选,用于归类与搜索
- **截图**:行内相机按钮弹全屏框选层截图(球与面板自动隐藏,避免入镜;Esc/右键取消);焦点在某行内时 Ctrl+V 直接把剪贴板图片粘为该行截图;缩略图点击查看/删除
- **搜索**:多关键词空格分隔取交集,子串匹配项目 / 用户 / 备注 / 标签 / 日期;支持 `今天` `昨天` `本周`(及 `today` `yesterday` `week`)和紧凑日期 `20260819`
- **备注**:每条记录行内直接输入,实时保存
- **持久化**:`%APPDATA%\TimeDot\records.jsonl`(一行一条记录,人可读),原子写入 + `.bak` 备份;计时用绝对时间戳,重启/关机后继续累计;截图本体存 `screenshots\{记录Id}\`
- **托盘**:双击唤出悬浮球;菜单:显示 / 空闲自动收边 / 开机自启 / 退出

## 数据格式

`%APPDATA%\TimeDot\records.jsonl`,每行:

```json
{"Id":"...","Project":"P-1001","User":"张工","Note":"备注","Tags":["交付","现场"],
 "Created":"2026-08-19T23:22:53.120",
 "Sessions":[{"Start":"2026-08-19T23:23:14.352","End":"2026-08-19T23:23:18.801"}],
 "Screenshots":[{"Id":"...","File":"a1b2.png","Created":"2026-08-19T23:24:00.000"}]}
```

`Sessions` 内 `End` 为 `null` 表示该段进行中;时间戳毫秒精度(兼容旧的秒级格式)。截图 PNG 存于 `%APPDATA%\TimeDot\screenshots\{记录Id}\`,`jsonl` 中只存引用。

**防丢失机制**:每次落盘走 `.tmp` → `File.Replace` 原子替换,上一版保留为 `.bak`;`records.meta.json` 记录上次成功落盘的行数,启动时若主文件有效行数低于该基准(被外部截断/损坏)自动回退 `.bak`;解析失败的行写入 `dropped.log` 供人工恢复。关机/注销/进程退出有落盘钩子兜住防抖窗口。

## 构建与运行

需要 .NET 8 SDK。

```powershell
dotnet build TimeDot.sln -c Release
# 运行
TimeDot\bin\Release\net8.0-windows\TimeDot.exe
# 数据层回归测试(42 项)
dotnet run --project TimeDot.Tests -c Release
```

单文件发布(Web 前端已内嵌为资源,磁盘没有 `Web` 目录时读内嵌资源,exe 独立可运行):

```powershell
dotnet publish TimeDot -c Release -r win-x64 -p:PublishSingleFile=true
```

## 项目结构

| 路径 | 说明 |
|---|---|
| `TimeDot.Core/Models.cs` | WorkRecord / Session / Screenshot / AppConfig 模型,时长与跨天切分计算 |
| `TimeDot.Core/RecordStore.cs` | JSONL 存储:防抖原子写、备份、损坏行容错、计时切换、标签、截图、搜索 |
| `TimeDot/MainWindow.xaml(.cs)` | 窗口几何 / 拖拽 / 吸附 / 展开 / Web 资源拦截 / 球外点击穿透 |
| `TimeDot/MainWindow.vm.cs` | 行视图模型 + 面板事件处理(计时 / 标签气泡 / 截图 / 粘贴) |
| `TimeDot/SnipWindow.cs` | 全屏框选截图层 |
| `TimeDot/Web/` | emotion-ball 前端(ball.html + JS),磁盘优先、内嵌资源兜底 |
| `TimeDot/App.xaml.cs` | 单实例、托盘、开机自启(HKCU Run)、全局异常兜底 |
| `TimeDot.Tests/Program.cs` | 数据层回归测试 |

## 设计语言

球体视觉由 emotion-ball 前端在 WebView2 中呈现:球体造型、眼部设计、彩带特效与情绪表情,计时中表情切换为专注态。面板为 WPF 半透明毛玻璃(细噪点 + 顶部冷光 sheen + 玻璃描边),青色单强调色。

## 渲染与开销

- 球体动画全部在 WebView2 渲染进程(JS)内完成,WPF 侧仅保留呼吸等轻量动画,展开面板期间暂停
- 收起面板后窗口立即缩回球大小;球可见时每 40ms 检查光标位置,处于「方形窗口内、圆形球体外」时对 WebView2 的 hwnd 设 `WS_EX_TRANSPARENT` 让点击穿透(标志未变化时零系统调用)
- 记录列表启用 UI 虚拟化(VSP + Recycling),每秒刷新仅在面板展开时进行,长期积累大量记录也不卡
- 空闲时周期性裁剪工作集

## 设计取舍

- **点击穿透**:球窗是方形、球是圆的,方形四角会挡住下方应用的点击(隐形空气墙)。收起即缩窗回球大小;可见时以光标位置动态切换 WebView2 的点击穿透,球的交互不受影响
- **误触保护**:不足 1 秒的计时段视为误触直接丢弃,不产生空段
- **单计时**:同一时刻只允许一条记录计时(开始新条自动停旧条),语义干净
- **删错保护**:删除需连点两次确认,3 秒未复点自动撤销
- **日期歧义**:纯 8 位数字既可能是项目号也可能是紧凑日期,搜索时取两者并集而非被日期语义独占

## 致谢与许可

本项目悬浮球前端(`TimeDot/Web/` 目录:`ball.html`、`ball.js`、`engine.js`、`rings.js`、`emotions.js`)源自开源项目 **[emotion-ball](https://github.com/sam70361/emotion-ball)**(作者 [sam70361](https://github.com/sam70361)),按其许可证要求进行了修改与集成,特此致谢。

该部分代码及其视觉形象(球体造型、眼部设计、彩带特效等)遵循原项目的 **"Learning & Research Only License"(仅供学习研究许可)**,完整文本见 [`TimeDot/Web/LICENSE`](TimeDot/Web/LICENSE) 与 [`TimeDot/Web/NOTICE.md`](TimeDot/Web/NOTICE.md)。

> ⚠️ **非商业限制**:该许可证允许学习、研究、修改及非商业场景的分享,但**禁止任何商业用途**(售卖、付费授权、集成到商业产品或服务等)。由于本项目集成了该前端,TimeDot 整体同样不得用于商业用途。
