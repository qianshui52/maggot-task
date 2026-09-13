package com.maggot.task.core

import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.random.Random

/**
 * 变态发育引擎 —— 产品的全部"性格"都在这里。
 *
 * 纯 Kotlin、零 Android 依赖，因此可以直接跑 JVM 单元测试。
 * Windows 端有一份逐行等价的 C# 实现（`docs/lifecycle-spec.md` 是共同契约）。
 *
 * **线程约定：本类不是线程安全的，只能在主线程访问。**
 * 需要落盘时调用 [snapshotForPersist] 在主线程取深拷贝，再把拷贝交给 IO 线程。
 */
class LifecycleEngine(
    var config: MaggotConfig = MaggotConfig(),
    val tasks: MutableList<TaskItem> = mutableListOf(),
    val dailies: MutableList<DailyTemplate> = mutableListOf(),
    val weeklies: MutableList<WeeklyTemplate> = mutableListOf(),
    private val random: Random = Random.Default,
) {

    /** 上一次 tick 时每只虫子的阶段，用来检测"跃迁"并只发一次事件。 */
    private val stageCache = HashMap<String, Stage>()
    private val pending = ArrayDeque<EngineEvent>()
    private var breedAccum = 0f

    // ────────────────────────────── 查询 ──────────────────────────────

    fun stageOf(task: TaskItem): Stage = Lifecycle.stageOf(task)

    fun find(id: String): TaskItem? = tasks.firstOrNull { it.id == id }

    fun countOf(stage: Stage): Int = tasks.count { Lifecycle.stageOf(it) == stage }

    fun flies(): List<TaskItem> = tasks.filter { Lifecycle.stageOf(it) == Stage.FLY }

    fun aliveTasks(): List<TaskItem> = tasks.filter { it.status != TaskStatus.DONE }

    /** 全部未完成任务的平均腐烂度，0..1.2。控制面板的"整体压力"就是它。 */
    fun averageRot(): Float {
        val alive = aliveTasks()
        if (alive.isEmpty()) return 0f
        return alive.sumOf { it.rot.toDouble() }.toFloat() / alive.size
    }

    /** 距离最近的"变苍蝇"还有多久，用于面板上那句让人心慌的倒计时。 */
    fun millisUntilNextFly(): Long =
        aliveTasks().minOfOrNull { Lifecycle.millisUntilFly(it, config) } ?: Long.MAX_VALUE

    // ────────────────────────────── 变更 ──────────────────────────────

    /**
     * 用户亲手创建任务。**不受 maxTasks 限制** —— 那是给繁殖用的安全阀，
     * 不该拿来拒绝用户自己的输入。
     */
    fun addTask(
        title: String,
        note: String = "",
        now: Long = System.currentTimeMillis(),
        parentId: String? = null,
        generation: Int = 0,
        dailyTemplateId: String? = null,
        dailyKey: String? = null,
        weeklyTemplateId: String? = null,
        weeklyKey: String? = null,
    ): TaskItem? {
        val clean = title.trim()
        if (clean.isEmpty()) return null
        val task = TaskItem(
            title = clean.take(80),
            note = note.take(400),
            createdAt = now,
            lastCaredAt = now,
            generation = generation,
            parentId = parentId,
            gene = Lifecycle.geneFor(generation),
            dailyTemplateId = dailyTemplateId,
            dailyKey = dailyKey,
            weeklyTemplateId = weeklyTemplateId,
            weeklyKey = weeklyKey,
        )
        tasks += task
        stageCache[task.id] = Lifecycle.stageOf(task)
        return task
    }

    /** 规格第 5 节。冷却只衰减收益，不阻止点击 —— 任何时候点都有反馈。 */
    fun care(id: String, now: Long = System.currentTimeMillis()): CareResult {
        val task = find(id) ?: return CareResult(applied = false)
        if (task.status == TaskStatus.DONE) return CareResult(applied = false)

        val sinceSec = ((now - task.lastCaredAt).coerceAtLeast(0L)) / 1000f
        val factor = Lifecycle.CARE_MIN_FACTOR +
            (1f - Lifecycle.CARE_MIN_FACTOR) *
            (sinceSec / Lifecycle.CARE_COOLDOWN_SEC).coerceIn(0f, 1f)
        val relief = Lifecycle.CARE_RELIEF * factor

        task.rot = (task.rot - relief).coerceAtLeast(0f)
        task.lastCaredAt = now
        task.status = TaskStatus.DOING
        val gain = Lifecycle.CARE_PROGRESS_MIN +
            ((Lifecycle.CARE_PROGRESS_MAX - Lifecycle.CARE_PROGRESS_MIN) * factor).roundToInt()
        task.progress = (task.progress + gain).coerceAtMost(100)

        refreshStages()
        return CareResult(applied = true, relief = relief, factor = factor, progressGain = gain)
    }

    /** 拍死它。 */
    fun complete(id: String, now: Long = System.currentTimeMillis()): Boolean {
        val task = find(id) ?: return false
        if (task.status == TaskStatus.DONE) return false
        task.status = TaskStatus.DONE
        task.progress = 100
        task.completedAt = now
        pending += EngineEvent.Squashed(task.id, task.title)
        trimSquashed()
        refreshStages()
        return true
    }

    fun setStatus(id: String, status: TaskStatus, now: Long = System.currentTimeMillis()): Boolean {
        val task = find(id) ?: return false
        if (task.status == TaskStatus.DONE && status != TaskStatus.DONE) return false
        task.status = status
        if (status == TaskStatus.DOING) task.lastCaredAt = now
        refreshStages()
        return true
    }

    fun edit(id: String, title: String, note: String): Boolean {
        val task = find(id) ?: return false
        val clean = title.trim()
        if (clean.isEmpty()) return false
        task.title = clean.take(80)
        task.note = note.take(400)
        return true
    }

    fun delete(id: String): Boolean {
        // 删除不是"拍死"，所以不发 Squashed 事件 —— 音效和爆浆只属于真正的完成。
        val removed = tasks.removeAll { it.id == id }
        if (removed) stageCache.remove(id)
        return removed
    }

    /** 重置：腐烂度归零，任务全留着。拍死过的不复活。 */
    fun resetAll(now: Long = System.currentTimeMillis()) {
        for (task in tasks) {
            if (task.status == TaskStatus.DONE) continue
            task.rot = 0f
            task.lastCaredAt = now
        }
        refreshStages()
    }

    /** 清空：全没了。 */
    fun clearAll() {
        tasks.clear()
        stageCache.clear()
        breedAccum = 0f
    }

    // ────────────────────────────── 每日任务（规格 §12）──────────────────────────────

    /** 新增模板，并**立刻**生成今天的实例 —— 用户点完就该看到虫子出现。 */
    fun addDailyTemplate(
        title: String,
        note: String = "",
        now: Long = System.currentTimeMillis(),
    ): DailyTemplate? {
        val clean = title.trim()
        if (clean.isEmpty()) return null
        val tpl = DailyTemplate(
            title = clean.take(60),
            note = note.take(400),
            createdAt = now,
            sortOrder = (dailies.maxOfOrNull { it.sortOrder } ?: -1) + 1,
        )
        dailies += tpl
        if (config.dailyEnabled) ensureDailyTasks(DailyClock.todayKey(), now)
        return tpl
    }

    /** 停用模板：不再生成新实例，**已有实例不受影响**（删虫子要有明确动作）。 */
    fun setDailyEnabled(templateId: String, enabled: Boolean): Boolean {
        val tpl = dailies.firstOrNull { it.id == templateId } ?: return false
        tpl.enabled = enabled
        return true
    }

    /**
     * 删除模板。**已有实例保留**，只是 `dailyTemplateId` 变成孤儿 ——
     * 它们退化成普通任务继续腐烂，不会凭空消失。
     */
    fun removeDailyTemplate(templateId: String): Boolean =
        dailies.removeAll { it.id == templateId }

    /**
     * 规格 §12.2。**幂等**：同一天无论调用多少次，每个模板最多只有一个实例。
     *
     * @return 本次实际生成的实例数量。
     */
    fun ensureDailyTasks(
        todayKey: String = DailyClock.todayKey(),
        now: Long = System.currentTimeMillis(),
    ): Int {
        if (!config.dailyEnabled) return 0
        var spawned = 0
        for (tpl in dailies.sortedBy { it.sortOrder }) {
            if (!tpl.enabled) continue
            val exists = tasks.any { it.dailyTemplateId == tpl.id && it.dailyKey == todayKey }
            if (exists) continue
            val task = addTask(
                title = "${tpl.title} · ${DailyClock.shortLabel(todayKey)}",
                note = tpl.note,
                now = now,
                dailyTemplateId = tpl.id,
                dailyKey = todayKey,
            ) ?: continue
            pending += EngineEvent.DailySpawned(task.id, task.title)
            spawned++
        }
        return spawned
    }

    /** 今天的每日实例（用于面板上显示"今日进度"）。 */
    fun todayDailyInstances(todayKey: String = DailyClock.todayKey()): List<TaskItem> =
        tasks.filter { it.dailyTemplateId != null && it.dailyKey == todayKey }

    /** 某个模板是否有逾期未完成的实例。 */
    fun hasOverdueDaily(templateId: String, todayKey: String = DailyClock.todayKey()): Boolean =
        tasks.any {
            it.dailyTemplateId == templateId &&
                it.status != TaskStatus.DONE &&
                DailyClock.overdueDays(it.dailyKey, todayKey) > 0
        }

    // ────────────────────────────── 每周任务（规格 §15）──────────────────────────────
    // 与每日任务完全同构，只多一个「星期几」。这里刻意保持结构一致，
    // 两端对照阅读时能一眼看出对应关系。

    /** 新增模板。若今天已经到达该星期，**立刻**生成本周实例。 */
    fun addWeeklyTemplate(
        title: String,
        weekday: Int,
        note: String = "",
        now: Long = System.currentTimeMillis(),
    ): WeeklyTemplate? {
        val clean = title.trim()
        if (clean.isEmpty()) return null
        val tpl = WeeklyTemplate(
            title = clean.take(60),
            note = note.take(400),
            createdAt = now,
            sortOrder = (weeklies.maxOfOrNull { it.sortOrder } ?: -1) + 1,
            weekday = weekday.coerceIn(1, 7),
        )
        weeklies += tpl
        if (config.weeklyEnabled) ensureWeeklyTasks(now = now)
        return tpl
    }

    /** 停用模板：不再生成新实例，已有实例不受影响。 */
    fun setWeeklyEnabled(templateId: String, enabled: Boolean): Boolean {
        val tpl = weeklies.firstOrNull { it.id == templateId } ?: return false
        tpl.enabled = enabled
        return true
    }

    /** 改星期几。只影响以后生成，已有实例不动。 */
    fun setWeeklyWeekday(templateId: String, weekday: Int): Boolean {
        val tpl = weeklies.firstOrNull { it.id == templateId } ?: return false
        tpl.weekday = weekday.coerceIn(1, 7)
        return true
    }

    /** 删除模板。已有实例保留，退化成普通任务继续腐烂。 */
    fun removeWeeklyTemplate(templateId: String): Boolean =
        weeklies.removeAll { it.id == templateId }

    /**
     * 规格 §15.4。**幂等**：同一周内无论调用多少次，每个模板最多一个实例。
     *
     * `weekday` 是「这一天或之后」—— 周一没开电脑、周三才打开，照样补上，
     * 不会因为迟到就跳过这一周。
     *
     * @return 本次实际生成的实例数量。
     */
    fun ensureWeeklyTasks(
        weekKey: String = WeekClock.weekKey(),
        isoWeekday: Int = WeekClock.isoWeekday(),
        now: Long = System.currentTimeMillis(),
    ): Int {
        if (!config.weeklyEnabled) return 0
        var spawned = 0
        for (tpl in weeklies.sortedBy { it.sortOrder }) {
            if (!tpl.enabled) continue
            if (isoWeekday < tpl.weekday) continue          // 还没到该生成的那天
            val exists = tasks.any { it.weeklyTemplateId == tpl.id && it.weeklyKey == weekKey }
            if (exists) continue
            val task = addTask(
                title = "${tpl.title} · ${WeekClock.shortLabel(weekKey)}",
                note = tpl.note,
                now = now,
                weeklyTemplateId = tpl.id,
                weeklyKey = weekKey,
            ) ?: continue
            pending += EngineEvent.WeeklySpawned(task.id, task.title)
            spawned++
        }
        return spawned
    }

    /** 本周的每周实例。 */
    fun thisWeekInstances(weekKey: String = WeekClock.weekKey()): List<TaskItem> =
        tasks.filter { it.weeklyTemplateId != null && it.weeklyKey == weekKey }

    /** 某个模板是否有逾期未完成的实例。 */
    fun hasOverdueWeekly(templateId: String, weekKey: String = WeekClock.weekKey()): Boolean =
        tasks.any {
            it.weeklyTemplateId == templateId &&
                it.status != TaskStatus.DONE &&
                WeekClock.overdueWeeks(it.weeklyKey, weekKey) > 0
        }

    // ────────────────────────────── 时间推进 ──────────────────────────────

    /**
     * 规格第 4 节。`dtSeconds` 必须是真实流逝时间，内部会 clamp 到 [Lifecycle.MAX_DT]，
     * 否则设备休眠回来会一次性把整个虫群推进到苍蝇。
     */
    fun tick(dtSeconds: Float, now: Long = System.currentTimeMillis()) {
        if (dtSeconds <= 0f) return
        val dt = dtSeconds.coerceAtMost(Lifecycle.MAX_DT)

        if (config.running) {
            for (task in tasks) {
                if (task.status == TaskStatus.DONE) continue
                val before = task.rot
                val rate = Lifecycle.rotRatePerSecond(task, config, Lifecycle.stageOf(task))
                task.rot = (task.rot + rate * dt).coerceAtMost(Lifecycle.ROT_MAX)

                if (before < Lifecycle.APPROACH_T && task.rot >= Lifecycle.APPROACH_T &&
                    task.rot < Lifecycle.FLY_T
                ) {
                    pending += EngineEvent.ApproachingFly(task.id, task.title)
                }
            }
            breedStep(dt, now)
        }
        refreshStages()
    }

    /** 规格第 7 节：失控的来源。 */
    private fun breedStep(dt: Float, now: Long) {
        if (!config.breedEnabled) {
            breedAccum = 0f
            return
        }
        val flies = flies()
        // 一只苍蝇不繁殖 —— 这条规则本身就是一个"赶紧处理"的动机。
        // maxFlies 只封顶繁殖，**绝不冻结腐烂**：任务该变苍蝇就一定会变，
        // 这是产品的核心承诺，任何安全阀都不能把它推翻。
        if (flies.size < 2 || flies.size >= config.maxFlies || tasks.size >= config.maxTasks) {
            breedAccum = 0f
            return
        }

        breedAccum += dt * config.rotSpeed
        // 苍蝇越多产卵越快，形成"一窝变十窝"的加速感。
        val interval = config.breedSeconds / (1f + (flies.size - 2) * 0.06f)
        if (breedAccum < interval) return
        breedAccum = 0f

        val a = flies[random.nextInt(flies.size)]
        var b = flies[random.nextInt(flies.size)]
        var guard = 0
        while (b.id == a.id && guard++ < 8) b = flies[random.nextInt(flies.size)]
        if (b.id == a.id) return

        val gen = max(a.generation, b.generation) + 1
        val child = addTask(
            title = "蝇卵 #$gen · ${a.title.ellipsize(10)}",
            now = now,
            parentId = a.id,
            generation = gen,
        ) ?: return
        pending += EngineEvent.Bred(a.id, a.title, b.id, child.id)
    }

    /**
     * 规格第 8 节：关掉应用也躲不掉，但最多补 [Lifecycle.OFFLINE_CAP_MIN] 分钟。
     * 按"一次性大步"推进，不触发繁殖 —— 离开期间虫群只是烂，不会背着你下崽。
     */
    fun applyOfflineDecay(elapsedMillis: Long, now: Long = System.currentTimeMillis()) {
        if (!config.running) return
        val cappedSec = min(
            elapsedMillis.coerceAtLeast(0L) / 1000f,
            Lifecycle.OFFLINE_CAP_MIN * 60f,
        )
        if (cappedSec <= 1f) return

        for (task in tasks) {
            if (task.status == TaskStatus.DONE) continue
            val rate = Lifecycle.rotRatePerSecond(task, config, Lifecycle.stageOf(task))
            task.rot = (task.rot + rate * cappedSec).coerceAtMost(Lifecycle.ROT_MAX)
        }
        refreshStages()
    }

    // ────────────────────────────── 事件 ──────────────────────────────

    fun drainEvents(): List<EngineEvent> {
        if (pending.isEmpty()) return emptyList()
        val out = pending.toList()
        pending.clear()
        return out
    }

    /**
     * 从数据库加载完成后调用：把当前阶段登记为基线，**不发**任何事件。
     * 否则每次冷启动都会因为"LARVA -> LARVA"之类炸出一堆通知。
     */
    fun seedStageBaseline() {
        stageCache.clear()
        for (task in tasks) stageCache[task.id] = Lifecycle.stageOf(task)
        breedAccum = 0f
    }

    /** 复用的临时集合，避免每帧分配。 */
    private val aliveScratch = HashSet<String>()

    /** 只发一次跃迁事件（EGG→LARVA→PUPA→FLY），不重复轰炸。 */
    private fun refreshStages() {
        if (tasks.isEmpty()) {
            stageCache.clear()
            return
        }
        for (task in tasks) {
            val stage = Lifecycle.stageOf(task)
            val prev = stageCache.put(task.id, stage)
            if (prev == null || prev == stage) continue
            when (stage) {
                Stage.LARVA -> pending += EngineEvent.Hatched(task.id, task.title)
                Stage.PUPA -> pending += EngineEvent.Pupated(task.id, task.title)
                Stage.FLY -> pending += EngineEvent.BecameFly(task.id, task.title)
                else -> Unit
            }
        }
        aliveScratch.clear()
        for (task in tasks) aliveScratch += task.id
        stageCache.keys.retainAll(aliveScratch)
    }

    /** 拍死的任务只留最近 [Lifecycle.SQUASHED_KEEP] 条，防止无限膨胀。 */
    private fun trimSquashed() {
        val squashed = tasks.filter { it.status == TaskStatus.DONE }
            .sortedByDescending { it.completedAt ?: 0L }
        if (squashed.size <= Lifecycle.SQUASHED_KEEP) return
        val doomed = squashed.drop(Lifecycle.SQUASHED_KEEP).mapTo(HashSet()) { it.id }
        tasks.removeAll { it.id in doomed }
        stageCache.keys.removeAll(doomed)
    }

    // ────────────────────────────── 持久化 ──────────────────────────────

    /** 主线程调用，返回可安全交给 IO 线程的深拷贝。 */
    fun snapshotForPersist(): List<TaskItem> = tasks.map { it.copy() }

    /** 每日任务模板的深拷贝快照。 */
    fun dailySnapshotForPersist(): List<DailyTemplate> = dailies.map { it.copy() }

    /** 每周任务模板的深拷贝快照。 */
    fun weeklySnapshotForPersist(): List<WeeklyTemplate> = weeklies.map { it.copy() }

    /** 用从磁盘读回的数据替换内存状态（不触发事件）。 */
    fun replaceAll(
        loaded: List<TaskItem>,
        loadedConfig: MaggotConfig?,
        loadedDailies: List<DailyTemplate> = emptyList(),
        loadedWeeklies: List<WeeklyTemplate> = emptyList(),
    ) {
        tasks.clear()
        tasks += loaded
        dailies.clear()
        dailies += loadedDailies
        weeklies.clear()
        weeklies += loadedWeeklies
        if (loadedConfig != null) config = loadedConfig
        seedStageBaseline()
    }

    /** 统计摘要，控制面板与通知都用它。 */
    fun stats(): Stats = Stats(
        egg = countOf(Stage.EGG),
        larva = countOf(Stage.LARVA),
        pupa = countOf(Stage.PUPA),
        fly = countOf(Stage.FLY),
        squashed = countOf(Stage.DEAD),
        averageRot = averageRot(),
    )

    data class Stats(
        val egg: Int,
        val larva: Int,
        val pupa: Int,
        val fly: Int,
        val squashed: Int,
        val averageRot: Float,
    ) {
        val alive: Int get() = egg + larva + pupa + fly
    }
}

/** 中文也算一个字符宽度；超长时截断并加省略号。 */
internal fun String.ellipsize(max: Int): String =
    if (length <= max) this else take(max) + "…"
