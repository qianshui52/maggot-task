package com.maggot.task.render

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.os.SystemClock
import android.view.View
import androidx.compose.ui.graphics.toArgb
import com.maggot.task.AppGraph
import com.maggot.task.ui.theme.RotHigh
import com.maggot.task.ui.theme.Success

/**
 * 悬浮层上那个"只负责画"的 View。
 *
 * 时间推进在 [com.maggot.task.service.SwarmService] 里做，这个类不持有自己的时钟 ——
 * 这样调度节奏只有一个来源，也不会因为 View 被回收而让模拟停摆。
 * 生物数据来自构造时传入的 [SwarmSimulation]（默认是 [AppGraph.simulation]）。
 */
class SwarmView(
    context: Context,
    private val graph: AppGraph,
    /**
     * 用自己的模拟时传进来（控制台的兜底层）；传 null 表示用悬浮层那一份
     * [AppGraph.simulation]，并且由这一层负责上报视口。
     */
    ownSimulation: SwarmSimulation? = null,
) : View(context) {

    /**
     * 渲染的数据源。悬浮层用共享那一份，兜底层用它自己那一份 ——
     * 两个图层视口不同，共用一份会把坐标系统搅在一起。
     * 推进只由 [AppGraph.tick] 负责，这个类不持有自己的时钟。
     */
    val simulation: SwarmSimulation = ownSimulation ?: graph.simulation

    /** 只有"主人"才有资格设定并上报视口，否则兜底层会把悬浮层的尺寸覆盖掉。 */
    private val ownsViewport: Boolean = ownSimulation == null

    private val painter = CreaturePainter()
    private val particlePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val splatColor = Success.toArgb()
    private val dangerColor = RotHigh.toArgb()

    var motionEnabled: Boolean = true

    /**
     * 是否接收触摸。
     *
     * 悬浮层上是 `true`（点一下虫子＝「我去做了」）；嵌在控制台里的兜底层是
     * `false` —— 那一层铺满整屏，如果吃掉触摸，用户点空白处就会误触。
     */
    var interactive: Boolean = true

    /** 非穿透模式下短按生物（＝「我去做了」）。 */
    var onCreatureTap: ((String) -> Unit)? = null

    /** 非穿透模式下长按生物（＝查看它对应哪个任务）。 */
    var onCreatureInspect: ((String) -> Unit)? = null

    private var lastTapAt = 0L
    private var downX = 0f
    private var downY = 0f
    private var longPressArmed = false

    /** 长按阈值。与规格 §14.3 的「长按 600ms」一致。 */
    private val longPressRunnable = Runnable {
        if (!longPressArmed) return@Runnable
        longPressArmed = false
        longPressFired = true
        hitTest(downX, downY)?.let { onCreatureInspect?.invoke(it) }
    }
    private var longPressFired = false

    init {
        setWillNotDraw(false)
        setBackgroundColor(0x00000000)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        simulation.density = resources.displayMetrics.density
        // 每一层都用自己的尺寸（各自的模拟，互不干扰）。
        simulation.setViewport(w, h)
        // 只有悬浮层有权上报：兜底层的尺寸不含状态栏和导航栏，
        // 让它上报会把体检卡上的数字变成误导人的东西。
        if (ownsViewport) graph.reportViewport(w, h)
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val nowSec = (SystemClock.uptimeMillis() % 1_000_000L) / 1000f
        val engine = graph.engine

        // 按任务顺序遍历 + 对 creature 做 O(1) 查找，避免 O(n²)。
        for (task in engine.tasks) {
            val c = simulation.find(task.id) ?: continue
            painter.draw(
                canvas = canvas,
                c = c,
                task = task,
                stage = engine.stageOf(task),
                baseSize = simulation.baseSize,
                motion = motionEnabled,
                nowSec = nowSec,
            )
        }

        drawParticles(canvas)
    }

    /** 爆浆粒子画在最上层，因为"拍死"的即时反馈比什么都重要。 */
    private fun drawParticles(canvas: Canvas) {
        val list = simulation.particleList
        if (list.isEmpty()) return
        for (p in list) {
            val t = (p.life / p.maxLife).coerceIn(0f, 1f)
            particlePaint.color = splatColor
            particlePaint.alpha = (255 * t * t).toInt().coerceIn(0, 255)
            canvas.drawCircle(p.x, p.y, p.size * (0.4f + 0.6f * t), particlePaint)
        }
    }

    /**
     * 只有在非穿透模式下才会收到触摸（穿透时这一层根本不接事件）。
     *
     * 手势分工：**短按 = 我去做了**（非破坏性），**长按 600ms = 看看这是哪个任务**。
     * 长按对应桌面版的"悬停 3 秒" —— 手机没有悬停，长按是最接近的等价交互。
     */
    override fun onTouchEvent(event: android.view.MotionEvent): Boolean {
        // 兜底层不参与触摸，避免误触。
        if (!interactive) return false
        when (event.actionMasked) {
            android.view.MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                longPressFired = false
                if (hitTest(downX, downY) != null) {
                    longPressArmed = true
                    postDelayed(longPressRunnable, LONG_PRESS_MS)
                }
            }

            android.view.MotionEvent.ACTION_MOVE -> {
                // 手指移动超过触摸斜率就取消长按，避免误触。
                if (longPressArmed) {
                    val slop = android.view.ViewConfiguration.get(context).scaledTouchSlop
                    if (kotlin.math.abs(event.x - downX) > slop ||
                        kotlin.math.abs(event.y - downY) > slop
                    ) {
                        cancelPendingInspect()
                    }
                }
            }

            android.view.MotionEvent.ACTION_UP -> {
                val armed = longPressArmed
                cancelPendingInspect()
                // 长按已经处理过了，抬手时不要再当成短按。
                if (armed && !longPressFired) {
                    val now = SystemClock.uptimeMillis()
                    if (now - lastTapAt >= 180L) {
                        lastTapAt = now
                        hitTest(event.x, event.y)?.let { onCreatureTap?.invoke(it) }
                    }
                }
            }

            android.view.MotionEvent.ACTION_CANCEL -> cancelPendingInspect()
        }
        return true
    }

    /** 注意：不能叫 cancelLongPress —— View 已经有同名方法，会撞签名。 */
    private fun cancelPendingInspect() {
        longPressArmed = false
        removeCallbacks(longPressRunnable)
    }

    /** 找到离触点最近且在血区内的生物。 */
    private fun hitTest(x: Float, y: Float): String? {
        var best: String? = null
        var bestDist = Float.MAX_VALUE
        val reach = simulation.baseSize * 0.95f
        for (c in simulation.all) {
            val dx = c.x - x
            val dy = c.y - y
            val d = dx * dx + dy * dy
            if (d < bestDist && d <= reach * reach) {
                bestDist = d
                best = c.id
            }
        }
        return best
    }

    /** 供无障碍/TalkBack 读出的简短状态。 */
    fun describe(): String {
        val s = graph.engine.stats()
        return "虫群：卵 ${s.egg}，蛆 ${s.larva}，蛹 ${s.pupa}，苍蝇 ${s.fly}"
    }

    private companion object {
        const val LONG_PRESS_MS = 600L
    }
}
