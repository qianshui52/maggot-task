package com.maggot.task

import com.maggot.task.core.DailyClock
import com.maggot.task.core.Lifecycle
import com.maggot.task.core.LifecycleEngine
import com.maggot.task.core.MaggotConfig
import com.maggot.task.core.Stage
import com.maggot.task.core.TaskStatus
import com.maggot.task.core.WeekClock
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate
import java.time.temporal.WeekFields
import kotlin.random.Random

/**
 * 直接对应 `docs/lifecycle-spec.md` 第 11 节的验收标准。
 *
 * 引擎是纯 Kotlin 的，所以这些用例跑在普通 JVM 上（`gradlew test`），
 * 不需要模拟器 —— Windows 端有一份等价的自测。
 */
class LifecycleEngineTest {

    private fun engine(cfg: MaggotConfig = MaggotConfig()) =
        LifecycleEngine(config = cfg, random = Random(42))

    private fun LifecycleEngine.newTask(title: String, now: Long = 1_000L) =
        addTask(title, now = now)!!

    /** 用固定 step 反复 tick，模拟真实时钟。 */
    private fun LifecycleEngine.run(seconds: Float, step: Float = 0.25f) {
        var elapsed = 0f
        while (elapsed < seconds) {
            tick(step, 1_000L + (elapsed * 1000).toLong())
            elapsed += step
        }
    }

    // ── 1. 阶段边界 ──

    @Test
    fun `stage boundaries follow the spec thresholds`() {
        val e = engine()
        val t = e.newTask("边界")

        val cases = listOf(
            0.00f to Stage.EGG,
            0.17f to Stage.EGG,
            0.18f to Stage.LARVA,
            0.49f to Stage.LARVA,
            0.50f to Stage.PUPA,
            0.77f to Stage.PUPA,
            0.78f to Stage.FLY,
            1.10f to Stage.FLY,
        )
        for ((rot, expected) in cases) {
            t.rot = rot
            assertEquals("rot=$rot", expected, Lifecycle.stageOf(t))
        }
    }

    @Test
    fun `marking DOING shows a larva immediately`() {
        val e = engine()
        val t = e.newTask("开始写了")
        assertEquals(Stage.EGG, Lifecycle.stageOf(t))
        t.status = TaskStatus.DOING
        assertEquals(Stage.LARVA, Lifecycle.stageOf(t))
    }

    @Test
    fun `an ignored egg hatches on its own`() {
        // 没点过"开始"，放着不管也会自己孵出来 —— 这正是产品要的压力。
        val e = engine()
        val t = e.newTask("从来不管")
        val before = Lifecycle.stageOf(t)
        t.rot = Lifecycle.HATCH_T + 0.01f
        assertEquals(Stage.EGG, before)
        assertEquals(Stage.LARVA, Lifecycle.stageOf(t))
    }

    // ── 2. 腐烂推进 / 暂停 ──

    @Test
    fun `rot advances while running and freezes while paused`() {
        val e = engine(MaggotConfig(rotSpeed = 1f, baseRotMinutes = 90))
        e.newTask("跑起来")
        e.run(seconds = 60f)          // 1 分钟真实时间
        val afterRun = e.tasks[0].rot
        assertTrue("rot 应该增长，实际 $afterRun", afterRun > 0f)

        e.config.running = false
        e.run(seconds = 60f)
        assertEquals(afterRun, e.tasks[0].rot, 1e-6f)
    }

    @Test
    fun `tick clamps dt so a sleeping device cannot jump the swarm`() {
        val e = engine(MaggotConfig(baseRotMinutes = 90))
        e.newTask("休眠")
        // 传一个巨大的 dt：内部必须 clamp 到 MAX_DT
        e.tick(3600f, 2_000L)
        val expectedMax = (1f / (90f * 60f)) * Lifecycle.MAX_DT * 0.70f // EGG 倍率
        assertTrue(
            "一次 tick 最多推进 MAX_DT 的量，实际 ${e.tasks[0].rot}",
            e.tasks[0].rot <= expectedMax + 1e-5f,
        )
    }

    // ── 3. care ──

    @Test
    fun `care lowers rot, sets DOING and advances progress`() {
        val e = engine()
        val t = e.newTask("照看", now = 0L)
        t.rot = 0.5f
        t.lastCaredAt = 0L

        val r = e.care(t.id, now = 60_000L)
        assertTrue(r.applied)
        assertTrue("rot 必须下降", t.rot < 0.5f)
        assertEquals(TaskStatus.DOING, t.status)
        assertTrue("进度必须推进", t.progress > 0)
        assertEquals(Lifecycle.CARE_RELIEF, r.relief, 1e-4f) // 冷却已满 => 满额
    }

    @Test
    fun `spamming care has strongly diminishing returns`() {
        val e = engine()
        val t = e.newTask("连点", now = 0L)
        val start = 0.95f
        t.rot = start
        t.lastCaredAt = 0L

        // 10 次都在同一时刻狂点：只有第一次有效果，其余 factor = CARE_MIN_FACTOR
        var now = 60_000L
        repeat(10) {
            e.care(t.id, now = now)
            now += 50L
        }
        val totalDrop = start - t.rot
        val naive = 10 * Lifecycle.CARE_RELIEF
        assertTrue(
            "连点总收益($totalDrop)必须远小于 10×RELIEF($naive)",
            totalDrop < naive * 0.45f,
        )
        assertTrue("但仍然必须有正反馈", totalDrop > 0f)
    }

    // ── 4. 拍死 ──

    @Test
    fun `complete marks DONE and emits Squashed`() {
        val e = engine()
        val t = e.newTask("拍死")
        e.drainEvents()
        assertTrue(e.complete(t.id, now = 5_000L))
        assertEquals(TaskStatus.DONE, t.status)
        assertEquals(100, t.progress)
        assertEquals(Stage.DEAD, Lifecycle.stageOf(t))
        assertTrue(e.drainEvents().any { it is com.maggot.task.core.EngineEvent.Squashed })
    }

    @Test
    fun `done tasks stop rotting`() {
        val e = engine()
        val t = e.newTask("完成")
        e.complete(t.id, now = 2_000L)
        val before = t.rot
        e.run(seconds = 120f)
        assertEquals(before, t.rot, 1e-6f)
    }

    // ── 5. 繁殖 ──

    @Test
    fun `a single fly never breeds`() {
        val e = engine(MaggotConfig(breedSeconds = 1, rotSpeed = 1f))
        val t = e.newTask("唯一一只")
        t.rot = 0.95f
        e.run(seconds = 120f)
        assertEquals(1, e.tasks.size)
    }

    @Test
    fun `two flies breed and offspring carries a stronger gene`() {
        val e = engine(MaggotConfig(breedSeconds = 1, rotSpeed = 1f, maxTasks = 50))
        repeat(2) { i ->
            e.newTask("苍蝇$i").also { it.rot = 0.95f }
        }
        e.run(seconds = 20f)

        assertTrue("必须繁殖出后代，实际 ${e.tasks.size} 个任务", e.tasks.size > 2)
        val child = e.tasks.first { it.generation > 0 }
        assertEquals(1, child.generation)
        assertEquals(Lifecycle.geneFor(1), child.gene, 1e-5f)
        assertTrue("后代的基因必须比原生强", child.gene > 1f)
        assertEquals(Stage.EGG, Lifecycle.stageOf(child))
        assertNotNull(child.parentId)
    }

    @Test
    fun `breeding respects maxTasks`() {
        val e = engine(MaggotConfig(breedSeconds = 1, rotSpeed = 8f, maxTasks = 6))
        repeat(3) { i -> e.newTask("苍蝇$i").also { it.rot = 0.95f } }
        e.run(seconds = 300f)
        assertTrue("不得超过 maxTasks，实际 ${e.tasks.size}", e.tasks.size <= 6)
    }

    @Test
    fun `maxFlies caps breeding but never freezes rotting`() {
        // 这是两端一致性的关键用例：
        // maxFlies 只封顶繁殖，不能变成"腐烂天花板"。
        // 一旦让腐烂停在苍蝇线下方，任务就永远卡在"即将变苍蝇"，
        // 产品的核心承诺（放着不管一定会烂）就被推翻了。
        val e = engine(
            MaggotConfig(breedSeconds = 1, rotSpeed = 1f, maxFlies = 3, maxTasks = 100)
        )
        repeat(3) { i -> e.newTask("苍蝇$i").also { it.rot = 0.95f } }
        e.run(seconds = 60f)
        assertEquals("已到 maxFlies，不该再繁殖", 3, e.tasks.size)

        // 但一个正在腐烂的任务仍然必须能变成苍蝇。
        // 注意时间要给够：rotSpeed=1、baseRotMinutes=90 时蛹期速率约 0.00025/s，
        // 跨过最后 0.01 需要约 40s，所以这里跑 240s 留足余量。
        val rookie = e.newTask("新人")
        rookie.rot = Lifecycle.FLY_T - 0.01f
        e.run(seconds = 240f)
        assertEquals("腐烂绝不能被 maxFlies 冻结", Stage.FLY, Lifecycle.stageOf(rookie))
    }

    @Test
    fun `gene grows across generations but is capped`() {
        assertEquals(1.0f, Lifecycle.geneFor(0), 1e-6f)
        assertTrue(Lifecycle.geneFor(3) > Lifecycle.geneFor(1))
        assertEquals(Lifecycle.GENE_MAX, Lifecycle.geneFor(999), 1e-6f)
    }

    // ── 6. 重置 / 清空 ──

    @Test
    fun `reset zeroes rot without deleting tasks`() {
        val e = engine()
        repeat(3) { i -> e.newTask("任务$i").also { it.rot = 0.6f } }
        e.resetAll(now = 9_000L)
        assertEquals(3, e.tasks.size)
        assertTrue(e.tasks.all { it.rot == 0f })
        assertTrue(e.tasks.all { it.status != TaskStatus.DONE })
    }

    @Test
    fun `reset does not resurrect squashed tasks`() {
        val e = engine()
        val a = e.newTask("拍死的")
        e.newTask("活着的")
        e.complete(a.id, now = 3_000L)
        e.resetAll(now = 4_000L)
        assertEquals(TaskStatus.DONE, a.status)
    }

    @Test
    fun `clear removes everything`() {
        val e = engine()
        repeat(5) { i -> e.newTask("任务$i") }
        e.clearAll()
        assertEquals(0, e.tasks.size)
    }

    // ── 7. 离线补算 ──

    @Test
    fun `offline decay is capped so an overnight absence is painful but survivable`() {
        val e = engine(MaggotConfig(baseRotMinutes = 90, rotSpeed = 1f))
        val t = e.newTask("离开一周")

        // 离开 7 天
        e.applyOfflineDecay(elapsedMillis = 7L * 24 * 3600 * 1000, now = 2_000L)

        val capped = Lifecycle.OFFLINE_CAP_MIN * 60f / (90f * 60f) * 0.70f
        assertTrue("补算必须被 OFFLINE_CAP_MIN 截断", t.rot <= capped + 1e-4f)
        assertTrue("但必须真的烂了一截", t.rot > 0f)
    }

    @Test
    fun `offline decay does not breed behind your back`() {
        val e = engine(MaggotConfig(breedSeconds = 1, rotSpeed = 8f))
        repeat(3) { i -> e.newTask("苍蝇$i").also { it.rot = 0.9f } }
        e.applyOfflineDecay(elapsedMillis = 3600_000L, now = 2_000L)
        assertEquals("离线期间只腐烂，不繁殖", 3, e.tasks.size)
    }

    // ── 8. 事件 ──

    @Test
    fun `stage transitions emit exactly one event each`() {
        val e = engine(MaggotConfig(baseRotMinutes = 1, rotSpeed = 8f))
        e.newTask("一路烂到底")
        e.run(seconds = 30f)

        val events = e.drainEvents()
        val hatched = events.count { it is com.maggot.task.core.EngineEvent.Hatched }
        val pupated = events.count { it is com.maggot.task.core.EngineEvent.Pupated }
        val flew = events.count { it is com.maggot.task.core.EngineEvent.BecameFly }
        assertEquals("孵化只报一次", 1, hatched)
        assertEquals("化蛹只报一次", 1, pupated)
        assertEquals("变苍蝇只报一次", 1, flew)
    }

    @Test
    fun `seeding the baseline suppresses startup notification storms`() {
        val e = engine()
        repeat(4) { i -> e.newTask("既有任务$i").also { it.rot = 0.95f } }
        e.seedStageBaseline()
        e.tick(0.05f, 2_000L)
        assertTrue("冷启动不该炸出阶段事件", e.drainEvents().isEmpty())
    }

    // ── 9. 上限保护 ──

    @Test
    fun `squashed history is trimmed to the retention limit`() {
        val e = engine()
        val n = Lifecycle.SQUASHED_KEEP + 25
        repeat(n) { i ->
            val t = e.newTask("任务$i")
            e.complete(t.id, now = 1_000L + i)
        }
        assertEquals(Lifecycle.SQUASHED_KEEP, e.countOf(Stage.DEAD))
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 每日任务（规格 §12.5 的七条验收标准）
    // ──────────────────────────────────────────────────────────────

    private val today: String = DailyClock.todayKey()
    private val tomorrow: String = LocalDate.parse(today).plusDays(1).toString()

    @Test
    fun `daily template immediately spawns today's instance`() {
        val e = engine()
        val tpl = e.addDailyTemplate("锻炼", now = 1_000L)
        assertNotNull(tpl)

        assertEquals("应该立刻生成一个实例", 1, e.tasks.size)
        val t = e.tasks[0]
        assertEquals(today, t.dailyKey)
        assertEquals(tpl!!.id, t.dailyTemplateId)
        assertEquals("实例从卵开始", 0f, t.rot, 1e-6f)
        assertEquals(TaskStatus.TODO, t.status)
        assertEquals("原生基因", 1.0f, t.gene, 1e-6f)
        assertTrue("标题必须带当天日期", t.title.startsWith("锻炼 · "))
        assertTrue("标题里要有 MM-DD", t.title.contains(DailyClock.shortLabel(today)))
        assertTrue(e.drainEvents().any { it is com.maggot.task.core.EngineEvent.DailySpawned })
    }

    @Test
    fun `ensuring daily tasks is idempotent within the same day`() {
        val e = engine()
        e.addDailyTemplate("锻炼", now = 1_000L)
        e.drainEvents()

        repeat(100) { e.ensureDailyTasks(today, now = 2_000L) }

        assertEquals("同一天调用 100 次也只能有一个实例", 1, e.tasks.size)
        assertTrue("幂等时不该反复发事件", e.drainEvents().isEmpty())
    }

    @Test
    fun `a new day spawns a new instance without resetting the old one`() {
        val e = engine()
        e.addDailyTemplate("锻炼", now = 1_000L)
        val old = e.tasks[0]
        old.rot = 0.42f   // 昨天那只已经烂了一半

        e.ensureDailyTasks(tomorrow, now = 90_000_000L)

        assertEquals("跨天要生成新实例", 2, e.tasks.size)
        assertEquals("老实例的腐烂度绝不能被重置", 0.42f, old.rot, 1e-6f)

        val fresh = e.tasks.first { it.id != old.id }
        assertEquals(tomorrow, fresh.dailyKey)
        assertEquals(0f, fresh.rot, 1e-6f)
        assertEquals("逾期判定：昨天的算 1 天", 1, old.overdueDays(tomorrow))
        assertEquals("今天的实例不算逾期", 0, fresh.overdueDays(tomorrow))
    }

    @Test
    fun `disabling a template stops new spawns but existing instances keep rotting`() {
        val e = engine(MaggotConfig(baseRotMinutes = 1))
        val tpl = e.addDailyTemplate("锻炼", now = 1_000L)!!
        e.setDailyEnabled(tpl.id, false)

        e.ensureDailyTasks(tomorrow, now = 90_000_000L)
        assertEquals("停用后不再生成新实例", 1, e.tasks.size)

        val old = e.tasks[0]
        old.rot = 0.30f
        e.run(seconds = 60f)
        assertTrue("已有实例必须照常腐烂", old.rot > 0.30f)
    }

    @Test
    fun `deleting a template keeps its instances alive as ordinary tasks`() {
        val e = engine()
        val tpl = e.addDailyTemplate("锻炼", now = 1_000L)!!
        assertTrue(e.removeDailyTemplate(tpl.id))

        assertEquals("实例不能被连带删除", 1, e.tasks.size)
        val orphan = e.tasks[0]
        assertEquals("模板没了，但实例仍是每日实例", tpl.id, orphan.dailyTemplateId)

        // 孤儿实例依然可以被正常完成、正常腐烂
        orphan.rot = 0.95f
        assertEquals(Stage.FLY, Lifecycle.stageOf(orphan))
        assertTrue(e.complete(orphan.id, now = 5_000L))
    }

    @Test
    fun `offspring of a daily instance are ordinary tasks`() {
        val e = engine(MaggotConfig(breedSeconds = 1, rotSpeed = 1f, maxTasks = 50))
        val tpl = e.addDailyTemplate("锻炼", now = 1_000L)!!
        val daily = e.tasks.first { it.dailyTemplateId == tpl.id }
        daily.rot = 0.95f                       // 烂成苍蝇
        e.newTask("陪跑的苍蝇").also { it.rot = 0.95f }

        e.run(seconds = 20f)

        val child = e.tasks.firstOrNull { it.generation > 0 }
        assertNotNull("应该繁殖出后代", child)
        assertNull("繁殖出的后代必须是普通任务", child!!.dailyTemplateId)
        assertNull(child.dailyKey)
    }

    @Test
    fun `the global daily switch disables spawning entirely`() {
        val e = engine(MaggotConfig(dailyEnabled = false))
        e.addDailyTemplate("锻炼", now = 1_000L)

        assertEquals("总开关关掉时连今天都不生成", 0, e.tasks.size)
        e.ensureDailyTasks(tomorrow, now = 90_000_000L)
        assertEquals(0, e.tasks.size)
    }

    @Test
    fun `overdue days are computed from the local date key`() {
        assertEquals(0, DailyClock.overdueDays(null, today))
        assertEquals(0, DailyClock.overdueDays(today, today))
        assertEquals(0, DailyClock.overdueDays(tomorrow, today))
        assertEquals(1, DailyClock.overdueDays(
            LocalDate.parse(today).minusDays(1).toString(), today,
        ))
        assertEquals(7, DailyClock.overdueDays(
            LocalDate.parse(today).minusDays(7).toString(), today,
        ))
    }

    // ──────────────────────────────────────────────────────────────
    // 11. 每周任务（规格 §15.6 的验收标准）
    // ──────────────────────────────────────────────────────────────

    @Test
    fun `iso week key uses the week-based year across new year`() {
        // 2027-01-01 是周五，2027-01-04 才是周一 —— 所以 1/1~1/3 属于 **2026** 年的最后一周。
        // 这就是必须用 YYYY（周基准年）而不是 yyyy（日历年）的原因：
        // 写成 yyyy 会得到 2027-Wxx，凭空多出一周。
        val jan1 = LocalDate.of(2027, 1, 1)
        val jan3 = LocalDate.of(2027, 1, 3)

        assertEquals("2026", WeekClock.weekKey(jan1).substringBefore("-W"))
        assertEquals(
            "同一 ISO 周内的跨年两天必须有相同周键",
            WeekClock.weekKey(jan1), WeekClock.weekKey(jan3),
        )

        // 与 java.time 自己的 ISO 周字段对照（独立算法，互为验证）
        val expectedYear = jan1.get(WeekFields.ISO.weekBasedYear())
        val expectedWeek = jan1.get(WeekFields.ISO.weekOfWeekBasedYear())
        assertEquals("%d-W%02d".format(expectedYear, expectedWeek), WeekClock.weekKey(jan1))
        assertNotEquals(
            "日历年写法会得出不同结果（证明 YYYY 是必须的）",
            "%d-W%02d".format(jan1.year, expectedWeek), WeekClock.weekKey(jan1),
        )
    }

    @Test
    fun `iso weekday is monday-based one to seven`() {
        assertEquals(1, WeekClock.isoWeekday(LocalDate.of(2027, 1, 4)))   // 周一
        assertEquals(7, WeekClock.isoWeekday(LocalDate.of(2027, 1, 10)))  // 周日
    }

    @Test
    fun `weekly template immediately spawns when today already qualifies`() {
        val e = engine()
        // weekday = 1（周一）永远 <= 今天，所以必然立刻生成
        val tpl = e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)
        assertNotNull(tpl)

        assertEquals(1, e.tasks.size)
        val t = e.tasks[0]
        assertEquals(WeekClock.weekKey(), t.weeklyKey)
        assertEquals(tpl!!.id, t.weeklyTemplateId)
        assertEquals(0f, t.rot, 1e-6f)
        assertEquals(1.0f, t.gene, 1e-6f)
        assertTrue("标题要带周号", t.title.startsWith("写周报 · W"))
        assertTrue(e.drainEvents().any { it is com.maggot.task.core.EngineEvent.WeeklySpawned })
    }

    @Test
    fun `weekly template does not spawn before its weekday`() {
        val e = engine()
        e.addWeeklyTemplate("写周报", weekday = 7, now = 1_000L)
        e.tasks.clear()   // 清掉 addWeeklyTemplate 可能顺手生成的那只

        // 周三（3）还没到周日（7）→ 不生成
        assertEquals(0, e.ensureWeeklyTasks("2026-W37", isoWeekday = 3, now = 2_000L))
        assertEquals(0, e.tasks.size)

        // 到了周日 → 生成
        assertEquals(1, e.ensureWeeklyTasks("2026-W37", isoWeekday = 7, now = 3_000L))
        assertEquals(1, e.tasks.size)
    }

    @Test
    fun `ensuring weekly tasks is idempotent within the same week`() {
        val e = engine()
        e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)
        e.drainEvents()

        repeat(100) { e.ensureWeeklyTasks("2026-W37", isoWeekday = 5, now = 2_000L) }

        assertEquals("同一周调用 100 次也只能有一个实例", 1, e.tasks.size)
        assertTrue("幂等时不该反复发事件", e.drainEvents().isEmpty())
    }

    @Test
    fun `a new week spawns a new instance without resetting the old one`() {
        val e = engine()
        e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)
        val old = e.tasks[0]
        old.rot = 0.42f

        e.ensureWeeklyTasks("2026-W38", isoWeekday = 1, now = 90_000_000L)

        assertEquals(2, e.tasks.size)
        assertEquals("上周那只的腐烂度绝不能被重置", 0.42f, old.rot, 1e-6f)
        val fresh = e.tasks.first { it.id != old.id }
        assertEquals("2026-W38", fresh.weeklyKey)
        assertEquals(0f, fresh.rot, 1e-6f)
        assertEquals(1, old.overdueWeeks("2026-W38"))
        assertEquals(0, fresh.overdueWeeks("2026-W38"))
    }

    @Test
    fun `disabling a weekly template stops new spawns but old ones keep rotting`() {
        val e = engine(MaggotConfig(baseRotMinutes = 1))
        val tpl = e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)!!
        e.setWeeklyEnabled(tpl.id, false)

        e.ensureWeeklyTasks("2099-W01", isoWeekday = 1, now = 90_000_000L)
        assertEquals("停用后不再生成新实例", 1, e.tasks.size)

        val old = e.tasks[0]
        old.rot = 0.30f
        e.run(seconds = 60f)
        assertTrue("已有实例必须照常腐烂", old.rot > 0.30f)
    }

    @Test
    fun `weekly weekday can be changed and only affects future spawns`() {
        val e = engine()
        val tpl = e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)!!
        val existing = e.tasks[0]

        assertTrue(e.setWeeklyWeekday(tpl.id, 5))
        assertEquals(5, tpl.weekday)
        assertEquals("已有实例不受影响", 1, e.tasks.size)
        assertEquals(existing.id, e.tasks[0].id)

        // 改成周五后，周三不再生成下一周的实例
        assertEquals(0, e.ensureWeeklyTasks("2026-W40", isoWeekday = 3, now = 5_000L))
        assertEquals(1, e.ensureWeeklyTasks("2026-W40", isoWeekday = 5, now = 6_000L))
    }

    @Test
    fun `deleting a weekly template keeps its instances alive`() {
        val e = engine()
        val tpl = e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)!!
        assertTrue(e.removeWeeklyTemplate(tpl.id))

        assertEquals("实例不能被连带删除", 1, e.tasks.size)
        val orphan = e.tasks[0]
        assertEquals(tpl.id, orphan.weeklyTemplateId)

        orphan.rot = 0.95f
        assertEquals(Stage.FLY, Lifecycle.stageOf(orphan))
        assertTrue(e.complete(orphan.id, now = 5_000L))
    }

    @Test
    fun `offspring of a weekly instance are ordinary tasks`() {
        val e = engine(MaggotConfig(breedSeconds = 1, rotSpeed = 1f, maxTasks = 50))
        val tpl = e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)!!
        e.tasks.first { it.weeklyTemplateId == tpl.id }.rot = 0.95f
        e.newTask("陪跑的苍蝇").also { it.rot = 0.95f }

        e.run(seconds = 20f)

        val child = e.tasks.firstOrNull { it.generation > 0 }
        assertNotNull("应该繁殖出后代", child)
        assertNull("繁殖出的后代必须是普通任务", child!!.weeklyTemplateId)
        assertNull(child.weeklyKey)
    }

    @Test
    fun `the global weekly switch disables spawning entirely`() {
        val e = engine(MaggotConfig(weeklyEnabled = false))
        e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)

        assertEquals("总开关关掉时连本周都不生成", 0, e.tasks.size)
        e.ensureWeeklyTasks("2026-W38", isoWeekday = 1, now = 9_000L)
        assertEquals(0, e.tasks.size)
    }

    @Test
    fun `daily and weekly tasks coexist and stay independent`() {
        val e = engine()
        e.addDailyTemplate("锻炼", now = 1_000L)
        e.addWeeklyTemplate("写周报", weekday = 1, now = 1_000L)

        assertEquals("两个模板各生成一个实例", 2, e.tasks.size)
        assertEquals(1, e.tasks.count { it.isDaily })
        assertEquals(1, e.tasks.count { it.isWeekly })

        repeat(50) {
            e.ensureDailyTasks(DailyClock.todayKey(), now = 2_000L)
            e.ensureWeeklyTasks(WeekClock.weekKey(), isoWeekday = 3, now = 2_000L)
        }
        assertEquals("两种任务各自幂等，互不干扰", 2, e.tasks.size)
    }

    @Test
    fun `overdue weeks are computed from the week key`() {
        val a = WeekClock.weekKey(LocalDate.of(2026, 9, 7))    // 周一
        val b = WeekClock.weekKey(LocalDate.of(2026, 9, 14))   // 下一周周一
        val c = WeekClock.weekKey(LocalDate.of(2026, 9, 28))   // 三周后

        assertEquals(0, WeekClock.overdueWeeks(null, a))
        assertEquals(0, WeekClock.overdueWeeks(a, a))
        assertEquals(0, WeekClock.overdueWeeks(c, a))   // 未来不算逾期
        assertEquals(1, WeekClock.overdueWeeks(a, b))
        assertEquals(3, WeekClock.overdueWeeks(a, c))
    }
}
