# 蛆虫任务 · 生命周期引擎规格 v1.0

> 这是 Android 与 Windows 两端**必须逐字实现一致**的行为契约。
> 两端各自用本地语言重写本规格（Kotlin / C#），不得各自发明规则。
> 任何一侧改了数值，另一侧必须同步。

---

## 1. 核心概念

一个「任务」就是一只虫子。它不会等你——你不管它，它就自己往下烂。

```
🥚 卵  ──→  🐛 蛆  ──→  🟤 蛹  ──→  🪰 苍蝇  ──→  ∞ 交配繁殖
 0.00      0.18       0.50        0.78            （≥2 只时）
```

形态**完全由 `rot`（腐烂度）决定**，不是由日历决定。
`rot ∈ [0, 1.20]`，`1.0` 以上表示「烂过头了」，苍蝇会更狂躁。

| 阶段 | 触发条件 | 视觉 |
|---|---|---|
| `EGG` 卵 | `rot < 0.18` 且未开始 | 白色小点，安静，轻微呼吸感 |
| `LARVA` 蛆 | `rot ≥ 0.18` **或** `status == DOING` | 米黄分节，正弦蠕动，缓慢拱行 |
| `PUPA` 蛹 | `rot ≥ 0.50` | 棕色胶囊，静止，偶尔抽动一下 |
| `FLY` 苍蝇 | `rot ≥ 0.78` | 深色躯干 + 虹彩翅，乱飞、急冲 |
| `DEAD` | `status == DONE` | 爆浆动画后消失 |

**关键推论：**
- 新建后从不碰 → 卵自己孵化成蛆 → 蛹 → 苍蝇。**放着不管一定会烂。**
- 点「我去做了」→ 立刻显示为蛆，并倒扣腐烂度。

---

## 2. 常量（两端必须一致）

```
ROT_MAX            = 1.20    // 腐烂度上限
HATCH_T            = 0.18    // 卵 → 蛆
PUPA_T             = 0.50    // → 蛹
FLY_T              = 0.78    // → 苍蝇
APPROACH_T         = FLY_T - 0.10 = 0.68   // 预警线（推通知）

ROT_MAX_SAFE       = 1.00    // 超过此值仅影响动画强度

CARE_RELIEF        = 0.22    // 「我去做了」最大倒扣腐烂度
CARE_COOLDOWN_SEC  = 20.0    // 恢复满额效果所需间隔
CARE_MIN_FACTOR    = 0.35    // 冷却未满时的最低效果系数
CARE_PROGRESS_MIN   = 4      // 每次「我去做了」最少推进的进度
CARE_PROGRESS_MAX   = 12     // 最多推进的进度

GENE_STEP          = 0.10    // 每繁殖一代，腐烂速度 +10%
GENE_MAX           = 2.20    // 基因系数上限
OFFLINE_CAP_MIN    = 45.0    // 重新打开时最多补算 45 分钟的腐烂

TICK_HZ            = 60      // 前台/可见时
IDLE_TICK_HZ       = 10      // 悬浮层关闭时降频省电
MAX_DT             = 0.50    // 单帧最多推进 0.5 秒，防止休眠后跳变
```

阶段腐烂倍率 `stageMultiplier`：

| 阶段 | 倍率 |
|---|---|
| `EGG` | 0.70 |
| `LARVA` | 1.00 |
| `PUPA` | 1.35 |
| `FLY` | 1.60 |

---

## 3. 数据结构

```
TaskItem
  id           : String   // UUID
  title        : String
  note         : String   // 可为空
  status       : TODO | DOING | DONE
  rot          : Float    // 0.0 .. 1.20
  progress     : Int      // 0 .. 100
  createdAt    : Long     // epoch millis
  lastCaredAt  : Long     // epoch millis，创建时 = createdAt
  completedAt  : Long?    
  generation   : Int      // 0 = 用户亲手创建；>0 = 苍蝇繁殖出来的
  parentId     : String?  // 繁殖来源
  gene         : Float    // = min(1 + GENE_STEP*generation, GENE_MAX)
  dailyTemplateId : String?  // 非空 = 这是某个每日任务模板生成的实例
  dailyKey        : String?  // "YYYY-MM-DD"（设备本地时区）；仅每日实例有值

DailyTemplate
  id        : String   // UUID
  title     : String
  note      : String   // 可为空
  enabled   : Boolean  // = true，关掉后不再生成新实例（已有实例不受影响）
  createdAt : Long
  sortOrder : Int      // = 0，列表排序

MaggotConfig
  running        : Boolean = true
  rotSpeed       : Float   = 1.0     // 0.25 .. 8.0  「腐化倍速」
  baseRotMinutes : Int     = 90      // rotSpeed=1 时 0→1 所需的分钟数
  breedEnabled   : Boolean = true
  breedSeconds   : Int     = 40      // 交配间隔基准（秒，受 rotSpeed 影响）
  maxFlies       : Int     = 60      // 苍蝇数量上限
  maxTasks       : Int     = 150     // 任务总数上限
  clickThrough   : Boolean = true    // 虫群层是否穿透鼠标/触摸
  soundEnabled   : Boolean = true    // 程序化音效（见 §13）
  notifyOnFly    : Boolean = true
  dailyEnabled   : Boolean = true    // 每日任务总开关
```

---

## 4. 腐烂推进

每个 tick（`dt` 秒，已 clamp 到 `MAX_DT`）：

```
if (!config.running) 跳过
if (task.status == DONE) 跳过

mult = stageMultiplier(stageOf(task))
rate = (1 / (baseRotMinutes * 60)) * rotSpeed * task.gene * mult
task.rot = min(task.rot + rate * dt, ROT_MAX)
```

`stageOf(task)`：

```
if (task.status == DONE)                 -> DEAD
if (task.rot >= FLY_T)                   -> FLY
if (task.rot >= PUPA_T)                  -> PUPA
if (task.rot >= HATCH_T)                 -> LARVA
if (task.status == DOING)                -> LARVA
                                         -> EGG
```

> 注意顺序：`DOING` 的判断放在最后，保证点了「我去做了」立刻变蛆，
> 但已经烂到蛹/苍蝇的不会因为点一下就退回蛆（必须先做完）。

---

## 5. 「我去做了」（care）

```
sinceSec = (now - task.lastCaredAt) / 1000
factor   = CARE_MIN_FACTOR + (1 - CARE_MIN_FACTOR) * min(sinceSec / CARE_COOLDOWN_SEC, 1)
relief   = CARE_RELIEF * factor

task.rot         = max(0, task.rot - relief)
task.lastCaredAt = now
task.status      = DOING
task.progress    = min(100, task.progress + CARE_PROGRESS_MIN + round(factor * (CARE_PROGRESS_MAX - CARE_PROGRESS_MIN)))
```

冷却设计是**软性**的：不阻止点击，只是连续点收益递减。
用户任何时候点都有反馈，但刷不出来。

---

## 6. 拍死（complete）

```
task.status      = DONE
task.progress    = 100
task.completedAt = now
```
- 立刻从虫群里移除该生物，并在其当前位置播放「爆浆」粒子动画。
- 计入 `squashedCount`。
- 已拍死的任务保留在「战果」列表；超过 **50** 条自动清理最旧的。

---

## 7. 繁殖（这是失控的来源）

每个 tick，若 `config.running && config.breedEnabled`：

```
flies = 所有 stageOf(t) == FLY 的任务
if (flies.size < 2)              { breedAccum = 0; return }   // 一只苍蝇不繁殖
if (flies.size >= maxFlies)      { breedAccum = 0; return }   // 苍蝇数量触顶
if (tasks.size >= maxTasks)      { breedAccum = 0; return }   // 任务总数触顶

breedAccum += dt * rotSpeed
interval = breedSeconds / (1 + (flies.size - 2) * 0.06)   // 苍蝇越多，产卵越快
if (breedAccum >= interval) {
    breedAccum = 0
    a, b   = 从 flies 中随机取两只不同个体
    gen    = max(a.generation, b.generation) + 1
    child  = 新建任务：
        title      = "蝇卵 #<gen> · <a.title 截断至 10 字>"
        generation = gen
        gene       = min(1 + GENE_STEP*gen, GENE_MAX)
        rot        = 0
        status     = TODO
        parentId   = a.id
        createdAt  = now, lastCaredAt = now
    发出 Bred 事件（a, b, child）
}
```

**设计意图**：`gene` 让后代腐烂更快 —— 一代比一代凶，符合「一窝变十窝」。

> ### ⚠️ `maxFlies` / `maxTasks` 的语义边界（两端必须一致）
>
> `maxFlies` 和 `maxTasks` **只封顶繁殖**，**绝对不允许冻结腐烂**。
>
> 也就是说：即使苍蝇已经到达上限，一个正在腐烂的任务**仍然会正常变成苍蝇** ——
> 只是不再产生新的卵。
>
> **反例（禁止实现）**：给 `rot` 设一个随苍蝇数量下降的"天花板"，
> 让任务永远停在 `FLY_T` 下方。这会导致任务永久卡在「即将变苍蝇」，
> 把产品最核心的承诺（放着不管一定会烂）整个推翻。
> 任何一侧出现这种写法，都视为规格违背。

---

## 8. 离线补算

应用重新回到前台（或服务重启）时：

```
elapsedSec = (now - lastSeenAt) / 1000
capped     = min(elapsedSec, OFFLINE_CAP_MIN * 60)
若 capped <= 1 秒则跳过
按第 4 节公式，用 capped 作为 dt 对每个未完成任务一次性推进 rot
刷新阶段（不发繁殖，不同步交配）
```

**关掉应用躲不掉，但最多只补 45 分钟**——足够肉痛，不至于隔夜回来满屏苍蝇。

---

## 9. 事件（供 UI / 通知消费）

```
Hatched(taskId, title)        // 卵自己孵了
Pupated(taskId, title)        // 卡住变蛹
ApproachingFly(taskId, title) // rot 越过 0.68
BecameFly(taskId, title)      // rot 越过 0.78  → 推通知
Bred(aId, bId, childId)       // 交配产卵
Squashed(taskId, title)       // 拍死
```

首次加载已存在的任务时**不发**阶段事件（避免每次启动都炸一堆通知）；
但启动时要**主动检查**一次「已经是苍蝇」的任务并推送汇总通知。

---

## 10. 控制面板动作

| 动作 | 语义 |
|---|---|
| 启动 / 暂停 | `config.running` 取反。暂停时 rot 完全不涨。 |
| 重置 | 所有任务 `rot = 0`、`progress` 保留、`lastCaredAt = now`；苍蝇全部退回卵。**不删任务。** |
| 清空 | 删除全部任务，计数归零。需二次确认。 |
| 腐化倍速 | `config.rotSpeed`，滑块范围 0.25–8.0，对数刻度更顺手。 |
| 穿透开关 | `clickThrough`。关闭后虫群层吃掉点击，方便「拍」它们。 |

---

## 11. 判定标准（两端自测用）

1. 新建任务，`rotSpeed=8`，什么都不做 → 应在约 `baseRotMinutes*60/8 ≈ 11 分钟` 内走完 卵→蛆→蛹→苍蝇。
2. 只有 1 只苍蝇时，等待 5 分钟 → 任务数**不增加**（不繁殖）。
3. 制造 2 只苍蝇，等待 → 必然出现 `generation ≥ 1` 的新任务。
4. 点「我去做了」→ 阶段立刻变 `LARVA`，`rot` 下降。
5. 连续快速点「我去做了」10 次 → 总下降量明显小于 10 × `CARE_RELIEF`。
6. 暂停后等待 → 所有 `rot` 不变。
7. 「重置」后 → 所有 `rot == 0`，任务数量不变。
8. 新建一个每日任务模板，把系统日期往后调一天 → 自动出现一个带新日期后缀的实例，旧的实例**依然在腐烂**。

---

## 12. 每日任务（v1.1 新增）

### 12.1 语义

一个「每日任务」不是一个任务，而是一个**模板**。它每天生成一个新的实例，
实例标题带当天日期，然后**完全按普通任务的生命周期腐烂**。

```
模板：「锻炼」
   ↓ 每天 00:00（本地时区）
实例：锻炼 · 09-13   ← 今天，rot 从 0 开始烂
实例：锻炼 · 09-12   ← 昨天，如果没做完，它还在烂，甚至已经是苍蝇了
```

**这是刻意的**：漏掉的每日任务不会自动消失，它会继续烂、变成苍蝇、参与繁殖。
「昨天没做」这件事必须一直恶心你，否则每日任务就退化成了一个安静的清单。

代价是可能堆积，靠 `maxTasks` 兜底；界面上给逾期实例打 **「已逾期 N 天」** 标记。

### 12.2 生成规则

每次 tick（或至少每 60 秒）调用一次：

```
todayKey = 设备本地日期格式化为 "YYYY-MM-DD"

EnsureDailyTasks(todayKey, now):
    if (!config.dailyEnabled) return
    for (t in dailyTemplates，按 sortOrder 排序):
        if (!t.enabled) continue
        if (tasks 中存在 dailyTemplateId == t.id 且 dailyKey == todayKey): continue
        新建任务：
            title           = "<t.title> · <MM-DD>"     // MM-DD 取自 todayKey
            note            = t.note
            dailyTemplateId = t.id
            dailyKey        = todayKey
            generation      = 0
            gene            = 1.0
            rot             = 0
            status          = TODO
            createdAt       = now
            lastCaredAt     = now
        发出 DailySpawned 事件
```

**幂等**：同一天无论调用多少次，每个模板最多只会有一个实例。
跨天（`todayKey` 变化）时自然再生成一个。

**时区**：一律用**设备本地日期**。不要用 UTC —— 用户在 UTC+8 晚上 8 点看到的"今天"必须是他的今天。

### 12.3 生命周期不变式

每日实例与普通任务**没有任何生命周期上的区别**：
- 同样的阶段判定、同样的腐烂速率（`gene = 1.0`，不吃繁殖加成）
- 可以被「我去做了」「拍死它」
- 会变成苍蝇，会参与繁殖（繁殖出的后代是**普通任务**，不是每日实例）
- 「重置」会把它退回卵，「清空」会删掉它

**唯一区别是它从哪来，以及标题带日期。**

### 12.4 模板管理

| 动作 | 语义 |
|:--|:--|
| 新增模板 | 立即检查一次今天是否已有实例；没有就**马上生成**（用户点完就能看见虫子出现） |
| 停用模板 | `enabled = false`。不再生成新实例，**已有实例不受影响**（删虫子要有明确动作） |
| 删除模板 | 移除模板。已有实例**保留**，只是 `dailyTemplateId` 变成孤儿 —— 它们退化成普通任务继续烂 |
| 总开关 | `dailyEnabled = false` 时完全不生成。同样不影响已有实例 |

### 12.5 验收标准

1. 建一个模板「锻炼」→ **立刻**出现一个 `锻炼 · MM-DD` 的任务，`rot == 0`。
2. 同一天内反复调用 `EnsureDailyTasks` 100 次 → 实例数量恒为 1。
3. 把 `todayKey` 换成明天再调用 → 变成 2 个实例，且**昨天那个的 `rot` 没有被重置**。
4. 停用模板后再调用 → 不生成新实例；旧实例照常腐烂。
5. 删除模板 → 实例还在，且能正常完成 / 变成苍蝇。
6. 每日实例变成苍蝇后参与繁殖 → `Bred` 的子代 `dailyTemplateId == null`。
7. `dailyEnabled = false` → 一个实例都不生成。

---

## 13. 音效（v1.1 新增）

**目标是「恶心」，不是「提示」。** 系统自带提示音（`SystemSounds.*`）一律不合格。

两端都必须**程序化合成**，不引入任何音频资源文件（离线构建约束）。
具体波形可以各自实现，但必须覆盖这四个语义：

| 事件 | 情绪目标 | 合成方向 |
|:--|:--|:--|
| **拍死（Squashed）** | 爆浆、解压 | 噪声爆裂 + 下行音高扫频 + 快速指数衰减（约 170ms） |
| **苍蝇嗡鸣（BecameFly）** | 烦人、挥之不去 | 低频锯齿/方波 + 慢速颤音，约 400ms |
| **孵化（Hatched）** | 黏腻、破壳 | 极短的低频"啵"+ 轻微噪声，约 90ms |
| **繁殖（Bred）** | 更多了、失控 | 两声快速上行的短音，约 260ms |

规则：

- `soundEnabled` **默认开启**。用户装了它就是要听见它。
- 开关**打开时立刻试听**（播放"拍死"），让用户马上知道是什么声音 —— 否则他只会以为坏了。
- 播放失败**一律静默**：不弹窗、不写日志刷屏、绝不阻塞渲染循环。
- **连点保护**：同一音效 30ms 内不重复触发，避免密集事件叠成噪音。
- 必须尊重系统静音状态。

---

## 14. 悬停查看任务（v1.1 新增）

鼠标在某个生物上**持续停留 3 秒**，显示它对应的任务信息。

### 14.1 关键约束（Windows）

虫群层是 `WS_EX_TRANSPARENT`（点击穿透），**它收不到任何鼠标消息**。
所以不能用 `MouseMove`，也不能为此装全局钩子（会被杀毒软件盯上）。

正确做法：**轮询 `GetCursorPos`**，频率约 10–15Hz：

```
每 100ms:
    取全局光标位置 p
    在生物集合里找最近的一只，若距离 <= 命中半径:
        if (命中 id == 当前悬停 id) 累计驻留时间
        else 换目标，驻留时间归零
        if (驻留 >= 3.0s 且还没弹过) 弹出信息气泡
    else:
        清空悬停目标；收起气泡
```

轮询成本极低（一次 `GetCursorPos`），且不需要注入任何东西。

### 14.2 气泡内容

紧贴光标显示，**不抢焦点**（`WS_EX_NOACTIVATE`）、不进入 Alt-Tab：

```
┌────────────────────────────┐
│ 锻炼 · 09-13               │   ← 标题
│ 阶段  蛹                    │   ← 阶段中文名
│ 腐烂  62%                  │   ← rot
│ 放置  3 小时 12 分           │   ← now - lastCaredAt
│ 每日  已逾期 1 天            │   ← 仅每日实例显示
└────────────────────────────┘
```

### 14.3 行为细节

- 驻留计时**按生物个体**归零：鼠标从 A 划到 B 要重新数 3 秒。
- 已经弹出后，只要还停在**同一只**身上就保持显示；一旦移开立刻收起。
- 生物在移动，所以命中判定要用**当帧位置**，并且允许它小幅漂移 ——
  推荐"跟随"策略：气泡位置每 100ms 更新到光标旁，而不是固定在弹出点。
- 暂停（`running = false`）时生物不动，悬停依然有效。
- Android 没有悬停：等价交互是**长按 600ms**（仅在关闭穿透模式下可达）。

### 14.4 验收标准

1. 鼠标停在某只虫上 2.9 秒 → 不弹。
2. 停满 3 秒 → 弹出，且标题与那只虫对应的任务一致。
3. 从 A 直接划到 B 并停 3 秒 → 只显示 B。
4. 移开 → 立刻收起，不残留。
5. 弹出时**不影响**鼠标下方的其它程序正常接收点击。

---

## 15. 每周任务（v1.2 新增）

### 15.1 语义

与每日任务（§12）**完全同构**，只是周期换成「周」，并且可以指定**星期几**生成。

```
模板：「写周报」  weekday = 5（周五）
   ↓ 每周五
写周报 · W37   ← 本周，rot 从 0 开始烂
写周报 · W36   ← 上周没写，它还在烂，甚至已经是苍蝇了
```

### 15.2 数据结构

```
WeeklyTemplate
  id        : String   // UUID
  title     : String
  note      : String
  enabled   : Boolean  // = true
  createdAt : Long
  sortOrder : Int      // = 0
  weekday   : Int      // 1=周一 … 7=周日（ISO-8601）。实例在这一天**或之后**生成

TaskItem 新增
  weeklyTemplateId : String?   // 非空 = 这是某个每周任务模板生成的实例
  weeklyKey        : String?   // ISO 周，形如 "2026-W37"
```

MaggotConfig 新增：

```
  weeklyEnabled : Boolean = true
```

### 15.3 周键与星期

- **周键**用 ISO-8601 周：`YYYY-Www`（周从**周一**开始，第 1 周是含 1 月 4 日的那一周）。
  必须用「周基准年」（week-based-year），不是普通年份 —— 否则跨年那一周会算错
  （例如 2027-01-01 可能属于 2026-W53）。
  - Kotlin: `date.get(WeekFields.ISO.weekBasedYear())` + `date.get(WeekFields.ISO.weekOfWeekBasedYear())`
    → `"%d-W%02d".format(year, week)`
    ⚠️ **不要**用 `DateTimeFormatter.ofPattern("YYYY-'W'ww", Locale.ROOT)`。该 API 取的是
    **该 Locale 的周规则**，而 `Locale.ROOT` 并不是 ISO —— 它是「周日为一周第一天、最小天数 1」，
    于是 `2027-01-01` 会被算成 `2027-W01`（正确值是 `2026-W53`），凭空多出一周。
    必须显式用 `WeekFields.ISO`。（两端都必须遵守；这个坑由单元测试抓出。）
  - C#: `ISOWeek.GetYear(dt)` + `ISOWeek.GetWeekOfYear(dt)` → `$"{year}-W{week:D2}"`
- **星期**统一用 ISO 编号 **1=周一 … 7=周日**。
  - Kotlin: `LocalDate.dayOfWeek.value`（本来就是 1..7）
  - C#: `(int)dt.DayOfWeek` 是 0=周日..6=周六，**必须转换**：
    `dow == DayOfWeek.Sunday ? 7 : (int)dow`

### 15.4 生成规则

每次 tick（或至少每 60 秒）调用一次：

```
EnsureWeeklyTasks(now):
    if (!config.weeklyEnabled) return 0
    today = 设备本地日期
    wd    = ISO 星期几（1..7）
    key   = ISO 周键 today              // "2026-W37"

    for (tpl in weeklyTemplates，按 sortOrder 排序):
        if (!tpl.enabled) continue
        if (wd < tpl.weekday) continue                      // 还没到该生成的那天
        if (存在 weeklyTemplateId == tpl.id 且 weeklyKey == key): continue
        新建任务：
            title            = "<tpl.title> · W<ww>"        // ww 取 key 的后两位
            note             = tpl.note
            weeklyTemplateId = tpl.id
            weeklyKey        = key
            generation = 0, gene = 1.0, rot = 0, status = TODO
            createdAt = now, lastCaredAt = now
        发出 WeeklySpawned 事件
```

**幂等**：同一周内无论调用多少次，每个模板最多一个实例。
**迟到也补**：`weekday = 1`（周一）但用户周三才打开应用，周三照样生成 —— 不跳过这一周。

### 15.5 生命周期不变式

与 §12.3 逐条相同：每周实例在生命周期上**与普通任务没有任何区别** ——
会腐烂、会变苍蝇、会参与繁殖（后代是普通任务）、可被照看 / 拍死。
**漏掉的每周实例不会自动消失**：「上周的周报没写」必须一直挂着。
逾期在界面上显示为 **「已逾期 N 周」**。

模板管理（新增 / 停用 / 删除）语义与 §12.4 逐条相同。

### 15.6 验收标准

1. 建一个 `weekday = 今天` 的模板 → **立刻**出现实例，标题形如 `写周报 · W37`，`rot == 0`。
2. 建一个 `weekday` 晚于今天的模板 → **不生成**（还没到那天）。
3. 同一周内反复调用 100 次 → 实例数量恒为 1。
4. 换到下一周的 key → 变成 2 个实例，且**上一周那个的 `rot` 没有被重置**。
5. 停用模板 → 不生成新实例；旧实例照常腐烂。
6. 删除模板 → 实例还在，仍能正常完成 / 变成苍蝇。
7. 每周实例变苍蝇后繁殖 → 子代 `weeklyTemplateId == null`。
8. `weeklyEnabled = false` → 一个实例都不生成。
9. **跨年周键**：`2026-12-31` 与 `2027-01-01` 若落在同一 ISO 周，周键必须相同；
   下一周才换键。（这是最容易写错的一条。）
10. 每日任务与每周任务**互不影响**：同一天可以同时生成两种实例，各自幂等。
