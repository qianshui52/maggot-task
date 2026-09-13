package com.maggot.task

import android.content.Context
import com.maggot.task.audio.SfxPlayer
import com.maggot.task.core.DailyClock
import com.maggot.task.core.DailyTemplate
import com.maggot.task.core.EngineEvent
import com.maggot.task.core.LifecycleEngine
import com.maggot.task.core.MaggotConfig
import com.maggot.task.core.Stage
import com.maggot.task.core.TaskItem
import com.maggot.task.core.TaskStatus
import com.maggot.task.core.WeekClock
import com.maggot.task.core.WeeklyTemplate
import com.maggot.task.data.TaskStore
import com.maggot.task.diag.CrashLog
import com.maggot.task.notify.Notifier
import com.maggot.task.render.SwarmSimulation
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext

/** Compose 需要不可变快照才能正确重组；这里就是那份快照。 */
data class UiSnapshot(
    val tasks: List<TaskItem> = emptyList(),
    val dailies: List<DailyTemplate> = emptyList(),
    val todayKey: String = "",
    val weekKey: String = "",
    val weeklies: List<WeeklyTemplate> = emptyList(),
    val stats: LifecycleEngine.Stats = LifecycleEngine.Stats(0, 0, 0, 0, 0, 0f),
    val config: MaggotConfig = MaggotConfig(),
    val millisToNextFly: Long = Long.MAX_VALUE,
    val maxGeneration: Int = 0,
    val bredTotal: Int = 0,
    val booted: Boolean = false,
    val swarm: SwarmDiag = SwarmDiag(),
    /**
     * 冷启动失败的原因（非空表示"状态没恢复成功，界面是空壳"）。
     *
     * 以前这里没有这个字段，因为冷启动失败会直接让进程崩掉 —— 那种情况下
     * 界面根本活不到显示错误。现在启动失败会降级，于是必须有个地方把它说出来。
     */
    val startupError: String = "",
    /** 冷启动耗时（毫秒）。超过 1 秒就说明主线程被阻塞得太久了。 */
    val bootMillis: Long = 0L,
)

/**
 * 虫群上桌的"体检报告"。
 *
 * 存在的理由很直接：悬浮窗是**另一个进程层面**才看得见的东西，用户在应用里
 * 完全无从判断"虫子去哪了"。把这些数字搬到控制台上，一张截图就能定位到
 * 是窗口没建起来、还是视口算错了、还是根本没在跑。
 */
data class SwarmDiag(
    /** 全屏虫群层是否成功挂到 WindowManager 上。 */
    val overlayOk: Boolean = false,
    /** 失败原因（异常类名 + message）。空字符串 = 没有失败。 */
    val overlayError: String = "",
    /** 虫群层真正测量到的视口，像素。0×0 表示还没 layout。 */
    val viewportW: Int = 0,
    val viewportH: Int = 0,
    /** 当前模拟里的生物数量；和"活着的任务数"不一致就说明同步出了问题。 */
    val creatures: Int = 0,
    /** 引擎里"活着"（非 DONE）的任务数量。[creatures] 应当跟它一致。 */
    val aliveTasks: Int = 0,
    /** 服务渲染循环的帧计数。它不动 = 时钟停了。 */
    val frames: Long = 0L,
    /** 第一只生物的坐标，用来判断是不是全被堆在角落。 */
    val firstAt: String = "-",
    /** 系统是否关闭了动效（开发者选项 / 省电模式里的「减少动态效果」）。 */
    val motionOff: Boolean = false,
) {
    val degenerateViewport: Boolean get() = viewportW <= 8 || viewportH <= 8

    /**
     * ⚠️ 这里**必须**把 [creatures] 也纳入判断。
     *
     * 只看「窗口挂上了 + 视口量到了 + 帧在跑」是不够的 —— 这三条全满足、而模拟里
     * 一只生物都没有时，屏幕同样是空的。之前正是因为漏了这一条，自检报了
     * 「悬浮层正常」，用户屏幕上却什么都没有，白跑一轮。
     */
    val healthy: Boolean
        get() = overlayOk && !degenerateViewport && frames > 0 &&
            (aliveTasks == 0 || creatures > 0)

    /** 引擎里有活着的虫、模拟里却没有 —— 同步环节坏了。 */
    val mismatched: Boolean get() = aliveTasks > 0 && creatures == 0
}

/**
 * 全应用唯一的依赖容器 + 唯一的模拟时钟。
 *
 * 关键设计：**引擎只在主线程 tick**，UI 快照以约 6Hz 发布。
 * 生物动画由 [com.maggot.task.service.SwarmService] 的 60fps 循环单独驱动，
 * 不会让 Compose 每帧重组整张任务列表。
 */
class AppGraph(context: Context) {

    private val appContext = context.applicationContext
    val store = TaskStore(appContext)
    val engine = LifecycleEngine()
    val notifier = Notifier(appContext)
    val sfx = SfxPlayer()

    /**
     * **悬浮层用的那一份生物模拟。**
     *
     * 位置推进只发生在 [tick] 里，谁掌握时钟谁就顺便把虫群往前推 ——
     * 于是永远不会出现"两个时钟各自推进、虫子分身"的问题。
     */
    val simulation = SwarmSimulation()

    /**
     * 控制台兜底层用的**另一份**模拟。
     *
     * 为什么不共用一份：两个图层的视口不一样（悬浮层是整块屏幕，
     * 控制台的内容区不含状态栏和导航栏）。共用一份的话，谁最后 layout
     * 谁就把坐标系统改掉，另一边的虫子会莫名其妙被挤到一边去。
     *
     * 只在兜底层**真的显示**时才推进 —— 不该为了一个看不见的图层每帧算一遍。
     */
    val fallbackSimulation = SwarmSimulation()

    /** 兜底层是否正在显示，由控制台维护。 */
    @Volatile
    var fallbackVisible: Boolean = false

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val _ui = MutableStateFlow(UiSnapshot())
    val ui: StateFlow<UiSnapshot> = _ui.asStateFlow()

    private var saveJob: Job? = null
    private var lastPublishAt = 0L
    private var lastSaveAt = 0L
    private var bredTotal = 0
    private var lastDailyKey = ""
    private var lastDailyCheckAt = 0L
    private var lastWeeklyKey = ""
    private var lastWeeklyCheckAt = 0L

    /** 监听事件的钩子，由 Service 挂上来做动画/通知。 */
    var onEvent: ((EngineEvent) -> Unit)? = null

    /**
     * 悬浮服务是否正在驱动时钟。
     *
     * 时钟必须**有且只有一个主人**：服务在前台时由服务驱动（60fps，虫群要动），
     * 服务没开时由任务控制台驱动（20fps，足够更新腐烂条）。
     * 否则要么双重推进（腐烂速度翻倍），要么完全不走（应用看起来像卡死了）。
     */
    @Volatile
    var serviceActive: Boolean = false

    /**
     * 系统是否允许动画（"减少动态效果"关闭时为 false）。
     *
     * 由悬浮服务每 5 秒重读一次系统设置，[tick] 再把它转交给 [simulation]。
     * 之前这个值只写到了 View 上、没写到模拟里，导致"静止"模式下虫子照样乱爬。
     */
    @Volatile
    var motionEnabled: Boolean = true

    // ────────────────────────── 虫群体检数据（只读给 UI 看）──────────────────────────

    @Volatile private var swarmOverlayOk = false
    @Volatile private var swarmOverlayError = ""
    @Volatile private var swarmViewportW = 0
    @Volatile private var swarmViewportH = 0
    @Volatile private var swarmFrames = 0L

    /** 由 [com.maggot.task.service.SwarmService] 汇报：虫群层挂载结果。 */
    fun reportOverlay(ok: Boolean, error: String) {
        swarmOverlayOk = ok
        swarmOverlayError = error
    }

    /** 由虫群层汇报：真实测量到的视口。 */
    fun reportViewport(w: Int, h: Int) {
        swarmViewportW = w
        swarmViewportH = h
    }

    /** 供悬浮服务自检用：虫群层当前报上来的视口。 */
    val swarmViewport: Pair<Int, Int> get() = swarmViewportW to swarmViewportH

    /**
     * 服务停止：悬浮层已经不在了。**保留**失败原因，方便下次排错。
     *
     * [reason] 是"没有失败原因的停止"兜底用语 —— 服务被系统/用户正常停掉时，
     * 体检卡不该只甩一句「未上桌」却什么都不解释。
     */
    fun markOverlayDown(reason: String = "") {
        swarmOverlayOk = false
        if (swarmOverlayError.isEmpty() && reason.isNotEmpty()) swarmOverlayError = reason
    }

    /** 由服务的渲染循环汇报：又画了一帧。 */
    fun reportFrame() {
        swarmFrames++
    }

    private fun swarmDiag(): SwarmDiag = SwarmDiag(
        overlayOk = swarmOverlayOk,
        overlayError = swarmOverlayError,
        viewportW = swarmViewportW,
        viewportH = swarmViewportH,
        creatures = simulation.count(),
        aliveTasks = engine.tasks.count { it.status != TaskStatus.DONE },
        frames = swarmFrames,
        firstAt = simulation.debugLine(),
        motionOff = !motionEnabled,
    )

    // ────────────────────────── 启动 ──────────────────────────

    /**
     * 冷启动恢复。
     *
     * ⚠️ 必须是**同步**的。`Application.onCreate` 一返回，任何组件都可能立刻来读
     * [engine] —— 尤其是 [com.maggot.task.service.SwarmService]，它在 `onCreate` 里
     * 就建窗口、建模拟、报体检数据。原来是 `scope.launch` 异步加载，于是存在一个窗口期：
     * 悬浮服务已经起来了、engine 还是空的 —— 模拟里一只生物都没有，屏幕上什么都没有，
     * 而 Toast 恰好会说「虫群已上桌 · 0 只」。
     *
     * 任务量在几百条以内，同步读一次 SQLite 的成本可以接受；退出路径上的
     * [saveBlocking] 用的也是同样的同步策略。
     */
    /**
     * 冷启动恢复（**不会抛异常**）。
     *
     * 这一层是从 `Application.onCreate` 直接调用的，所以它必须"要么成功、要么降级"，
     * 绝不能把异常往上抛 —— 那等于进程直接死，用户看到的现象就是
     * 「点进去就闪退 / 一片黑」，而且拿不到任何线索。
     *
     * 失败时的语义是：状态没恢复成功，但控制台照样能打开，并在顶部把原因写出来。
     */
    fun bootstrapSafely() {
        if (_ui.value.booted) return

        val startedAt = System.currentTimeMillis()
        try {
            bootstrap()
            lastStartupError = null
        } catch (t: Throwable) {
            lastStartupError = t
            CrashLog.recordNonFatal(appContext, "bootstrap", t)
        } finally {
            bootMillis = System.currentTimeMillis() - startedAt
            // 成败都要发布：失败时界面仍然要能打开，并把原因显示出来，
            // 而不是停在"永远没加载完"的空壳上。
            runCatching { publish(force = true) }
        }
    }

    /** 冷启动失败的原因；null = 正常。 */
    @Volatile
    var lastStartupError: Throwable? = null
        private set

    /** 冷启动耗时，毫秒。 */
    @Volatile
    var bootMillis: Long = 0L
        private set

    private fun bootstrap() {
        val loaded = runBlocking { store.loadTasks() }
        val loadedDailies = runBlocking { store.loadDailyTemplates() }
        val loadedWeeklies = runBlocking { store.loadWeeklyTemplates() }
        val cfg = store.loadConfig()
        engine.replaceAll(loaded, cfg, loadedDailies, loadedWeeklies)

        // 规格第 8 节：离开期间照样烂，但最多补 45 分钟。
        val lastSeen = store.lastSeenAt()
        val now = System.currentTimeMillis()
        if (lastSeen > 0L && loaded.isNotEmpty()) {
            engine.applyOfflineDecay(now - lastSeen, now)
        }
        engine.seedStageBaseline()
        engine.drainEvents()

        // 冷启动时把"今天该有的每日任务"补齐（幂等，跨天也会在这里补上）。
        ensureDailyIfNeeded(now, force = true)
        ensureWeeklyIfNeeded(now, force = true)
        engine.drainEvents()

        // 冷启动时不逐条轰炸，只发一条汇总。
        // 单独兜一层：通知渠道/权限在部分 ROM 上会抛，但它绝不该让整次冷启动失败。
        runCatching {
            val flies = engine.countOf(Stage.FLY)
            if (flies > 0 && engine.config.notifyOnFly) notifier.notifyFlySummary(flies)
        }
    }

    /**
     * 规格 §12.2 的调度：幂等，所以这里只是"多久检查一次"的问题。
     * 跨天必须立刻生效，所以日期键一变就强制检查一次。
     */
    private fun ensureDailyIfNeeded(now: Long, force: Boolean = false) {
        val key = DailyClock.todayKey()
        if (!force && key == lastDailyKey && now - lastDailyCheckAt < DAILY_CHECK_INTERVAL_MS) return
        lastDailyKey = key
        lastDailyCheckAt = now
        engine.ensureDailyTasks(key, now)
    }

    /**
     * 规格 §15.4 的调度。与每日任务同构，但**周键 + 星期**都要参与判断：
     * 周键变了（跨周）或到达了模板指定的星期，都必须立刻检查一次。
     */
    private fun ensureWeeklyIfNeeded(now: Long, force: Boolean = false) {
        val key = WeekClock.weekKey()
        if (!force && key == lastWeeklyKey && now - lastWeeklyCheckAt < DAILY_CHECK_INTERVAL_MS) return
        lastWeeklyKey = key
        lastWeeklyCheckAt = now
        engine.ensureWeeklyTasks(key, WeekClock.isoWeekday(), now)
    }

    // ────────────────────────── 时钟 ──────────────────────────

    /** 由 Service 的渲染循环或前台界面调用。 */
    fun tick(dtSeconds: Float, now: Long = System.currentTimeMillis()) {
        engine.tick(dtSeconds, now)
        ensureDailyIfNeeded(now)
        ensureWeeklyIfNeeded(now)
        consumeEvents()
        // 虫群的位置推进和腐烂走同一个时钟。只有这一处调用 advance，
        // 所以既不会"两个时钟互相打架"，也不会"谁也忘了推进、虫子冻在屏幕上"。
        simulation.motionEnabled = motionEnabled
        simulation.advance(dtSeconds, engine)
        if (fallbackVisible) {
            fallbackSimulation.motionEnabled = motionEnabled
            fallbackSimulation.advance(dtSeconds, engine)
        }
        // UI 快照限流，避免每帧重组整张列表。
        if (now - lastPublishAt >= UI_PUBLISH_INTERVAL_MS) {
            publish()
        }
        maybeAutoSave(now)
    }

    private fun consumeEvents() {
        val events = engine.drainEvents()
        if (events.isEmpty()) return
        val sound = engine.config.soundEnabled
        for (e in events) {
            when (e) {
                is EngineEvent.BecameFly -> {
                    if (sound) sfx.buzz()
                    if (engine.config.notifyOnFly) {
                        engine.find(e.taskId)?.let { notifier.notifyBecameFly(it, it.generation) }
                    }
                }
                is EngineEvent.ApproachingFly -> {
                    if (engine.config.notifyOnFly) {
                        engine.find(e.taskId)?.let { notifier.notifyApproaching(it) }
                    }
                }
                is EngineEvent.Hatched -> if (sound) sfx.hatch()
                // 今天的新虫子出现了 —— 用孵化音提示一下。
                is EngineEvent.DailySpawned -> if (sound) sfx.hatch()
                // 本周的新虫子出现了 —— 同样用孵化音。
                is EngineEvent.WeeklySpawned -> if (sound) sfx.hatch()
                is EngineEvent.Bred -> {
                    bredTotal++
                    if (sound) sfx.breed()
                    if (engine.config.notifyOnFly) {
                        notifier.notifyBred(e.title, engine.countOf(Stage.FLY))
                    }
                }
                // 「拍死它」的解压感有一半来自这一声。
                is EngineEvent.Squashed -> if (sound) sfx.splat()
                is EngineEvent.Pupated -> Unit
            }
            onEvent?.invoke(e)
        }
    }

    fun publish(force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (!force && now - lastPublishAt < UI_PUBLISH_INTERVAL_MS) return
        lastPublishAt = now
        _ui.value = UiSnapshot(
            tasks = engine.snapshotForPersist(),
            dailies = engine.dailySnapshotForPersist(),
            todayKey = DailyClock.todayKey(),
            weekKey = WeekClock.weekKey(),
            weeklies = engine.weeklySnapshotForPersist(),
            stats = engine.stats(),
            config = engine.config.copy(),
            millisToNextFly = engine.millisUntilNextFly(),
            maxGeneration = engine.tasks.maxOfOrNull { it.generation } ?: 0,
            bredTotal = bredTotal,
            booted = true,
            swarm = swarmDiag(),
            startupError = lastStartupError?.let { describeError(it) } ?: "",
            bootMillis = bootMillis,
        )
    }

    /** 冷启动失败后，用户按过"我知道了"就把它收起来，别再挡住界面。 */
    fun clearStartupError() {
        lastStartupError = null
        CrashLog.clearNonFatal(appContext)
        publish(force = true)
    }

    private fun describeError(t: Throwable): String =
        "${t.javaClass.name}: ${t.message ?: "(无 message)"}\n" +
            t.stackTrace.take(6).joinToString("\n") { "    at $it" }

    // ────────────────────────── 持久化 ──────────────────────────

    private fun maybeAutoSave(now: Long) {
        if (now - lastSaveAt < AUTO_SAVE_INTERVAL_MS) return
        lastSaveAt = now
        requestSave()
    }

    /** 防抖落盘：密集操作（连点"我去做了"）只会写一次。 */
    fun requestSave() {
        saveJob?.cancel()
        saveJob = scope.launch {
            delay(SAVE_DEBOUNCE_MS)
            saveNow()
        }
    }

    suspend fun saveNow() = withContext(Dispatchers.IO) {
        // engine.config 与任务集合都归主线程所有，所以先把要写的数据取成快照，
        // 再切回 IO 落盘。
        val snap = withContext(Dispatchers.Main.immediate) {
            store.saveConfig(engine.config)
            store.markSeen()
            Triple(
                engine.snapshotForPersist(),
                engine.dailySnapshotForPersist(),
                engine.weeklySnapshotForPersist(),
            )
        }
        store.saveTasks(snap.first)
        store.saveDailyTemplates(snap.second)
        store.saveWeeklyTemplates(snap.third)
    }

    fun saveBlocking() {
        val tasks = engine.snapshotForPersist()
        val dailies = engine.dailySnapshotForPersist()
        val weeklies = engine.weeklySnapshotForPersist()
        store.saveConfig(engine.config)
        store.markSeen()
        // 退出路径上没时间等协程，直接同步写。任务量很小，可接受。
        kotlinx.coroutines.runBlocking {
            store.saveTasks(tasks)
            store.saveDailyTemplates(dailies)
            store.saveWeeklyTemplates(weeklies)
        }
    }

    // ────────────────────────── 动作封装（统一触发落盘 + 刷新） ──────────────────────────

    fun addTask(title: String, note: String = ""): TaskItem? =
        engine.addTask(title, note).also { if (it != null) afterMutation() }

    fun care(id: String) = engine.care(id).also { afterMutation() }

    fun complete(id: String) = engine.complete(id).also { afterMutation() }

    fun setStatus(id: String, status: TaskStatus) = engine.setStatus(id, status).also { afterMutation() }

    fun edit(id: String, title: String, note: String) = engine.edit(id, title, note).also { afterMutation() }

    fun delete(id: String) = engine.delete(id).also { afterMutation() }

    fun resetAll() {
        engine.resetAll()
        afterMutation()
    }

    fun clearAll() {
        engine.clearAll()
        bredTotal = 0
        scope.launch { store.clearTasks() }
        afterMutation()
    }

    fun updateConfig(block: (MaggotConfig) -> Unit) {
        block(engine.config)
        afterMutation()
    }

    // ────────────────────────── 每日任务（规格 §12）──────────────────────────

    fun addDailyTemplate(title: String): DailyTemplate? =
        engine.addDailyTemplate(title).also { afterMutation() }

    fun setDailyTemplateEnabled(id: String, enabled: Boolean) =
        engine.setDailyEnabled(id, enabled).also { afterMutation() }

    fun removeDailyTemplate(id: String) =
        engine.removeDailyTemplate(id).also { afterMutation() }

    /** 今日每日任务的完成情况，面板上显示"2/3 已拍死"。 */
    fun todayDailyProgress(): Pair<Int, Int> {
        val today = engine.todayDailyInstances()
        val done = today.count { it.status == TaskStatus.DONE }
        return done to today.size
    }

    // ────────────────────────── 每周任务（规格 §15）──────────────────────────

    fun addWeeklyTemplate(title: String, weekday: Int): WeeklyTemplate? =
        engine.addWeeklyTemplate(title, weekday).also { afterMutation() }

    fun setWeeklyTemplateEnabled(id: String, enabled: Boolean) =
        engine.setWeeklyEnabled(id, enabled).also { afterMutation() }

    fun setWeeklyTemplateWeekday(id: String, weekday: Int) =
        engine.setWeeklyWeekday(id, weekday).also { afterMutation() }

    fun removeWeeklyTemplate(id: String) =
        engine.removeWeeklyTemplate(id).also { afterMutation() }

    /** 本周每周任务的完成情况。 */
    fun thisWeekProgress(): Pair<Int, Int> {
        val week = engine.thisWeekInstances()
        val done = week.count { it.status == TaskStatus.DONE }
        return done to week.size
    }

    private fun afterMutation() {
        consumeEvents()
        publish(force = true)
        requestSave()
    }

    /** 面板上那句倒计时文案。 */
    fun countdownText(): String {
        val ms = engine.millisUntilNextFly()
        if (engine.countOf(Stage.FLY) > 0) return "已经失控"
        if (ms == Long.MAX_VALUE) return "暂无威胁"
        val totalMin = ms / 60000
        return when {
            totalMin >= 60 -> "${totalMin / 60} 小时 ${totalMin % 60} 分"
            totalMin >= 1 -> "$totalMin 分钟"
            else -> "不到 1 分钟"
        }
    }

    /**
     * 长按生物时展示的任务信息。
     * 移动端对应桌面版的「鼠标悬停 3 秒」（规格 §14.2 的同一份字段）。
     */
    fun describeTask(task: TaskItem): String {
        val stage = engine.stageOf(task)
        val rotPct = (task.rot.coerceIn(0f, 1f) * 100).toInt()
        val idleMin = ((System.currentTimeMillis() - task.lastCaredAt) / 60000L).coerceAtLeast(0L)
        val idle = when {
            idleMin < 1 -> "刚刚管过"
            idleMin < 60 -> "$idleMin 分钟"
            else -> "${idleMin / 60} 小时 ${idleMin % 60} 分"
        }
        val overdue = task.overdueDays(DailyClock.todayKey())

        return buildString {
            append(task.title).append('\n')
            append("阶段　").append(stage.zh).append('\n')
            append("腐烂　").append(rotPct).append("%\n")
            append("放置　").append(idle)
            when {
                overdue > 0 -> append("\n每日　已逾期 ").append(overdue).append(" 天")
                task.isDaily -> append("\n每日　今天")
            }
        }
    }

    companion object {
        const val UI_PUBLISH_INTERVAL_MS = 160L
        private const val SAVE_DEBOUNCE_MS = 900L
        private const val AUTO_SAVE_INTERVAL_MS = 5000L
        /** 每日任务的检查间隔。跨天时日期键变化会立刻触发，不受这个值限制。 */
        private const val DAILY_CHECK_INTERVAL_MS = 60_000L
    }
}
