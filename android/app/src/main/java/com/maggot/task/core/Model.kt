package com.maggot.task.core

import java.util.UUID

/** 任务本身的状态机。与 [Stage] 不同：status 是人给的，stage 是烂出来的。 */
enum class TaskStatus { TODO, DOING, DONE }

/** 昆虫变态发育阶段。完全由 [TaskItem.rot] + [TaskItem.status] 推导。 */
enum class Stage(val zh: String, val en: String) {
    EGG("卵", "Egg"),
    LARVA("蛆", "Larva"),
    PUPA("蛹", "Pupa"),
    FLY("苍蝇", "Fly"),
    DEAD("已拍死", "Squashed");

    val isAlive: Boolean get() = this != DEAD
    val isInfestation: Boolean get() = this == FLY
}

/**
 * 一个任务 == 一只虫子。
 *
 * 全部时间字段都是 epoch millis。引擎不读系统时钟，`now` 一律由调用方传入，
 * 这样引擎是纯函数式的、可测试的。
 */
data class TaskItem(
    var id: String = UUID.randomUUID().toString(),
    var title: String,
    var note: String = "",
    var status: TaskStatus = TaskStatus.TODO,
    var rot: Float = 0f,
    var progress: Int = 0,
    var createdAt: Long,
    var lastCaredAt: Long,
    var completedAt: Long? = null,
    var generation: Int = 0,
    var parentId: String? = null,
    var gene: Float = 1f,
    /** 非空 = 这是某个每日任务模板生成的实例。 */
    var dailyTemplateId: String? = null,
    /** "YYYY-MM-DD"（设备本地时区）；仅每日实例有值。 */
    var dailyKey: String? = null,
    /** 非空 = 这是某个每周任务模板生成的实例。 */
    var weeklyTemplateId: String? = null,
    /** ISO 周键 "YYYY-Www"；仅每周实例有值。 */
    var weeklyKey: String? = null,
) {
    /** 距离上次被照看过去了多久（毫秒），用来做 UI 文案与腐烂预警。 */
    fun neglectedFor(now: Long): Long = now - lastCaredAt

    val isDaily: Boolean get() = dailyTemplateId != null
    val isWeekly: Boolean get() = weeklyTemplateId != null
    val isRecurring: Boolean get() = isDaily || isWeekly

    /** 每日实例逾期天数；当天或未来为 0。 */
    fun overdueDays(todayKey: String): Int = DailyClock.overdueDays(dailyKey, todayKey)

    /** 每周实例逾期周数；本周或未来为 0。 */
    fun overdueWeeks(weekKey: String): Int = WeekClock.overdueWeeks(weeklyKey, weekKey)
}

/**
 * 每日任务模板。它本身不是任务，而是「每天生成一个新实例」的配方。
 *
 * 实例的生命周期与普通任务**完全一致** —— 漏掉的每日任务会继续烂、
 * 变成苍蝇、参与繁殖。这是刻意的：昨天的债必须一直恶心你。
 */
data class DailyTemplate(
    var id: String = UUID.randomUUID().toString(),
    var title: String,
    var note: String = "",
    var enabled: Boolean = true,
    var createdAt: Long,
    var sortOrder: Int = 0,
)

/**
 * 每周任务模板（规格 §15）。结构与 [DailyTemplate] 同构，只多一个 [weekday]。
 *
 * 实例在这一天**或之后**生成 —— 周一没开电脑、周三才打开，照样补上，不跳过这一周。
 */
data class WeeklyTemplate(
    var id: String = UUID.randomUUID().toString(),
    var title: String,
    var note: String = "",
    var enabled: Boolean = true,
    var createdAt: Long,
    var sortOrder: Int = 0,
    /** ISO 星期几：1=周一 … 7=周日。 */
    var weekday: Int = 1,
)

/**
 * 全部可调参数。两端（Android / Windows）字段名与默认值必须一致。
 */
data class MaggotConfig(
    var running: Boolean = true,
    var rotSpeed: Float = 1f,
    var baseRotMinutes: Int = 90,
    var breedEnabled: Boolean = true,
    var breedSeconds: Int = 40,
    var maxFlies: Int = 60,
    var maxTasks: Int = 150,
    var clickThrough: Boolean = true,
    var soundEnabled: Boolean = true,
    var notifyOnFly: Boolean = true,
    var dailyEnabled: Boolean = true,
    var weeklyEnabled: Boolean = true,
) {
    companion object {
        const val ROT_SPEED_MIN = 0.25f
        const val ROT_SPEED_MAX = 8f
        const val BASE_ROT_MINUTES_MIN = 15
        const val BASE_ROT_MINUTES_MAX = 480
    }
}

/** 引擎向 UI / 通知层发出的事件。每次 tick 后由调用方 drain。 */
sealed interface EngineEvent {
    val taskId: String
    val title: String

    /** 卵自己孵了。 */
    data class Hatched(override val taskId: String, override val title: String) : EngineEvent
    /** 卡住，僵成了蛹。 */
    data class Pupated(override val taskId: String, override val title: String) : EngineEvent
    /** 逼近苍蝇线，该预警了。 */
    data class ApproachingFly(override val taskId: String, override val title: String) : EngineEvent
    /** 彻底烂尾。 */
    data class BecameFly(override val taskId: String, override val title: String) : EngineEvent
    /** 交配产卵。 */
    data class Bred(
        override val taskId: String,
        override val title: String,
        val partnerId: String,
        val childId: String,
    ) : EngineEvent
    /** 被拍死。 */
    data class Squashed(override val taskId: String, override val title: String) : EngineEvent
    /** 每日任务生成了今天的新实例。 */
    data class DailySpawned(override val taskId: String, override val title: String) : EngineEvent
    /** 每周任务生成了本周的新实例。 */
    data class WeeklySpawned(override val taskId: String, override val title: String) : EngineEvent
}

/** 「我去做了」的结果，供 UI 给出反馈强度。 */
data class CareResult(
    val applied: Boolean,
    val relief: Float = 0f,
    val factor: Float = 0f,
    val progressGain: Int = 0,
)

/**
 * 规格中所有魔数集中在这里。**改这里等于改产品定义**，两端必须同步。
 *
 * 详见 `docs/lifecycle-spec.md`。
 */
object Lifecycle {
    const val ROT_MAX = 1.20f
    const val HATCH_T = 0.18f
    const val PUPA_T = 0.50f
    const val FLY_T = 0.78f
    const val APPROACH_T = FLY_T - 0.10f

    const val CARE_RELIEF = 0.22f
    const val CARE_COOLDOWN_SEC = 20f
    const val CARE_MIN_FACTOR = 0.35f
    const val CARE_PROGRESS_MIN = 4
    const val CARE_PROGRESS_MAX = 12

    const val GENE_STEP = 0.10f
    const val GENE_MAX = 2.20f
    const val OFFLINE_CAP_MIN = 45f

    const val MAX_DT = 0.50f
    const val SQUASHED_KEEP = 50

    /** 阶段对腐烂速度的放大倍率。 */
    fun stageMultiplier(stage: Stage): Float = when (stage) {
        Stage.EGG -> 0.70f
        Stage.LARVA -> 1.00f
        Stage.PUPA -> 1.35f
        Stage.FLY -> 1.60f
        Stage.DEAD -> 0f
    }

    /** 规格第 4 节。顺序不可调换。 */
    fun stageOf(task: TaskItem): Stage = when {
        task.status == TaskStatus.DONE -> Stage.DEAD
        task.rot >= FLY_T -> Stage.FLY
        task.rot >= PUPA_T -> Stage.PUPA
        task.rot >= HATCH_T -> Stage.LARVA
        task.status == TaskStatus.DOING -> Stage.LARVA
        else -> Stage.EGG
    }

    fun geneFor(generation: Int): Float =
        (1f + GENE_STEP * generation).coerceAtMost(GENE_MAX)

    /** 腐烂度 -> 0..1 的「危险度」，用于进度条配色。 */
    fun dangerOf(rot: Float): Float = (rot / FLY_T).coerceIn(0f, 1f)

    /** 按当前速度，还有多少毫秒会变成苍蝇；已经烂尾则返回 0。 */
    fun millisUntilFly(task: TaskItem, config: MaggotConfig): Long {
        if (task.status == TaskStatus.DONE) return 0
        if (task.rot >= FLY_T) return 0
        val rate = rotRatePerSecond(task, config, stageOf(task))
        if (rate <= 0f) return Long.MAX_VALUE
        return (((FLY_T - task.rot) / rate) * 1000f).toLong()
    }

    fun rotRatePerSecond(task: TaskItem, config: MaggotConfig, stage: Stage): Float {
        val window = (config.baseRotMinutes.coerceAtLeast(1) * 60f)
        return (1f / window) * config.rotSpeed * task.gene * stageMultiplier(stage)
    }
}
