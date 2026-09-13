package com.maggot.task.ui.theme

import androidx.compose.ui.graphics.Color
import com.maggot.task.core.Stage
import kotlin.math.roundToInt

/**
 * 设计系统 v1.0 的语义 token。**组件里不许出现裸 hex**，一律引用这里。
 * 所有正文配色的对比度已实测（见 docs/design-system.md 第 1 节）。
 */

// ── 表面：防腐液 / 标本缸 ──
val Void = Color(0xFF070906)
val Deep = Color(0xFF0C100A)
val SurfaceBg = Color(0xFF131A11)
val SurfaceHi = Color(0xFF1B2418)
val BorderCol = Color(0xFF2A3624)
val BorderHiCol = Color(0xFF3B4A32)

// ── 文字（对比度对 Deep 实测）──
val Fg = Color(0xFFE8EDE0)        // 15.1:1
val FgMuted = Color(0xFF9AA88C)   //  7.6:1
val FgSubtle = Color(0xFF7A8A6C)  //  5.2:1

// ── 生命阶段 ──
val EggCol = Color(0xFFF3EEDD)
val EggDeep = Color(0xFFCFC6AC)
val LarvaCol = Color(0xFFE9D9A3)
val LarvaDeep = Color(0xFFC4A85F)
val PupaCol = Color(0xFF7A4A24)
val PupaDeep = Color(0xFF4A2C14)
val FlyCol = Color(0xFF2E3A33)
val FlyRim = Color(0xFF8FD3C0)    // 没有它，苍蝇在暗背景上只有 1.7:1，等于隐形
val WingA = Color(0xFF6E7FC4)
val WingB = Color(0xFF4FA98E)

// ── 腐烂度渐变 ──
val RotLow = Color(0xFF7CB342)
val RotMid = Color(0xFFD4A017)
val RotHigh = Color(0xFFC0392B)

// ── 强调 ──
val Primary = Color(0xFF8ACE00)   // 对 Void 10.4:1
val OnPrimary = Color(0xFF0A0C08)
val SecondaryCol = Color(0xFF4FA98E)
val Success = Color(0xFF4FD1A5)
val Warn = Color(0xFFE0A32E)
val Danger = Color(0xFFD14B3A)

/** 生物描边：让它们在纯白网页上也不糊掉。 */
val CreatureOutline = Color(0x8C14180F)

/** 阶段 -> 展示色。 */
fun stageColor(stage: Stage): Color = when (stage) {
    Stage.EGG -> EggCol
    Stage.LARVA -> LarvaCol
    Stage.PUPA -> PupaCol
    Stage.FLY -> FlyRim
    Stage.DEAD -> FgSubtle
}

/**
 * 腐烂度 -> 颜色。0 是病态绿，中途转脓黄，接近苍蝇线变血红。
 * 这是整个产品最重要的一个视觉信号：**颜色自己在报警**。
 */
fun rotColor(rot: Float): Color {
    val t = (rot / 0.78f).coerceIn(0f, 1f)
    return if (t < 0.5f) {
        lerpColor(RotLow, RotMid, t / 0.5f)
    } else {
        lerpColor(RotMid, RotHigh, (t - 0.5f) / 0.5f)
    }
}

private fun lerpColor(a: Color, b: Color, t: Float): Color {
    val f = t.coerceIn(0f, 1f)
    return Color(
        red = a.red + (b.red - a.red) * f,
        green = a.green + (b.green - a.green) * f,
        blue = a.blue + (b.blue - a.blue) * f,
        alpha = 1f,
    )
}

/** 把 rot 渲染成整数百分比，UI 上到处都是它。 */
fun rotPercent(rot: Float): Int = (rot.coerceIn(0f, 1f) * 100).roundToInt()
