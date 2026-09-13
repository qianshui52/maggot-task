package com.maggot.task.render

import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Shader
import androidx.compose.ui.graphics.toArgb
import com.maggot.task.core.Lifecycle
import com.maggot.task.core.Stage
import com.maggot.task.core.TaskItem
import com.maggot.task.ui.theme.CreatureOutline
import com.maggot.task.ui.theme.EggCol
import com.maggot.task.ui.theme.EggDeep
import com.maggot.task.ui.theme.FlyCol
import com.maggot.task.ui.theme.FlyRim
import com.maggot.task.ui.theme.LarvaCol
import com.maggot.task.ui.theme.LarvaDeep
import com.maggot.task.ui.theme.PupaCol
import com.maggot.task.ui.theme.PupaDeep
import com.maggot.task.ui.theme.RotHigh
import com.maggot.task.ui.theme.WingA
import com.maggot.task.ui.theme.WingB
import kotlin.math.abs
import kotlin.math.sin

/**
 * 程序化矢量绘制 —— 没有任何位图资源。
 *
 * **核心技巧**：所有形状都在**单位坐标系**里描述（生物跨度约 1.0），
 * 由 [layer] 统一施加 `translate → rotate → scale`。这样阴影层、描边层、本体层
 * 可以复用同一个形状函数，只要换一个放大倍数和颜色，就自动得到
 * "深色描边 + 柔和阴影"——也就是生物在纯白 / 纯黑背景上都能看清的原因。
 *
 * 修正：不要把生物画成纯白或纯黑；中间调 + 描边才是在任意桌面上都可读的关键。
 */
class CreaturePainter {

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val rect = RectF()

    private val wingPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        alpha = 105
        shader = LinearGradient(
            -0.42f, 0f, 0.30f, 0f,
            intArrayOf(WingA.copy(alpha = 0.85f).toArgb(), WingB.copy(alpha = 0.55f).toArgb()),
            null,
            Shader.TileMode.CLAMP,
        )
    }

    // 单位坐标下的描边宽度：乘以生物像素尺寸后约为 1.5–2px。
    private val creaseW = 0.040f

    // 提到类级别，避免每帧在 onDraw 里分配数组。
    private val sides = intArrayOf(-1, 1)
    private val pupaCreases = floatArrayOf(-0.20f, 0.02f, 0.24f)
    private val wobble = FloatArray(6)

    /**
     * 绘制一只生物。[baseSize] 是像素尺寸（对应单位 1.0）。
     * [motion] 为 false 时（系统开启了"减少动态效果"）生物静止、翅膀收拢。
     */
    fun draw(
        canvas: Canvas,
        c: Creature,
        task: TaskItem,
        stage: Stage,
        baseSize: Float,
        motion: Boolean,
        nowSec: Float,
    ) {
        // 关闭动效时不做入场缩放，直接以完整尺寸绘制。
        //
        // 这不只是"少一个动画"：spawnEase(0) 恰好等于 0，一旦 spawn 因为任何原因没能
        // 推进，size 就会算成 0，下面那行 `return` 会把**每一只**生物都跳过 ——
        // 现象就是"体检卡说悬浮层正常，屏幕上却一只都没有"。
        // 所以这里对"减少动效"直接取 1，给可见性上一道硬保险。
        val s = baseSize * c.sizeScale * if (motion) spawnEase(c.spawn) else 1f
        if (s <= 0.5f) return
        val angleDeg = Math.toDegrees(c.angle.toDouble()).toFloat()

        when (stage) {
            Stage.EGG -> drawEgg(canvas, c, task, s, angleDeg, motion, nowSec)
            Stage.LARVA -> drawLarva(canvas, c, task, s, angleDeg, motion)
            Stage.PUPA -> drawPupa(canvas, c, task, s, angleDeg, motion)
            Stage.FLY -> drawFly(canvas, c, task, s, angleDeg, motion)
            Stage.DEAD -> Unit
        }

        // 预警光晕：逼近苍蝇线时，生物外面套一圈危险色脉冲。
        if (task.rot >= Lifecycle.APPROACH_T && stage != Stage.FLY) {
            drawWarningHalo(canvas, c, s, task, nowSec, motion)
        }
    }

    // ───────────────────────────── 卵 ─────────────────────────────

    private fun drawEgg(
        canvas: Canvas, c: Creature, task: TaskItem,
        s: Float, angleDeg: Float, motion: Boolean, nowSec: Float,
    ) {
        val breathe = if (motion) 1f + 0.04f * sin(nowSec * 1.6f + c.seed) else 1f

        // 阴影
        layer(canvas, c, angleDeg, s * 1.10f * breathe, 0f, s * 0.10f, SHADOW) {
            canvas.drawOval(-0.36f, -0.46f, 0.36f, 0.46f, fill)
        }
        // 深色描边（浅色背景上不消失）
        layer(canvas, c, angleDeg, s * 1.06f * breathe, 0f, 0f, CreatureOutline.toArgb()) {
            canvas.drawOval(-0.36f, -0.46f, 0.36f, 0.46f, fill)
        }
        // 本体：先铺暗部，再叠亮部，形成左上受光
        layer(canvas, c, angleDeg, s * breathe, 0f, 0f, EggDeep.toArgb()) {
            canvas.drawOval(-0.36f, -0.46f, 0.36f, 0.46f, fill)
        }
        layer(canvas, c, angleDeg, s * breathe, -s * 0.045f, -s * 0.055f, EggCol.toArgb()) {
            canvas.drawOval(-0.33f, -0.42f, 0.33f, 0.42f, fill)
        }
        // 高光
        layer(canvas, c, angleDeg, s * breathe, 0f, 0f, HIGHLIGHT) {
            canvas.drawOval(-0.22f, -0.34f, 0.02f, -0.10f, fill)
        }
        // 腐烂暗斑：rot 越高，卵越"脏"
        val speck = ((task.rot - 0.08f) * 30f).toInt()
        if (speck > 0) {
            layer(canvas, c, angleDeg, s, 0f, 0f, SPECK) {
                for (i in 0 until speck.coerceAtMost(14)) {
                    val h1 = hash01(c.seed, i * 2)
                    val h2 = hash01(c.seed, i * 2 + 1)
                    val px = (h1 - 0.5f) * 0.52f
                    val py = (h2 - 0.5f) * 0.68f
                    if (px * px / 0.13f + py * py / 0.21f < 0.92f) {
                        canvas.drawCircle(px, py, 0.020f + h1 * 0.018f, fill)
                    }
                }
            }
        }
    }

    // ───────────────────────────── 蛆 ─────────────────────────────

    private val segX = floatArrayOf(-0.45f, -0.27f, -0.09f, 0.09f, 0.27f, 0.45f)
    private val segR = floatArrayOf(0.155f, 0.185f, 0.195f, 0.185f, 0.165f, 0.135f)

    private fun drawLarva(
        canvas: Canvas, c: Creature, task: TaskItem,
        s: Float, angleDeg: Float, motion: Boolean,
    ) {
        val amp = if (motion) 0.085f else 0f
        val wob = wobble
        for (i in 0..5) {
            wob[i] = if (motion) amp * sin(c.phase * 0.06f + i * 0.9f) else 0f
        }

        // 阴影
        layer(canvas, c, angleDeg, s * 1.10f, 0f, s * 0.10f, SHADOW) {
            for (i in 0..5) canvas.drawCircle(segX[i], wob[i], segR[i], fill)
        }
        // 描边（合并轮廓靠"放大一层的深色底"实现，天然不会有内部杂线）
        layer(canvas, c, angleDeg, s * 1.07f, 0f, 0f, CreatureOutline.toArgb()) {
            for (i in 0..5) canvas.drawCircle(segX[i], wob[i], segR[i], fill)
        }
        // 本体
        layer(canvas, c, angleDeg, s, 0f, 0f, LarvaCol.toArgb()) {
            for (i in 0..5) canvas.drawCircle(segX[i], wob[i], segR[i], fill)
        }
        // 分节缝
        layer(canvas, c, angleDeg, s, 0f, 0f, LarvaDeep.toArgb()) {
            stroke.strokeWidth = creaseW
            stroke.strokeCap = Paint.Cap.ROUND
            for (i in 1..4) {
                val x = (segX[i - 1] + segX[i]) * 0.5f
                val r = (segR[i - 1] + segR[i]) * 0.5f
                val w = wob[i]
                canvas.drawLine(x, w - r * 0.95f, x, w + r * 0.95f, stroke)
            }
        }
        // 头节加深 + 口钩
        val headW = wob[5]
        layer(canvas, c, angleDeg, s, 0f, 0f, LarvaDeep.toArgb()) {
            canvas.drawCircle(segX[5], headW, segR[5] * 0.78f, fill)
        }
        layer(canvas, c, angleDeg, s, 0f, 0f, MOUTH) {
            canvas.drawCircle(segX[5] + 0.115f, headW - 0.045f, 0.028f, fill)
            canvas.drawCircle(segX[5] + 0.115f, headW + 0.045f, 0.028f, fill)
        }
    }

    // ───────────────────────────── 蛹 ─────────────────────────────

    private fun drawPupa(
        canvas: Canvas, c: Creature, task: TaskItem,
        s: Float, angleDeg: Float, motion: Boolean,
    ) {
        // 静止，但偶尔抽动一下 —— 证明它还活着，渲染没卡死
        val tw = if (motion && c.twitch > 0f) sin(c.twitch * 90f) * 2.2f else 0f

        layer(canvas, c, angleDeg + tw, s * 1.09f, 0f, s * 0.10f, SHADOW) { pupaBody(canvas) }
        layer(canvas, c, angleDeg + tw, s * 1.06f, 0f, 0f, CreatureOutline.toArgb()) { pupaBody(canvas) }
        layer(canvas, c, angleDeg + tw, s, 0f, 0f, PupaCol.toArgb()) { pupaBody(canvas) }

        // 纵向暗部
        layer(canvas, c, angleDeg + tw, s, 0.06f, 0.06f, PupaDeep.toArgb()) {
            rect.set(-0.16f, -0.30f, 0.21f, 0.40f)
            canvas.drawRoundRect(rect, 0.18f, 0.18f, fill)
        }
        // 节纹
        layer(canvas, c, angleDeg + tw, s, 0f, 0f, PupaDeep.toArgb()) {
            stroke.strokeWidth = creaseW
            stroke.strokeCap = Paint.Cap.ROUND
            for (y in pupaCreases) {
                canvas.drawLine(-0.17f, y, 0.17f, y, stroke)
            }
        }
        // 高光条
        layer(canvas, c, angleDeg + tw, s, 0f, 0f, HIGHLIGHT) {
            rect.set(-0.13f, -0.34f, -0.04f, 0.20f)
            canvas.drawRoundRect(rect, 0.05f, 0.05f, fill)
        }
    }

    private fun pupaBody(canvas: Canvas) {
        rect.set(-0.21f, -0.42f, 0.21f, 0.42f)
        canvas.drawRoundRect(rect, 0.21f, 0.21f, fill)
    }

    // ───────────────────────────── 苍蝇 ─────────────────────────────

    private fun drawFly(
        canvas: Canvas, c: Creature, task: TaskItem,
        s: Float, angleDeg: Float, motion: Boolean,
    ) {
        layer(canvas, c, angleDeg, s * 1.10f, 0f, s * 0.12f, SHADOW) { flyBody(canvas) }
        layer(canvas, c, angleDeg, s * 1.07f, 0f, 0f, CreatureOutline.toArgb()) { flyBody(canvas) }
        layer(canvas, c, angleDeg, s, 0f, 0f, FlyCol.toArgb()) { flyBody(canvas) }

        // 虹彩描边：暗背景上的可见性全靠它
        layer(canvas, c, angleDeg, s, 0f, 0f, FlyRim.copy(alpha = 0.75f).toArgb()) {
            stroke.strokeWidth = 0.030f
            rect.set(-0.48f, -0.19f, 0.04f, 0.19f)
            canvas.drawOval(rect, stroke)          // 腹部
            canvas.drawCircle(0.02f, 0f, 0.155f, stroke)  // 胸部
            canvas.drawCircle(0.20f, 0f, 0.105f, stroke)  // 头
        }
        // 复眼
        layer(canvas, c, angleDeg, s, 0f, 0f, EYE) {
            rect.set(0.20f, -0.100f, 0.275f, -0.020f); canvas.drawOval(rect, fill)
            rect.set(0.20f, 0.020f, 0.275f, 0.100f); canvas.drawOval(rect, fill)
        }

        // 翅膀：独立一层，带扇动
        val flap = if (motion) {
            abs(sin(c.phase * 0.70f + c.seed)).coerceAtLeast(0.18f)
        } else {
            0.85f
        }
        canvas.save()
        canvas.translate(c.x, c.y)
        canvas.rotate(angleDeg)
        canvas.scale(s, s)
        for (side in sides) {
            canvas.save()
            canvas.translate(0.02f, side * 0.10f)
            canvas.rotate(side * (18f + 16f * flap))
            canvas.scale(1f, (0.40f + 0.60f * flap) * side)
            rect.set(-0.44f, -0.16f, 0.16f, 0.16f)
            canvas.drawOval(rect, wingPaint)
            canvas.restore()
        }
        canvas.restore()

        // 腿
        layer(canvas, c, angleDeg, s, 0f, 0f, LEG) {
            stroke.strokeWidth = 0.026f
            stroke.strokeCap = Paint.Cap.ROUND
            for (side in sides) {
                canvas.drawLine(0.02f, side * 0.12f, -0.14f, side * 0.40f, stroke)
                canvas.drawLine(0.04f, side * 0.13f, 0.06f, side * 0.42f, stroke)
                canvas.drawLine(0.06f, side * 0.12f, 0.24f, side * 0.36f, stroke)
            }
        }
    }

    /** 苍蝇的躯干合并轮廓：腹部 + 胸部 + 头。描边/阴影层复用它。 */
    private fun flyBody(canvas: Canvas) {
        rect.set(-0.48f, -0.19f, 0.04f, 0.19f)
        canvas.drawOval(rect, fill)
        canvas.drawCircle(0.02f, 0f, 0.155f, fill)
        canvas.drawCircle(0.20f, 0f, 0.105f, fill)
    }

    // ───────────────────────────── 通用 ─────────────────────────────

    private fun drawWarningHalo(
        canvas: Canvas, c: Creature, s: Float,
        task: TaskItem, nowSec: Float, motion: Boolean,
    ) {
        val danger = Lifecycle.dangerOf(task.rot)
        val pulse = if (motion) 0.5f + 0.5f * sin(nowSec * 4.2f) else 0.75f
        val alpha = (60 + 110 * danger * pulse).toInt().coerceIn(0, 200)
        stroke.color = RotHigh.copy(alpha = alpha / 255f).toArgb()
        stroke.strokeWidth = s * 0.05f
        canvas.save()
        canvas.translate(c.x, c.y)
        canvas.rotate(Math.toDegrees(c.angle.toDouble()).toFloat())
        canvas.drawCircle(0f, 0f, s * 0.62f, stroke)
        canvas.restore()
    }

    /**
     * 单位坐标绘制：平移（屏幕空间）→ 旋转 → 缩放。
     * 形状函数只需在原点附近用单位尺寸描述。
     */
    private inline fun layer(
        canvas: Canvas,
        c: Creature,
        angleDeg: Float,
        scale: Float,
        dx: Float,
        dy: Float,
        color: Int,
        draw: () -> Unit,
    ) {
        canvas.save()
        canvas.translate(c.x + dx, c.y + dy)
        canvas.rotate(angleDeg)
        canvas.scale(scale, scale)
        fill.color = color
        draw()
        canvas.restore()
    }

    /** 出生时轻微弹性放大（easeOutBack），避免凭空出现。 */
    private fun spawnEase(v: Float): Float {
        if (v >= 1f) return 1f
        val t = v.coerceIn(0f, 1f)
        val c1 = 1.70158f
        val c3 = c1 + 1f
        val u = t - 1f
        return 1f + c3 * u * u * u + c1 * u * u
    }

    /** 由 seed 决定的确定性伪随机，保证同一只虫子的斑点不会每帧乱跳。 */
    private fun hash01(seed: Float, i: Int): Float {
        var h = (seed * 7919f).toInt() + i * 2654435761.toInt()
        h = h xor (h ushr 16)
        h *= 0x7feb352d
        h = h xor (h ushr 15)
        return ((h ushr 8) and 0xFFFF) / 65535f
    }

    private companion object {
        const val SHADOW = 0x4D000000
        const val HIGHLIGHT = 0x8CFFFFFF.toInt()
        const val SPECK = 0x998A7F5E.toInt()
        const val MOUTH = 0xE62A1F10.toInt()
        const val EYE = 0xFFB33A2E.toInt()
        const val LEG = 0xCC14180F.toInt()
    }
}
