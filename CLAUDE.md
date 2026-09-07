# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

TimeDot — Windows 常驻工时悬浮球（WPF + WebView2）。悬浮球承载表情动画（emotion-ball），点击展开面板记录「项目号 + 用户」工时；含环形菜单、边缘吸附、快捷回复、待办、截图附件。

## 常用命令

`dotnet` 不在 PATH，必须用完整路径（本机为用户级安装；机器无管理员权限，勿尝试 winget 机器级安装）：

```powershell
# 构建整个解决方案
& "$env:USERPROFILE\.dotnet\dotnet.exe" build TimeDot.sln -c Release

# 运行（构建产物路径）
TimeDot\bin\Release\net8.0-windows\TimeDot.exe

# 数据层回归测试（自研 Check 断言，非 xunit；输出 PASS/FAIL 与失败计数）
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project TimeDot.Tests -c Release
```

调试用启动参数：`--expanded`（跳过动画直接展开面板）、`--auto-repro=N`（自动循环展开/收起 N 次，复现动画问题）、`--ghost-diag`（开启残影诊断：收起后的像素对账/取证/实验级联仅此开关下运行）。

诊断日志：`%APPDATA%\TimeDot\error.log`（全局异常）、exe 同目录 `web.log`（WebView2 导航与 JS 消息）。数据目录 `%APPDATA%\TimeDot\`（records.jsonl / config.json / quickreplies.json / todos.json / screenshots/）。

## 架构

### 三个项目

| 项目 | 目标框架 | 职责 |
|---|---|---|
| `TimeDot.Core` | net8.0 纯库 | 数据模型 + 三个存储（无 UI 依赖，测试只测这层） |
| `TimeDot` | net8.0-windows | WPF 宿主窗口 + WebView2 球体 + WinForms 托盘 |
| `TimeDot.Tests` | net8.0 | 控制台回归测试（Program.cs 自研断言） |

### 数据层（TimeDot.Core）

三个互相独立的 Store，各自持久化，全内存态 + 锁保护：

- **`RecordStore`**（核心，records.jsonl）：一行一条 WorkRecord 的 JSONL。
  - **防抖原子写**：变更后 400ms 防抖落盘；`.tmp` → `File.Replace` 原子替换，旧版留 `.bak`；`records.meta.json` 记录上次成功落盘行数，启动时主文件有效行数低于基准则回退 `.bak`（判据是 meta 而非 bak 行数，避免删除记录后误回滚）。损坏行写入 `dropped.log` 跳过，不中断加载。
  - **单一计时不变量**：全库同一时刻只有一条记录的 Session 进行中（`End == null`）；在别条开始计时会自动停掉当前条；不足 1 秒的段视为误触直接丢弃。修改计时逻辑时必须维持这些语义。
  - `TryParseDuration`/`AdjustTotal`：手动改总时长（挪最后一段的 End；无段时补一段"刚刚结束"）。
  - 搜索：多 token AND；支持关键词 `今天/昨天/本周/today/yesterday/week` 与紧凑日期 `20260819`；8 位纯数字同时保留子串匹配（可能是项目号）。
- **`QuickReplyStore`**（quickreplies.json）：文本/图片快捷回复，图片按内容 SHA1 去重，存 `quickreply-images/`。
- **`TodoStore`**（todos.json）：待办。两者量级小，整文件同步读写，无防抖。

`App.xaml.cs` 在关机/注销/进程退出时 `SaveNow()` 兜住防抖窗口。

### UI 层（TimeDot）

`MainWindow` 拆成两个 partial 文件，按此分工新增代码：

- **`MainWindow.xaml.cs`** — 窗口本体：几何常量（DIP）、`Mode` 状态机（`Free / Expanded / Docked / Quick / Todo / Radial` 六态互斥，窗口矩形随模式整体变化）、拖拽/边缘吸附（DockSnapDist 内松手吸附成 10px 小条）、动画（DispatcherTimer + 手写插值）、Win32 互操作。两处非显然的 Win32 治理：
  - **球外空气墙**（`UpdateWebPass`，40ms 轮询）：WebView2 是方形 hwnd，球是圆的；光标在方形内圆外时给 WebView2 加 `WS_EX_TRANSPARENT` 让点击穿透到下方应用。吸附态全程穿透。
  - **残影清理**（`DispelDwmGhost`/`PostCollapseGuard`）：用 `UpdateLayeredWindow` 强制 DWM 重合成。根目录大量 `ghost_*.ps1`、`ui_*.ps1` 是当时调查这些残影/动画问题的一次性调试脚本，非产品代码，勿在其上续写功能。
- **`MainWindow.vm.cs`** — 面板内容：`RecordViewModel`（INotifyPropertyChanged，行内备注/标签/时长/截图的 UI 状态）与全部面板事件处理（记录增删、计时按钮、标签弹层、时长编辑、快捷回复、待办）。

球体交互不经过 WPF 命中测试：JS 侧捕获 pointer 事件后 `postMessage` 给 C#（`dragstart/dragmove/dragend/enter/leave/move/dblclick`），C# 用 `GetCursorPos` 绝对坐标驱动窗口移动；点击 vs 拖拽由 JS 位移阈值判定。

### 球体前端（TimeDot/Web，WebView2）

单页 `ball.html` + 四个分层 JS（自底向上）：`rings.js`（纯几何数据：25 组表情眼环 + 身体形状）、`emotions.js`（表情配置数据）、`ball.js`（SVG 渲染）、`engine.js`（rAF 状态机 + 对外 SDK `EmotionBall.create()`）。宿主页面只依赖 engine 的 SDK；表情用两位字符串 ID（'02' 待机、'00' 睡眠、'22' 计时中）。

C# ↔ JS 桥接（`InitBallWeb`）：

- 导航到虚拟域 `https://app.local/ball.html`，`WebResourceRequested` 拦截后走 `OpenWebAsset`：**磁盘 Web 目录优先（开发态改完刷新即生效），内嵌资源兜底**——csproj 里 `Web\**\*` 同时是 `EmbeddedResource` 和 `CopyToOutputDirectory`，单文件发布后 exe 独立可运行。改 Web 文件无需重编译（磁盘存在时）。
- C# → JS：`SendEmotion`/`SendAction`（`PostWebMessageAsJson`）；页面未就绪时表情缓存到 `_pendingEmotion`，`NavigationCompleted` 后补发。
- JS → C#：`WebMessageReceived`，`Dispatcher.BeginInvoke` 回 UI 线程。

### 其他

- 单实例：`Mutex("TimeDot.Singleton")`。托盘用 WinForms `NotifyIcon`（csproj 已 `Using Remove` WinForms/Drawing 全局 using，避免与 WPF 类型冲突——新增 WinForms 引用需写全限定名）。开机自启写 HKCU Run。
- 资源占用是设计目标：吸附态动画全停、每秒刷新仅面板展开时进行、空闲裁剪工作集。改动动画/刷新逻辑时不要引入常驻高频定时器。

## 约定

- 代码注释与文档一律中文，风格与现有文件一致（密集的「为什么」注释是本库特色，改动处请延续）。
- README.md 中构建路径（`E:\ClaudeCode\prj1`）已过时，实际以本仓库根目录为准。
- 不是 git 仓库；`bin/`、`dist/`、`TimeDot/bin/` 等为构建/发布产物，`obj/` 勿改。
