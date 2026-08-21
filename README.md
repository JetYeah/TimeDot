# TimeDot — 克制风格的桌面工时悬浮球

Windows 常驻悬浮球,快速记录「项目号 + 用户」的工时。深色玻璃质感 + 青色单强调色,无多余装饰。

## 功能

- **悬浮球**:无边框透明置顶小窗,可拖到任意位置;外环低频呼吸
- **边缘吸附**:拖到屏幕边缘松手即吸附成一条 10px 小条;点击小条唤回悬浮球;空闲 60 秒自动吸附到最近边缘(托盘可关)
- **快速录入**:点球展开面板 → 项目号 + 用户 → 回车/「＋ 记录」即生成一条记录
- **计时**:每条记录**行首**按钮,点击记开始时间,再点记结束时间;多次点击产生多段,总时长累加。同一时刻全库只有一条在计时,开始新条自动停止旧条
- **搜索**:按 项目 / 用户 / 备注 / 日期 子串过滤;支持关键词 `今天` `昨天` `本周` 和紧凑日期 `20260819`
- **备注**:每条记录行内直接输入,实时保存
- **持久化**:`%APPDATA%\TimeDot\records.jsonl`(一行一条记录,人可读),原子写入 + `.bak` 备份;计时用绝对时间戳,重启/关机后继续累计
- **托盘**:双击唤出悬浮球;菜单:显示 / 空闲自动收边 / 开机自启 / 退出

## 数据格式

`%APPDATA%\TimeDot\records.jsonl`,每行:

```json
{"Id":"...","Project":"P-1001","User":"张工","Note":"备注","Created":"2026-08-19T23:22:53.120",
 "Sessions":[{"Start":"2026-08-19T23:23:14.352","End":"2026-08-19T23:23:18.801"}]}
```

`Sessions` 内 `End` 为 `null` 表示该段进行中;时间戳毫秒精度(兼容旧的秒级格式)。

**防丢失机制**:每次落盘走 `.tmp` → `File.Replace` 原子替换,上一版保留为 `.bak`;`records.meta.json` 记录上次成功落盘的行数,启动时若主文件有效行数低于该基准(被外部截断/损坏)自动回退 `.bak`;解析失败的行写入 `dropped.log` 供人工恢复。关机/注销/进程退出有落盘钩子兜住防抖窗口。

## 构建与运行

需要 .NET 8 SDK(已装:`C:\Program Files\dotnet\dotnet.exe`)。

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build E:\ClaudeCode\prj1\TimeDot.sln -c Release
# 运行
E:\ClaudeCode\prj1\TimeDot\bin\Release\net8.0-windows\TimeDot.exe
# 数据层测试(26 项)
& "C:\Program Files\dotnet\dotnet.exe" run --project E:\ClaudeCode\prj1\TimeDot.Tests -c Release
```

## 项目结构

| 路径 | 说明 |
|---|---|
| `TimeDot.Core/Models.cs` | WorkRecord / Session / AppConfig 模型,时长与跨天切分计算 |
| `TimeDot.Core/RecordStore.cs` | JSONL 存储:防抖原子写、备份、损坏行容错、计时切换、搜索 |
| `TimeDot/MainWindow.xaml(.cs)` | 窗口几何 / 拖拽 / 吸附 / 展开 / 动画循环 / 每秒刷新 |
| `TimeDot/MainWindow.vm.cs` | 行视图模型 + 面板事件处理 |
| `TimeDot/App.xaml.cs` | 单实例、托盘、开机自启(HKCU Run)、全局异常兜底 |
| `TimeDot.Tests/Program.cs` | 数据层回归测试 |

## 设计语言

球体参考 Siri / Apple Intelligence 的 orb:暗色玻璃球**内部**有 4 个柔色斑(蓝 / 紫 / 粉 / 青)以不同速度、不同方向缓慢公转,配合暗角收边、左上镜面高光、底部折射积光与外圈 bloom,营造"光在球内流动"的体积感;计时中色斑增亮并显示走秒。面板为半透明毛玻璃(细噪点 + 顶部冷光 sheen + 玻璃描边)。循环动画限定 30fps —— 慢速流动肉眼无差,渲染成本减半。

## 资源占用

吸附态实测:CPU ≈ 0.1% 单核,动画全部停止;自由态全动画 ≈ 1.5%;列表每秒刷新仅在面板展开时进行;记录列表启用 UI 虚拟化(VSP + Recycling),长期积累大量记录也不卡;空闲时周期性裁剪工作集。展开面板期间暂停球体动画以避免透明大窗每帧重绘。

## 设计取舍

- **点击穿透**:收起面板后窗口矩形不缩(透明区域天然点击穿透),球原地不动,零跳动
- **误触保护**:不足 1 秒的计时段视为误触直接丢弃,不产生空段
- **单计时**:同一时刻只允许一条记录计时(开始新条自动停旧条),语义干净
- **删错保护**:删除需连点两次确认,3 秒未复点自动撤销

## 致谢与许可

本项目悬浮球前端(`TimeDot/Web/` 目录:`ball.html`、`ball.js`、`engine.js`、`rings.js`、`emotions.js`)源自开源项目 **[emotion-ball](https://github.com/sam70361/emotion-ball)**(作者 [sam70361](https://github.com/sam70361)),按其许可证要求进行了修改与集成,特此致谢。

该部分代码及其视觉形象(球体造型、眼部设计、彩带特效等)遵循原项目的 **"Learning & Research Only License"(仅供学习研究许可)**,完整文本见 [`TimeDot/Web/LICENSE`](TimeDot/Web/LICENSE) 与 [`TimeDot/Web/NOTICE.md`](TimeDot/Web/NOTICE.md)。

> ⚠️ **非商业限制**:该许可证允许学习、研究、修改及非商业场景的分享,但**禁止任何商业用途**(售卖、付费授权、集成到商业产品或服务等)。由于本项目集成了该前端,TimeDot 整体同样不得用于商业用途。
