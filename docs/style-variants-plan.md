# 生物绘制双风格（卡通 / 写实）实施方案

> 目标一句话：给两端加一个**随时可切换**的外观开关——`卡通`（现状）与 `写实`（更接近真虫）。
> 写实版优先做好**苍蝇**。切换只改"长什么样"，绝不改任何机制。

状态：**已搁置（2026-10-02，用户决定）** —— 写实风格原型已完成并实测，
但**性能不达标**（见 §11），先把精力放回单风格；方案的其余部分（数据模型、开关位置、
验收标准）仍然有效，将来重启时从 §6 的 sprite 预烘开始。
适用范围：Android（Kotlin/Compose + Canvas 绘制）、Windows（WPF DrawingContext 绘制）

---

## 11. 实测结论与搁置原因（2026-10-02）

**写实苍蝇原型已实现并出图**（`docs/images/parked-realistic-fly-sheet.png`），
画法结论见本文件末尾"画法经验"。搁置的唯一原因是性能：

| 风格 | 生物数 | 实测帧率 | 帧间隔中位 | 绘制指令构建 | 每只成本 |
|---|---|---|---|---|---|
| 卡通 | 124 | 9.3 fps | 108 ms | 102 ms | ≈0.87 ms |
| 写实 | 150 | **2.8 fps** | 359 ms | 347 ms | ≈2.40 ms（**2.8×**） |

验收线是"写实 ≥ 卡通 × 0.7"，实测 0.30 → **不达标**，故不发布。

**这次实测挖到的关键事实（比结论本身更值钱）**：
耗时几乎全在**绘制指令构建**（102/108、347/359 都是这个比例），不是光栅化。
即瓶颈是"每只虫子每帧创建 ~40 个绘制原语"，不是"画得太细"。
→ 正解是 **sprite 预烘**：每只每帧 1 次 `DrawImage` 取代 ~40 个原语。
这条路**对现有的卡通风格同样是大提速**（卡通 9.3fps 也远不理想），
所以将来无论是否重启写实风格，`CreatureSprites` 都该做。

---

## 12. 画法经验（写实苍蝇原型留下，别丢）

- **比例关系才是"像不像"的关键**：真实家蝇顶视 体长:体宽 ≈ 2.4:1；腹部是**向后收尖的水滴形**（不是圆球）；
  翅**顺体向后覆盖、翅尖略超过腹端**；腿必须**短（总长 < 0.4s）且跗节回折**。
- 三个踩过的坑：① 翅画成侧向 62° 的碎片 → 像蜂不像蝇；② 腿伸得又长又直 → 像蚊子；
  ③ 复眼用鲜红且凸出成两个球 → 像瓢虫。
- 复眼要**暗红褐**（#7A2E22 系）。
- **LOD**：实机 34px 下细纹/刚毛/小眼面/翅痣全是灰噪点，应省略；
  4 条胸纵纹在实机档反而要**加粗**才不糊。

---

## 13. 卡通苍蝇翅膀修正（2026-10-02，已发布）

用户反馈："翅膀感觉向内收有点奇怪"。原因：旧实现把翅当成**两颗长轴近乎平行于体轴的椭圆**，
只旋转 ±24°，翅尖在尾部互相靠近 —— 读起来就是"向内收拢的叶片"。

修正：改为**绕翅根铰接**向外后张开（局部坐标 +X 为翅长方向）：
- 翅根移到**胸部**两侧（`x = +0.01s, y = ±0.13s`）——根放太后，翅身会横压在胖腹部上；
- 张开角 `128° - 16°·flap`（纯侧向 90°、纯向后 180°，128° 即向后外的 V 字），拍动时向前扫；
- 翅长 0.56s、宽度随 `flap` 收窄（抬起时顶视投影变窄）；
- 投影包络从 `h = 0.70s` 放宽到 `1.10s`（翼展变大后，旧包络会缩在翅下像画错）。

标尺图：`docs/images/fly-cartoon-wings.png`（1×/2×/3× × 浅底/深底）。

> 复现方式：`MaggotTask.exe --flysheet=<path.png> [--stage=fly|egg|larva|pupa]`
> 不开窗口、不读状态文件，改完绘制代码跑一次即可自查。

---

---

## 1. 判定与验收标准

| 编号 | 验收项 | 判定方式 |
|---|---|---|
| A1 | 切换开关在两端都能随手够到、即时生效（无需重启） | 实机操作：Android 面板 / Windows 控制面板 + 托盘 |
| A2 | 切换**不改变**任何机制结果 | 自测断言：同 seed 跑 600 tick，两种风格下 `Engine.Snapshot()` 与生物位置序列完全一致 |
| A3 | 写实苍蝇在正常尺寸下能认出"这是只真苍蝇" | 32 / 48 / 64 px 三档截图人工确认 |
| A4 | 性能不塌 | 140 只生物、写实风格帧率 ≥ 卡通风格的 70%（本机为软件渲染，阈值从宽但不能卡） |
| A5 | 旧存档/旧配置不受影响 | 缺字段一律取默认（卡通），不抛异常 |
| A6 | 风格**不得 gate 任何影响可见性的状态** | 代码审查 + 1.2.3 事故回归：`motionEnabled` 教训同样适用于 `style` |

## 2. 现状（已核对代码）

**Windows**
- `Render/CreaturePainter.cs`：静态类，`DrawShadow()` + `DrawBody()` → `DrawEgg/DrawLarva/DrawPupa/DrawFly`。
  已有"开关型"静态属性先例：`CreaturePainter.Shadow`（`ShadowMode`），由 `--shadow=` 命令行覆盖。
- `Render/Palette.cs`：语义色集中管理（`Fly #2E3A33`、`FlyRim #8FD3C0`、`WingA/B`、`EyeRed`）。
- 开关持久化：`Services/AppState.cs` → `state.json`；`PanelLeft/PanelTop` 已有"UI 偏好放在 Config 之外"的先例。
- 控制面板：`View/ControlPanelWindow.xaml` §7/§7b/§7c 是既有开关行；`ControlPanelViewModel` 绑 `MaggotConfig`。
- **性能实测（写进代码注释的历史数据）**：整层 BlurEffect ≈3fps；**逐只逐帧光栅化渐变 ≈4fps**；
  不画阴影 18.7fps；缓存位图接近"不画阴影"。→ 结论：**任何逐只逐帧的复杂矢量都不可接受**。

**Android**
- `render/CreaturePainter.kt`：实例类，`draw(canvas, c, task, stage, baseSize, motion, nowSec)`，单位坐标系 + `layer()` 统一变换。
- `render/SwarmView.kt`：持有 `painter` 实例，已有 `var motionEnabled`（服务侧与兜底层各自 set）——**风格开关照这个模式接**。
- UI 偏好：`SharedPreferences: maggot_ui`（`in_app_swarm` 先例），**不进 `MaggotConfig`**。

## 3. 关键设计决策（需要你点头的三条）

1. **存储位置：不进 `MaggotConfig`。**
   `docs/lifecycle-spec.md` 的配置区是两端共享契约，外观不属于契约。
   - Windows：`AppState` 顶层加 `CreatureStyle`（与 `PanelLeft/Top` 同类）。
   - Android：`SharedPreferences: maggot_ui` 加 `creature_style`。
   - 两端默认值都是 `Cartoon`（= 什么都不改，行为与 1.2.10 一致）。
2. **命名统一**：`CreatureStyle { Cartoon, Realistic }`，两端同名同序，`state.json` 里存字符串
   （`"cartoon"` / `"realistic"`），不用整数——手改文件也不会读错。
3. **档位只有两个，不加"半写实"。** 维护两套画法已经够贵；中间档会变成第三个要长期维护的资产。

## 4. 开关放哪（A1）

| 端 | 位置 | 形式 |
|---|---|---|
| Windows | `ControlPanelWindow` §7 开关行 → 加第 4 个 `CheckBox`「写实」 | 与「穿透/音效/通知」同风格，即时生效 |
| Windows | 托盘菜单 → 「外观：卡通 / 写实」 | 面板收起时也能切（面板可收起是既有功能） |
| Android | 悬浮面板 → 在「烂尾通知/拍死音效」后加 `ToggleRow("写实外观", ...)` | 面板是主控台，位置最顺手 |
| Android | 控制台「虫群」体检卡 → 与「在应用内显示虫群」并列再加一行 | 面板打不开时的备用入口 |

两个入口写同一个偏好键，双向同步（Android 用 `SharedPreferences.OnSharedPreferenceChangeListener`；
Windows 走 `App` 的 `RefreshViews()` 既有广播）。

## 5. 写实版画法规格（重点：苍蝇）

统一原则：**保留"阶段可辨识"**——写实不等于丢掉色码。卵仍是乳白、蛆仍是乳黄、蛹仍是深褐、
苍蝇仍是金属感深色。玩家必须一眼分得出阶段，否则玩法读不出来。

### 5.1 苍蝇（优先级最高，参考家蝇 Musca domestica）
| 部件 | 卡通现状 | 写实版 |
|---|---|---|
| 复眼 | 两个红色小圆 | 大占头面积的**半球+蜂窝小面**（径向渐变 + 8~12 个小格线），加一点高光；红褐 `#8C3B2A` |
| 胸部 | 单色圆 | 灰黑底 `#5E625C` + **4 条纵向暗纹** + 密集短刚毛（8~12 根细线） |
| 腹部 | 单色椭圆 | 褐黄底 `#7A6A55` + 3 条深色横带 + 中缝，边缘略透光 |
| 翅 | 纯色半透明椭圆 | **透明翼膜 + 5~7 条翅脉**，边缘略深；静止时平铺、飞行时高频拍动 |
| 腿 | 2 段深色线 | 3 段（股/胫/跗）+ 末端跗节，带关节折角 |
| 其它 | 无 | 平衡棒（halteres，腹部两侧小黄点，双翅目特征）+ 唇瓣口器 |

### 5.2 蛆
6 节体节保留，但加：**气门点**（每节侧面 1 个小黑点）、**口钩成对且带齿**、
体表**高光带**（湿润角质感）、尾端**后气门两个小孔**；节间缝加深、加一点"皱褶"抖动。

### 5.3 卵
半透明蛋壳感：外圈高光弧 + 内部浅影（幼虫轮廓若隐若现，随 rot 变明显）、
顶部** micropyle 小点**；`rot` 越大内部阴影越深（烂透了能看到里面的东西）。

### 5.4 蛹
围蛹形态：**桶状+端部环纹**，3 条体节纹 + 表面细微颗粒感（点阵），
深褐渐变 + 一侧高光；`rot` 接近 1 时出现裂缝预兆（一条浅色细线）。

## 6. 性能方案（A4 的关键，必须照做）

**Windows（分层软件渲染，最危险）**
- 每个阶段预烘 **sprite**（`RenderTargetBitmap`，Pbgra32，Freeze）：
  - 尺寸桶：`24 / 40 / 64 / 96 px`，按需懒生成（首次用到才烘）。
  - 苍蝇的**翅单独成 sprite**，拍动用"相位桶"：`6~8 档 scaleY` 各烘一张 → 拍动 = 换图 + 旋转，
    不做运行时几何运算。
- 每帧只做：`DrawImage(sprite, Rect)` + `PushTransform`（旋转）+ 入场淡入的 `PushOpacity`（仅 spawn 期）。
- 复用既有 `SoftShadowImage` 阴影路径（阴影不必分风格）。
- 档位回退：若实测写实风格帧率 < 卡通 × 0.7，则降低尺寸桶数 / 翅相位档数，**而不是砍风格**。

**Android**
- 同思路：`Bitmap` 缓存池，键 = `(stage, style, bucket, wingPhase)`；
  用 `Canvas.drawBitmap(bitmap, matrix, paint)`，`onDraw` 里不 new 对象（现有代码已有"提到类级别"的先例）。
- 悬浮层与兜底层各自持缓存（各自的 density 不同，不要共享）。

**两端共同**：预烘在**空闲时**做（Windows 启动后第一次绘制前、Android 首次 onSizeChanged 后），
避免切风格瞬间卡顿；期间先用卡通图顶上（**不得因为没烘好就不画** —— 这是 1.2.3 事故的同类风险）。

## 7. 实现清单（文件级）

**Windows**
1. `Render/CreatureStyle.cs`（新）：枚举 + 显示名 + 解析/序列化。
2. `Render/Palette.cs`：加写实专用色（胸部灰、腹带、翅脉、跗节、气门…），卡通色**不动**。
3. `Render/CreaturePainter.cs`：
   - 加 `public static CreatureStyle Style { get; set; } = CreatureStyle.Cartoon;`
   - `DrawBody` 按 `Style` 分派到 `DrawXxxCartoon` / `DrawXxxRealistic`（现有函数原样改名为 `…Cartoon`，逻辑零改动）。
   - 新增 `Render/CreatureSprites.cs`：sprite 烘焙 + 缓存 + LOD 选择。
4. `Services/AppState.cs`：加 `public string CreatureStyle { get; set; } = "cartoon";`
5. `App.xaml.cs`：启动时把 `AppState.CreatureStyle` 灌进 `CreaturePainter.Style`；改变时保存 + 刷新。
6. `View/ControlPanelWindow.xaml(.cs)` + `ControlPanelViewModel`：开关行加「写实」。
7. `Services/TrayService.cs`：托盘菜单加外观两项。
8. `Core/SelfTest.cs`：加 A2 断言 + sprite 缓存键唯一性断言。
9. `--style=` / `--stage=` 命令行（诊断/截图用，与 `--shadow=` 同款实现）。

**Android**
1. `render/CreatureStyle.kt`（新）：枚举 + `fromKey/toKey`。
2. `ui/theme/Color.kt`：加写实色（卡通色不动）。
3. `render/CreaturePainter.kt`：加 `var style`；按风格分派到 `drawXxxCartoon` / `drawXxxRealistic`。
4. `render/CreatureSprites.kt`（新）：Bitmap 缓存 + 相位/尺寸桶。
5. `render/SwarmView.kt`：加 `var style: CreatureStyle`（与 `motionEnabled` 同模式），`onDraw` 里传下去。
6. `service/SwarmService.kt`：读偏好 → set 到两个 View；偏好变化时立即 `invalidate()`。
7. `ui/ControlPanel.kt`：加「写实外观」`ToggleRow` + 回调。
8. `ui/MainActivity.kt`：体检卡加同一开关；`maggot_ui` 读写。
9. 单测：`SwarmSimulationTest` 加"风格不影响坐标"断言。

**文档**
10. `docs/design-system.md`：新增「§风格变体」章节（两套画法的硬约束：描边、阴影、阶段色码必须保留）。
11. `docs/lifecycle-spec.md`：**只在附录注明**"外观风格属表现层偏好，不入本规格配置区"（不改 §3）。
12. `README.md` 特性表加一行 + 双风格对比截图。

## 8. 测试与验收流程

1. 单元/自测：Android 53 单测（+2 风格断言）、Windows SelfTest 527（+约 6 条风格断言）全绿。
2. 视觉：Windows `--style=realistic --stage=fly` 截图；Android `adb shell screencap` 三档尺寸各一张。
3. 性能：`--diag-fps` 在 140 只生物下对比两种风格，记录到 `logs/` 并在交付里附数字。
4. 回归：切风格 20 次后确认无内存增长（Android Bitmap 缓存要设上限，超限 LRU 淘汰）。

## 9. 风险与对策

| 风险 | 对策 |
|---|---|
| 小尺寸下写实细节糊成一团 | **LOD**：< 24px 用"简化写实"（只有轮廓+复眼+腹带），不打蜂窝小面 |
| 软件渲染下写实版掉帧 | sprite 预烘 + 相位桶；实测不达标先降细节档，不降风格 |
| 写实版丢阶段辨识度 | 阶段色码是硬约束（§5 开头），写进 design-system 验收项 |
| 两套画法长期维护成本 | 只保留两个档；卡通逻辑不动（改名不改行为），写实新增独立文件 |
| 切风格瞬间白屏/卡顿 | 预烘期间先用卡通 sprite 顶上，**任何情况下都不跳过绘制** |
| 旧 state.json 缺字段 | 缺字段取默认（沿用 AppState 既有兼容策略），自测加一条老文件读取用例 |

## 10. 建议实施顺序（每步都可单独验收）

1. **第 1 步（先给你看效果）**：Windows 写实苍蝇原型 + `--style=` 开关 + 三种尺寸截图对比。
   你确认"像不像"后，再铺开其余三个阶段。
2. 第 2 步：Windows 补齐卵/蛆/蛹写实 + sprite 缓存 + 性能实测。
3. 第 3 步：Android 全套（同画法规格，Bitmap 缓存）。
4. 第 4 步：两端 UI 开关 + 文档 + 1.3.0 发布（APK + Windows zip + Release）。

> 估量：第 1 步是全部风险所在（画法与性能同时验证），做完之后 2–4 步是体力活。
