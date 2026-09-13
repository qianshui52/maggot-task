package com.maggot.task

import com.maggot.task.core.Lifecycle
import com.maggot.task.core.LifecycleEngine
import com.maggot.task.core.MaggotConfig
import com.maggot.task.core.Stage
import com.maggot.task.render.SwarmSimulation
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random

/**
 * 生物**摆放**的回归测试。
 *
 * 存在的理由：曾经有一个真实 bug —— 卵和蛹是完全静止的（`vx = vy = 0`），
 * 但 `spawn()` 把**所有**生物都放在屏幕外缘之外。会动的阶段自己能飞进来，
 * 静止的阶段就**永久停在屏幕外**：引擎统计里明明有卵，用户屏幕上什么都没有。
 *
 * 引擎测试覆盖不到这个 —— 它在渲染层。所以这里单独立一组。
 *
 * `SwarmSimulation` 是纯 Kotlin（不碰 android.graphics），可以直接跑 JVM 测试。
 */
class SwarmSimulationTest {

    private val screenW = 1080
    private val screenH = 2400

    private fun sim(seed: Int = 7): SwarmSimulation =
        SwarmSimulation(Random(seed)).apply {
            density = 3f
            setViewport(screenW, screenH)
        }

    private fun engineWith(count: Int, rot: Float): LifecycleEngine {
        val e = LifecycleEngine(config = MaggotConfig())
        repeat(count) { i -> e.addTask("任务$i", now = 1_000L)!!.rot = rot }
        return e
    }

    private fun SwarmSimulation.run(engine: LifecycleEngine, frames: Int = 30) {
        repeat(frames) { advance(1f / 60f, engine) }
    }

    private fun assertAllOnScreen(s: SwarmSimulation, what: String) {
        for (c in s.all) {
            assertTrue(
                "$what 必须落在可视区内，实际 (${c.x}, ${c.y})",
                c.x >= 0f && c.x <= screenW && c.y >= 0f && c.y <= screenH,
            )
        }
    }

    @Test
    fun `eggs spawn on screen and stay there`() {
        val e = engineWith(25, rot = 0.02f)          // 全部是卵
        val s = sim()
        s.run(e)
        assertEquals(25, s.count())
        assertAllOnScreen(s, "卵")
    }

    @Test
    fun `pupae spawn on screen and stay there`() {
        val e = engineWith(25, rot = 0.62f)          // 全部是蛹
        val s = sim()
        s.run(e)
        assertEquals(25, s.count())
        assertAllOnScreen(s, "蛹")
    }

    @Test
    fun `a stationary egg dragged off screen is pulled back`() {
        val e = engineWith(1, rot = 0.02f)
        val s = sim()
        s.run(e, frames = 2)

        val c = s.all.first()
        // 模拟"出生时视口还没确定"或"分辨率变化"把它留在了界外
        c.x = -500f
        c.y = screenH + 500f

        s.advance(1f / 60f, e)

        assertTrue("静止的卵必须被拉回屏幕内，实际 (${c.x}, ${c.y})", c.x in 0f..screenW.toFloat())
        assertTrue("静止的卵必须被拉回屏幕内，实际 (${c.x}, ${c.y})", c.y in 0f..screenH.toFloat())
    }

    @Test
    fun `viewport change pulls already-placed creatures inside`() {
        val e = engineWith(10, rot = 0.02f)
        // 先用一个"假"的小视口，模拟 onSizeChanged 之前就 spawn 了
        val s = SwarmSimulation(Random(3)).apply { density = 3f }
        s.setViewport(200, 200)
        s.run(e, frames = 2)

        // 真实视口到位
        s.setViewport(screenW, screenH)

        assertAllOnScreen(s, "视口变化后的生物")
    }

    @Test
    fun `mobile stages may start off screen but must come into view`() {
        val e = engineWith(20, rot = 0.92f)          // 全部是苍蝇
        val s = sim()
        s.advance(1f / 60f, e)
        assertEquals(20, s.count())

        // 给它们足够时间飞进来（苍蝇 70–130 dp/s，60dp 边距很快就能进来）
        s.run(e, frames = 240)
        assertAllOnScreen(s, "飞行 4 秒后的苍蝇")
    }

    @Test
    fun `sessile stages are visible even when the viewport arrives late`() {        // 最坏情况：视口尺寸在 spawn 之后才确定
        val e = LifecycleEngine(config = MaggotConfig())
        repeat(40) { i ->
            e.addTask("任务$i", now = 1_000L)!!.rot = when (i % 4) {
                0 -> 0.02f; 1 -> 0.30f; 2 -> 0.62f; else -> 0.92f
            }
        }
        val s = SwarmSimulation(Random(11))
        s.setViewport(1, 1)          // 退化的视口
        s.run(e, frames = 2)
        s.setViewport(screenW, screenH)
        s.run(e, frames = 30)

        assertEquals(40, s.count())
        for (c in s.all) {
            val task = e.find(c.id)!!
            val stage = Lifecycle.stageOf(task)
            if (stage == Stage.EGG || stage == Stage.PUPA) {
                assertTrue(
                    "$stage 必须可见，实际 (${c.x}, ${c.y})",
                    c.x >= 0f && c.x <= screenW && c.y >= 0f && c.y <= screenH,
                )
            }
        }
    }

    /**
     * 视口退化成 0×0 时，**不能**把整个世界压扁。
     *
     * 窗口在 layout 完成之前会被测量成 0×0。旧实现是 `coerceAtLeast(1f)`，
     * 于是视口恒为 1×1 像素，`enforceVisibility` 又把每只虫子夹进 [0,1]×[0,1]，
     * 结果 4 只蛆全叠在屏幕左上角那一个像素上 —— 引擎统计里有虫，屏幕上什么都没有。
     * 这是一个和"出生在屏幕外"完全不同的坑，必须单独守一条。
     */
    @Test
    fun `a degenerate viewport never collapses the world`() {
        val e = engineWith(12, rot = 0.02f)
        val s = SwarmSimulation(Random(5)).apply { density = 3f }
        s.setViewport(screenW, screenH)
        s.run(e, frames = 2)

        // 0×0 必须被无视，保留上一次的真实尺寸。
        s.setViewport(0, 0)
        s.advance(1f / 60f, e)

        assertTrue(
            "0×0 视口不能被接受，实际 ${s.width}×${s.height}",
            s.width >= screenW * 0.5f && s.height >= screenH * 0.5f,
        )
        assertAllOnScreen(s, "退化视口之后")
    }

    /** 会动的阶段过场后也不能只露半边 —— 内缩夹取要真的把它们放回画面里。 */
    @Test
    fun `moving stages are pulled fully into view after the grace period`() {
        val e = engineWith(20, rot = 0.30f)          // 全部是蛆
        val s = sim()
        s.advance(1f / 60f, e)
        s.run(e, frames = 240)

        for (c in s.all) {
            assertTrue(
                "蛆必须完全落在可视区内，实际 (${c.x}, ${c.y})",
                c.x >= 0f && c.x <= screenW && c.y >= 0f && c.y <= screenH,
            )
        }
    }

    /**
     * 关闭动效时，生物必须**照常可见**（真机上翻过车的一条）。
     *
     * `spawn`（入场缩放进度）原来只在 `step()` 里推进，而 `step()` 在系统开启
     * 「减少动态效果」时根本不会被调用。于是 `spawn` 永远是 0，`spawnEase(0)` 恰好
     * 等于 0，绘制层算出 `size = 0` 后直接 `return` —— **整屏一只都不画**。
     * 与此同时体检卡报「悬浮层正常」（窗口挂上了、视口量到了、帧也在跑），
     * 用户看到的就是那句最费解的话：「卡上说正常，但屏幕上什么都没有」。
     *
     * 正确语义：「减少动效」= 不播入场动画，而不是「不出场」。
     */
    @Test
    fun `reduced motion still spawns creatures at full size`() {
        val e = engineWith(12, rot = 0.30f)
        val s = sim()
        s.motionEnabled = false

        s.advance(1f / 60f, e)

        assertEquals(12, s.count())
        for (c in s.all) {
            assertEquals("关闭动效时入场进度必须立刻完成", 1.0, c.spawn.toDouble(), 0.0001)
            assertTrue("关闭动效时 age 仍应推进（宽限期才有意义）", c.age > 0f)
        }
        assertAllOnScreen(s, "关闭动效后的蛆")
    }

    /** 关闭动效时，被丢到界外的会动阶段也必须被拉回来（走的是 clampSessile 分支）。 */
    @Test
    fun `reduced motion clamps a stray creature back on screen`() {
        val e = engineWith(4, rot = 0.30f)
        val s = sim()
        s.motionEnabled = false
        s.advance(1f / 60f, e)

        val c = s.all.first()
        c.x = -900f
        c.y = screenH + 900f

        s.advance(1f / 60f, e)

        assertAllOnScreen(s, "关闭动效后被丢到界外的蛆")
    }
}
