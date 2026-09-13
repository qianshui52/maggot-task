# 蛆虫任务 · Windows 版

WPF / .NET 7 原生桌面应用。没有 Electron、没有 NuGet 依赖、没有位图素材 ——
clone 下来 `dotnet build` 就能跑。

行为契约与 Android 版**完全一致**，共同遵守 [`../docs/lifecycle-spec.md`](../docs/lifecycle-spec.md)；
视觉规范见 [`../docs/design-system.md`](../docs/design-system.md)。

---

## 运行要求

| 项目 | 要求 |
|:--|:--|
| 系统 | Windows 10 1809 及以上（需要 `WS_EX_LAYERED` + 每显示器 DPI 感知） |
| 运行时 | .NET 7 Desktop Runtime（`Microsoft.WindowsDesktop.App`） |
| 依赖 | **无** —— 不需要任何 NuGet 包，也不需要联网 |

## 构建与运行

```powershell
cd windows
dotnet build MaggotTask.sln -c Release
.\MaggotTask\bin\Release\net7.0-windows\MaggotTask.exe
```

首次启动会在托盘出现图标，并同时弹出控制面板与任务列表。

---

## 怎么用

### 托盘菜单

| 菜单项 | 作用 |
|:--|:--|
| 显示/隐藏控制面板 | 小控制面板的显隐（文案随实际状态切换，不会脱钩） |
| 显示/隐藏虫群 | 全屏穿透叠加层的显隐 |
| 启动/暂停 | 冻结/恢复腐烂推进 |
| 打开任务列表 | 完整任务控制台 |
| 退出 | 保存状态并退出 |

双击托盘图标 = 唤回控制面板。气泡通知可点击跳转。

> 图标本身是运行时用 GDI+ 程序化画出来的 `HICON`，**没有 .ico 文件**。

### 控制面板

按 `docs/design-system.md` §7：运行状态灯、四阶段计数、腐烂压力条、
启动/暂停、腐化倍速滑块、新建/重置/清空、以及穿透/通知/音效/每日/每周开关。
**标题行左边的「蛆虫任务」是拖动手柄**，右边是 `收起` / `任务列表` 两个 ghost 按钮
（热区 32px 高、可键盘聚焦、有聚焦环；它们在独立列里，不可能被当成拖动）。
`收起` 只是隐藏面板，托盘菜单文案同步变成「显示控制面板」，双击托盘图标可唤回。
**清空**会弹二次确认并给出确切任务数。

### 每日任务（规格 §12）

任务列表窗口顶部有「每日任务」区：`新增模板` 输入框 + 每条模板的
启用/停用开关、今天的实例状态（未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成）和删除按钮。

- 新增模板会**立刻**生成今天那只虫（不用等到明天）。
- 停用模板不再生成新实例，**已有实例照常腐烂**。
- 删除模板后实例保留，退化成普通任务继续烂。
- 逾期实例在任务列表和悬停气泡上打 **「已逾期 N 天」** 标记。
- 面板上的「每日任务」开关是总闸（关掉不影响已有实例）。

### 每周任务（规格 §15）

与每日任务**完全同构**，周期换成「周」，并且可以指定**星期几**。
任务列表窗口里有第二个区「每周任务」：`新增模板` 输入框 + 星期几选择按钮 + 每条模板的
星期/启用停用开关、本周实例状态（未生成 / 卵 / 蛆 / 蛹 / 苍蝇 / 已完成）和删除按钮。

- 星期几用 ISO 编号 **1=周一 … 7=周日**；实例在这一天**或之后**生成 ——
  周一没开电脑、周三才打开，照样补上，不跳过这一周。
- 新增模板时如果今天已经到达该星期，会**立刻**生成本周那只虫。
- 停用 / 删除 / 总开关的语义与每日任务逐条相同。
- 逾期实例（上周没写的那份）在任务列表和悬停气泡上打 **「已逾期 N 周」** 标记。
- 周键用 ISO-8601 的 `YYYY-Www`，并且**用周基准年**（`ISOWeek.GetYear`）——
  否则跨年那一周会算错（`2027-01-01` 属于 `2026-W53`）。

### 悬停查看任务（规格 §14）

鼠标在任意一只虫上**停满 3 秒**，光标旁弹出信息气泡：
标题 / 阶段 / 腐烂 / 放置 / （每日实例显示逾期天数、每周实例显示逾期周数）。
移开立刻收起；从 A 划到 B 会重新数 3 秒；暂停时同样有效。

> 虫群层是点击穿透的，收不到鼠标消息，所以这里用**每 100ms 轮询 `GetCursorPos`**
> 来实现 —— 不装全局钩子、不注入任何东西。气泡自己是
> `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`，不抢焦点、不进 Alt-Tab、
> 也不挡下面的点击。

### 音效（规格 §13）

四段音效全部是**运行时在内存里合成的 PCM**（噪声爆裂 + 下行扫频 / 低频颤音锯齿 /
低频「啵」/ 两声上行短音），套一个 WAV 头用 `winmm!PlaySound` 播放 ——
没有音频素材文件、没有 NuGet 包。开关**默认开启**，打开时会立刻试听「拍死」。
播放失败一律静默，同一音效 30ms 内不重复触发。

### 关掉穿透之后

虫群层默认 `WS_EX_TRANSPARENT`，鼠标完全穿透，不挡任何操作。
关掉「穿透点击」后虫群层开始接收鼠标 —— 方便你直接点它们。
为避免把自己困住，**45 秒后自动恢复穿透**，面板上会显示倒计时。

---

## 状态与文件

| 路径 | 内容 |
|:--|:--|
| `%APPDATA%\MaggotTask\state.json` | 任务 + 每日/每周模板 + 配置 + 面板位置 + `lastSeenAt`（v3） |
| `%APPDATA%\MaggotTask\selftest-report.txt` | `--selftest` 的完整报告 |
| `%APPDATA%\MaggotTask\ui-diagnostics.txt` | `--diag-ui` 的窗口/字体/渲染/新界面诊断 |
| `%APPDATA%\MaggotTask\weekly-diagnostics.txt` | `--diag-weekly` 的每周任务模板/周键/逾期周标记诊断 |
| `%APPDATA%\MaggotTask\hover-diagnostics.txt` | `--diag-hover` 的悬停驻留诊断 |
| `%APPDATA%\MaggotTask\sound-diagnostics.txt` | `--diag-sound` 的缓冲区统计 |
| `%APPDATA%\MaggotTask\{panel,tasklist}-v11.png` | 新界面的渲染证据 |
| `%APPDATA%\MaggotTask\tasklist-weekly-v11.png` | 每周任务区（含「已逾期 N 周」标记）的渲染证据 |
| `%APPDATA%\MaggotTask\swarm-on-{white,black}.png` | 叠加层合成到纯白/纯黑的渲染证据 |

落盘是**防抖**的（变更后 + 每 5 秒 + 退出时）。
JSON 损坏时回退到默认状态并记录原因，不会抛异常。

**schema 版本**：写入 `version: 3`（加入每周任务）。读取时**不看版本号** ——
缺失字段一律取默认值，所以旧的 v1 `state.json`（没有 `dailyTemplates`/`weeklyTemplates`）
和 v2（有每日、没有每周）都能直接读进来，不抛异常。

---

## 命令行开关（用于验证）

这些开关都在正常启动逻辑之前返回，不影响你的真实状态：

```powershell
MaggotTask.exe --selftest     # 跑 527 条引擎验收断言，写报告，exit code 0/1
MaggotTask.exe --diag-ui      # 窗口 Win32 样式、字体解析、渲染统计 + 新界面/逾期标记
MaggotTask.exe --diag-weekly  # 每周模板/周键/本周状态 + 可视树里的「已逾期 N 周」标签数
MaggotTask.exe --diag-hover   # 程序化驱动悬停驻留逻辑，逐条核对 §14.4
MaggotTask.exe --diag-sound   # 打印四段音效的时长/峰值/RMS 与 WAV 头校验
MaggotTask.exe --seed-demo    # 灌一份演示任务（含各阶段与后代）
MaggotTask.exe --smoke        # 快速启动并退出，用于 CI 冒烟
```

> 这是 `WinExe`（GUI 子系统），**stdout 不会自动接到控制台**。
> 想看输出必须重定向：
> ```powershell
> Start-Process .\MaggotTask.exe -ArgumentList '--selftest' -NoNewWindow -Wait `
>   -RedirectStandardOutput out.txt
> ```
> 程序内部会把 stdout 换成带 BOM 的 UTF-8，所以重定向出来的中文报告直接可读。

---

## 代码结构

```
MaggotTask/
  Core/      Stage / TaskItem / MaggotConfig / EngineEvent
             DailyTemplate.cs     ← 每日模板 + DailyKeys（本地日期/逾期/放置文案）
             WeeklyTemplate.cs    ← 每周模板 + WeeklyKeys（ISO 周键/星期/逾期周数）
             LifecycleEngine.cs   ← 与 Android 逐行等价的引擎（含 §12 每日、§15 每周生成）
             SelfTest.cs          ← 527 条断言
  Render/    CreaturePainter.cs   ← 程序化矢量绘制（卵/蛆/蛹/苍蝇）
             CreatureVisual.cs    ← 单只生物的运行时状态
             SplatField.cs        ← 爆浆粒子
             Palette.cs / ThemeFonts.cs / Rng.cs
  View/      OverlayWindow        ← 全屏透明穿透层
             ControlPanelWindow   ← 可拖动控制面板（含收起/任务列表按钮）
             TaskListWindow       ← 任务控制台（含每日/每周任务区、逾期标记）
             HoverBubbleWindow    ← 悬停信息气泡（NOACTIVATE + TOOLWINDOW）
             HoverTracker.cs      ← §14 驻留逻辑（纯逻辑，可单测）
             DailyTemplateRowViewModel.cs / WeeklyTemplateRowViewModel.cs
             ConfirmDialog        ← 危险动作二次确认
             SwarmModel.cs        ← 生物集合与模拟推进
             WindowStyles.cs      ← Win32 扩展样式（穿透 / 气泡）
             Theme.xaml           ← 32KB 设计 token 与控件样式
  Services/  PersistenceService / NotificationService
             SoundService.cs      ← 程序化 PCM 合成 + WAV 头 + winmm 播放
             TrayService / TrayIconFactory / SimulationPacer
  Assets/Fonts/  Space Grotesk + JetBrains Mono（与 Android 同一份静态实例）
```

**43 个 `.cs`（约 9 330 行）+ 7 个 `.xaml`（约 1 490 行）**，Release 产物约 1.4 MB。

### 时钟

`SimulationPacer` 把模拟固定在 `1/60` 秒步长（规格 `TICK_HZ`）。
没有它，腐烂速度会随显示器刷新率变化 —— 144Hz 屏幕上虫子会烂得快 2.4 倍。

---

## 已验证 / 未验证

**已在真机验证：**

- `dotnet build -c Release` 干净构建：**0 错误 0 警告**
- `--selftest`：**527 通过 / 0 失败**，退出码 0（原 371 条全部保留并通过，
  新增 §15.6 全部 10 条验收标准、跨年周键（`2026-12-31` 与 `2027-01-01` 同为 `2026-W53`）、
  `wd < weekday` 不生成、跨周不重置 rot、繁殖出的后代 `weeklyTemplateId == null`、
  每日/每周互不影响、每周模板增删改与幂等/排序/生命周期同构、
  以及 v2 `state.json`（无每周字段）兼容加载）
- 正常启动路径运行 ≥12 秒无崩溃、无卡死，退出正确写盘（`version: 3`）
- 叠加层 Win32 样式回读：`WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE` 全部置位；
  运行时切换只翻转 `TRANSPARENT`，其余保留
- 悬停气泡 Win32 样式回读：`0x080800A8` =
  `LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE` 全部置位（`--diag-hover`）
- 「收起」→ 面板隐藏 → 托盘菜单文案同步（`--diag-ui` 实测 PASS，两个方向都试过）
- 逾期标记真的渲染进可视树并有截图（`--diag-ui` 实测「已逾期 2 天」标签数 1）
- 每周任务区实测（`--diag-weekly`）：3 条模板的星期/启用/本周状态逐条打印，
  周键 `2026-W37`、`weekday=7 > 今天(6)` 不生成、可视树里的 **「已逾期 N 周」标签数 1**，
  悬停气泡「每周」行显示「已逾期 1 周」、普通任务上该行收起；
  带每周行的任务列表渲染成 `tasklist-weekly-v11.png`
- 真机往返：手写一份含每周模板的 `state.json` → 正常启动 → 立刻补出本周实例
  （`写周报 · W37`、`weeklyKey=2026-W37`、`gene=1.0`、`generation=0`）→ 退出重新落盘仍为 v3
- 字体经 pack URI 解析到仓库内 `.ttf`；两款字体确实**没有 CJK 字形**，
  中文回退到 Microsoft YaHei UI（混排 `"腐烂度 ROT 0.78"` 实测渲染宽度 110.6px）
- 生物渲染合成到**纯白**与**纯黑**背景，肉眼确认轮廓/描边/阴影均可读
- 音效缓冲区实测：拍死 170ms 峰值 0.8600 / RMS 0.1236；嗡鸣 400ms 0.8464 / 0.2929；
  孵化 90ms 0.8600 / 0.1773；繁殖 260ms 0.7650 / 0.3348 —— 全部非静音、WAV 头自洽

**未验证（诚实说明）：**

- **鼠标实际点击各个控件**未被自动化验证。核心业务逻辑 100% 被 527 条断言覆盖，
  新按钮的**热区尺寸/可聚焦性/聚焦环**由 `--diag-ui` 实测，
  悬停驻留由 `--diag-hover` 用合成光标位置实测，
  每周任务区的星期选择按钮由 `--diag-weekly` / 自测核对逻辑（默认选中「今天」、
  点一下往后挪一天、周日回到周一），但「人手点下去有没有反应」没有端到端跑过。
- **音效没有被耳朵听过** —— 只能证明合成出的 PCM 非静音、头部合法、播放调用成功。
- **多显示器**未验证 —— 测试机只报告 1 台显示器。代码走
  `VirtualScreenLeft/Top/Width/Height` 并在 `DisplaySettingsChanged` 时重新贴合。
- **减少动态效果（reduced-motion）的视觉效果**未实际观察 ——
  测试机 `ClientAreaAnimation` 为 True。分支已实现（静止姿态 + ≥4 秒呼吸、
  不扇翅不抽动、爆浆退化为淡出）但只做了静态检查。
- **跨天 / 跨周真机等待**未做 —— 跨天由自测用显式的 `todayKey` 覆盖（§12.5-3），
  跨周 / 跨年由自测用显式的 `weekKey`（`2026-W38` → `2026-W39`、`2026-W53` → `2027-W01`）覆盖（§15.6-4 / §15.6-9）。

---

## 性能

**先说结论：这个应用在软件渲染环境下的帧率，几乎完全由一个 bug 决定。**

### 怎么发现的

先用 `--diag-fps` 量**真实帧间隔**（不是"绘制指令耗时"）：

| 场景 | 帧率 |
|:--|:--|
| 空叠加层 | **60.2 fps**（vsync 上限，说明全屏重绘本身不贵） |
| 100 只生物（修复前） | **2.4–4 fps** |
| 100 只生物（修复后） | **18.8 fps** |

再把四个阶段分开压测，发现**卵（2 个椭圆）和苍蝇（20 多个图元）一样慢** ——
复杂度无关，说明瓶颈不在"画得多"。

### 真凶：一个恒为真的条件

```csharp
double alpha = 0.85 * a;        // a = SpawnProgress
bool fading = alpha < 0.999;    // ← 0.85 恒小于 1，永远为 true
if (fading) dc.PushOpacity(alpha);
```

本意是"只在入场淡入的 0.45 秒内开透明度图层"，但判据写成了含 0.85 强度系数的
`alpha`。结果是**每一只、每一帧都开一层合成表面** —— 100 只就是 100 层，
单这一项吃掉约 190 ms/帧。

判据改成 `SpawnProgress`（`bool fading = a < 0.999;`）后：**3.2 fps → 18.8 fps**。

### 顺带做的两项

- 阴影从「整层套全屏 `BlurEffect`」改成**把径向渐变一次性烘进 128×128 位图**，
  每帧只做 `DrawImage`。软件光栅化渐变极慢，这是第二大开销。
- `RenderOptions.SetBitmapScalingMode(..., LowQuality)`：WPF 默认的 Fant
  高质量重采样在缩放小图时很贵，而阴影只是个模糊色块，看不出来。

### 诊断开关

```powershell
MaggotTask.exe --diag-fps 100              # 真实帧间隔统计
MaggotTask.exe --diag-fps 100 --stage=fly  # 只压某一阶段
MaggotTask.exe --diag-fps 100 --shadow=blur   # 切回旧的整层模糊做 A/B
MaggotTask.exe --diag-fps 100 --shadow=none   # 完全关阴影（隔离用）
```

> ⚠️ `--diag-fps` 会往 `state.json` 写入压测任务。跑完记得清。

### 还没榨干的部分

`--shadow=none` 时 140 只生物仍只有 18.7 fps，其中约 24 ms 是**构建绘制指令**本身
（0.17 ms/只）。要真正稳到 60 fps，得把生物美术预渲染成**位图精灵**、每帧只 blit，
那是渲染架构级改动。当前的虚拟显示器（`OrayIddDriver`，无 GPU）也压低了上限。
