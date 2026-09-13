package com.maggot.task.render

import com.maggot.task.core.LifecycleEngine
import com.maggot.task.core.Stage
import com.maggot.task.core.TaskItem
import com.maggot.task.core.TaskStatus
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.min
import kotlin.math.sin
import kotlin.random.Random

/** 一只生物的运行时状态。与 task 一对一，阶段变化时**位置不重置**（避免瞬移）。 */
class Creature(
    val id: String,
    var x: Float,
    var y: Float,
    var vx: Float = 0f,
    var vy: Float = 0f,
    var angle: Float = 0f,
    var wander: Float = 0f,
    var phase: Float = 0f,
    var seed: Float = 0f,
    var sizeScale: Float = 1f,
    var spawn: Float = 0f,
    var dartIn: Float = 0f,
    var turnIn: Float = 0f,
    var twitchIn: Float = 0f,
    var twitch: Float = 0f,
    var dying: Float = -1f,
    /** 出生以来经过的秒数。用来给"从屏幕外飞进来"留一段宽限期。 */
    var age: Float = 0f,
)

/** 爆浆粒子。 */
class Particle(
    var x: Float,
    var y: Float,
    var vx: Float,
    var vy: Float,
    var life: Float,
    val maxLife: Float,
    val size: Float,
)

/**
 * 全体生物的运行时。**只在主线程访问。**
 *
 * 坐标系统是屏幕像素；[density] 用来把设计稿尺寸换算成像素。
 */
class SwarmSimulation(
    private val rng: Random = Random.Default,
) {

    private val map = LinkedHashMap<String, Creature>()
    private val particles = ArrayList<Particle>()

    var width: Float = 1080f
    var height: Float = 1920f
    var density: Float = 3f
    var motionEnabled: Boolean = true

    /**
     * 生物基准直径 = 设计系统 §5 的 `size = 1.0`（22dp）。
     *
     * 这里必须是 `22 * density`。之前误乘了一个 0.55 的系数，把虫子缩到 12dp，
     * 在真机屏幕上小得像灰尘 —— 生物绘制里所有长度单位都是 dp，
     * 只有这一个地方负责 dp → px。
     */
    val baseSize: Float get() = 22f * density

    /** 直接暴露 value 视图，避免每帧 toList() 分配。绘制顺序 = 插入顺序。 */
    val all: Collection<Creature> get() = map.values
    val particleList: List<Particle> get() = particles
    fun find(id: String): Creature? = map[id]
    fun count(): Int = map.size

    // ────────────────────────── 同步 ──────────────────────────

    fun setViewport(w: Int, h: Int) {
        // ⚠️ 必须拒绝"退化视口"。
        //
        // 窗口在 layout 完成之前可能被测量成 0×0。如果照单全收，整个世界会被压到
        // 1×1 像素，所有生物都叠在屏幕左上角那一个像素上 —— 引擎统计里明明有 4 只蛆，
        // 画面上却一片空白。保持上一次（或默认）的尺寸，等真正的尺寸到来。
        if (w < MIN_VIEWPORT_PX || h < MIN_VIEWPORT_PX) return
        width = w.toFloat()
        height = h.toFloat()
        // 视口尺寸第一次确定（或旋转）时，把已经存在的生物拉进可视区。
        // 之前 spawn 时可能还在用默认尺寸，坐标是错的。
        for (c in map.values) {
            c.x = c.x.coerceIn(0f, width)
            c.y = c.y.coerceIn(0f, height)
        }
    }

    /**
     * 让生物集合与引擎里的任务对齐：新任务按阶段决定出生位置，完成/删除的消失。
     */
    fun sync(engine: LifecycleEngine) {
        val alive = aliveScratch
        alive.clear()
        for (task in engine.tasks) {
            if (task.status == TaskStatus.DONE) continue
            alive += task.id
            if (!map.containsKey(task.id)) {
                map[task.id] = spawn(task, engine.stageOf(task))
            }
        }
        map.keys.retainAll(alive)
    }

    private val aliveScratch = HashSet<String>()

    /**
     * 造一只新生物。
     *
     * ⚠️ **静止阶段（卵 / 蛹）必须直接落在屏幕内。**
     * 它们 `vx = vy = 0`，永远不会移动 —— 如果像会动的阶段那样出生在屏幕外，
     * 就会**永久停在屏幕外，用户根本看不到**。这正是「手机上有卵、屏幕上什么都没有」的根因：
     * 引擎统计显示有卵，画面上却空无一物。
     *
     * 会动的阶段（蛆 / 苍蝇）才从边缘外飞进来，且初始朝向指向屏幕中心，否则会一直往外爬。
     */
    private fun spawn(task: TaskItem, stage: Stage): Creature {
        val twoPi = 2f * PI.toFloat()
        val sessile = stage == Stage.EGG || stage == Stage.PUPA

        var x: Float
        var y: Float
        var facing: Float

        if (sessile) {
            // 留出边距，保证整个身体都在可视区内（生物是绕中心绘制的）。
            val pad = 48f * density
            x = pad + rng.nextFloat() * (width - 2f * pad).coerceAtLeast(1f)
            y = pad + rng.nextFloat() * (height - 2f * pad).coerceAtLeast(1f)
            facing = rng.nextFloat() * twoPi
        } else {
            val edge = rng.nextInt(4)
            val margin = 60f * density
            when (edge) {
                0 -> { x = rng.nextFloat() * width; y = -margin }
                1 -> { x = width + margin; y = rng.nextFloat() * height }
                2 -> { x = rng.nextFloat() * width; y = height + margin }
                else -> { x = -margin; y = rng.nextFloat() * height }
            }
            // 朝屏幕中心，否则会一直往外走。
            facing = kotlin.math.atan2(height * 0.5f - y, width * 0.5f - x)
        }

        return Creature(
            id = task.id,
            x = x,
            y = y,
            angle = facing,
            wander = facing,
            phase = rng.nextFloat() * 100f,
            seed = rng.nextFloat() * 1000f,
            sizeScale = 0.85f + rng.nextFloat() * 0.35f,
            dartIn = rng.nextFloat() * 2f,
            turnIn = rng.nextFloat() * 2f,
            twitchIn = 2f + rng.nextFloat() * 4f,
        )
    }

    /** 一只生物被拍死：在它所在位置炸开，然后从名单里删掉。 */
    fun burst(id: String) {
        val c = map.remove(id) ?: return
        val n = 22
        for (i in 0 until n) {
            val a = rng.nextFloat() * (2f * PI.toFloat())
            val sp = (90f + rng.nextFloat() * 320f) * density
            particles += Particle(
                x = c.x,
                y = c.y,
                vx = kotlin.math.cos(a) * sp,
                vy = kotlin.math.sin(a) * sp - 40f * density,
                life = 0.5f + rng.nextFloat() * 0.45f,
                maxLife = 0.95f,
                size = (1.5f + rng.nextFloat() * 3.5f) * density,
            )
        }
    }

    fun reset() {
        map.clear()
        particles.clear()
    }

    // ────────────────────────── 推进 ──────────────────────────

    fun advance(dt: Float, engine: LifecycleEngine) {
        sync(engine)
        for (task in engine.tasks) {
            val c = map[task.id] ?: continue
            if (task.status == TaskStatus.DONE) continue

            // ⚠️ 入场进度（spawn）与宽限期计时（age）**必须与动效开关无关**。
            //
            // 这两行原来写在 step() 里，而 step() 在系统开启「减少动态效果」时
            // 根本不会被调用 —— 于是 spawn 永远停在 0，而 spawnEase(0) 恰好等于 0，
            // 绘制层算出 size = 0 后直接 `return`：**整屏一只都不画**。
            // 与此同时体检卡仍报「悬浮层正常 · 生物 4 只」，因为窗口确实挂上了、
            // 视口也确实量到了、生物对象也确实存在。
            // 用户看到的就是那句最费解的话：「卡上说正常，但屏幕上什么都没有」。
            c.age += dt
            c.spawn = if (motionEnabled) {
                (c.spawn + dt * SPAWN_RATE).coerceAtMost(1f)
            } else {
                // 「减少动效」的语义是**不播入场动画**，不是「不出场」：
                // 直接以完整尺寸出现，静止但可见。
                1f
            }

            if (motionEnabled) {
                step(c, task, engine.stageOf(task), dt, task.rot)
            }
        }
        // 可见性兜底：无论动不动（reduced-motion 下 step 根本不会跑），
        // 都要保证没有生物被永久留在屏幕外。
        enforceVisibility(engine)
        advanceParticles(dt)
    }

    /**
     * 「出生后多久必须已经进场」的宽限期（秒）。
     *
     * 会动的阶段允许从屏幕外飞进来，但**不能无限期待在外面** ——
     * 苍蝇会随机急冲，偶尔会顶着边界转向力停在界外。
     */
    private val entryGraceSec = 2.0f

    /**
     * 摆放兜底。
     *
     * - 静止阶段（卵 / 蛹）**永远**夹在屏幕内：它们不会自己动，出界等于隐身。
     * - 会动的阶段过了宽限期后，把位置夹回可视区（留 12dp 内缩）——
     *   这样至少身体绝大部分在屏幕里，不会再出现"统计里有、画面里没有"。
     */
    private fun enforceVisibility(engine: LifecycleEngine) {
        for (task in engine.tasks) {
            if (task.status == TaskStatus.DONE) continue
            val c = map[task.id] ?: continue
            val stage = engine.stageOf(task)
            if (stage == Stage.EGG || stage == Stage.PUPA || !motionEnabled) {
                clampSessile(c)
            } else if (c.age > entryGraceSec) {
                // 夹到"身体基本在界内"而不是"中心在界内" —— 中心贴边意味着只有一半露出来，
                // 在密集的桌面上会被当成没有。
                val inset = 12f * density
                c.x = c.x.coerceIn(inset, (width - inset).coerceAtLeast(inset))
                c.y = c.y.coerceIn(inset, (height - inset).coerceAtLeast(inset))
            }
        }
    }

    private fun step(c: Creature, task: TaskItem, stage: Stage, dt: Float, rot: Float) {
        c.phase += dt * 60f
        // 注意：age / spawn **不在这里**推进（见 advance()）。
        // 放在这里的话，关闭动效会让它们彻底停住，生物会变成不可见。

        val s = baseSize * c.sizeScale

        when (stage) {
            Stage.EGG -> {
                // 卵完全不动，只有呼吸。可见性由 enforceVisibility 统一兜底。
                c.vx = 0f; c.vy = 0f
            }

            Stage.LARVA -> {
                // 慢速爬行：方向偶尔小幅改变。
                c.turnIn -= dt
                if (c.turnIn <= 0f) {
                    c.turnIn = 1.6f + rng.nextFloat() * 2.8f
                    c.angle += (rng.nextFloat() - 0.5f) * 1.5f
                }
                val speed = 6f * density * (0.7f + 0.6f * min(rot / 0.5f, 1f))
                c.vx = kotlin.math.cos(c.angle) * speed
                c.vy = kotlin.math.sin(c.angle) * speed
                c.x += c.vx * dt
                c.y += c.vy * dt
                steerInside(c, 34f * density, dt, 1.4f)
            }

            Stage.PUPA -> {
                c.vx = 0f; c.vy = 0f
                // 像死了，但每几秒抽一下 —— 这点微动很重要，否则会被当成渲染卡住。
                c.twitchIn -= dt
                if (c.twitchIn <= 0f) {
                    c.twitchIn = 3.5f + rng.nextFloat() * 2.5f
                    c.twitch = 0.12f
                }
                if (c.twitch > 0f) c.twitch = (c.twitch - dt).coerceAtLeast(0f)
            }

            Stage.FLY -> {
                // boids-lite：只求"烦人"，不做群体智能。
                c.wander += (rng.nextFloat() - 0.5f) * 3.4f * dt * 60f * 0.0166f
                val madness = min(rot, 1f)
                val target = 70f + 60f * madness
                val sp = target * density
                val dx = kotlin.math.cos(c.wander) * sp
                val dy = kotlin.math.sin(c.wander) * sp

                // 帧率无关的指数趋近
                val k = 1f - exp(-6f * dt)
                c.vx += (dx - c.vx) * k
                c.vy += (dy - c.vy) * k

                // 突然急冲 —— 苍蝇最烦人的就是这个
                c.dartIn -= dt
                if (c.dartIn <= 0f) {
                    c.dartIn = 0.8f + rng.nextFloat() * 2.2f
                    val a = rng.nextFloat() * (2f * PI.toFloat())
                    val power = (60f + 90f * madness) * density
                    c.vx += kotlin.math.cos(a) * power
                    c.vy += kotlin.math.sin(a) * power
                }

                steerInside(c, 70f * density, dt, 3.2f)

                c.x += c.vx * dt
                c.y += c.vy * dt
                if (abs(c.vx) + abs(c.vy) > 1f) {
                    c.angle = kotlin.math.atan2(c.vy, c.vx)
                }
            }

            Stage.DEAD -> Unit
        }
    }

    /**
     * 静止阶段（卵 / 蛹）每帧硬夹在屏幕内。
     *
     * 它们不会自己移动，所以**任何**把它们放到界外的路径都会导致「永久隐身」：
     * 出生位置、视口尺寸变化、以及「重置」把原来的苍蝇打回卵。
     * 夹取是唯一可靠的兜底 —— 这个 bug 已经真实发生过一次了。
     */
    private fun clampSessile(c: Creature) {
        val padX = 30f * density
        val padY = 30f * density
        val maxX = (width - padX).coerceAtLeast(padX)
        val maxY = (height - padY).coerceAtLeast(padY)
        c.x = c.x.coerceIn(padX, maxX)
        c.y = c.y.coerceIn(padY, maxY)
    }

    /** 靠近边界就往回推，避免虫子卡在屏幕外。 */
    private fun steerInside(c: Creature, margin: Float, dt: Float, force: Float) {
        val f = force * density * 60f
        if (c.x < margin) c.vx += f * dt * (1f - c.x / margin).coerceIn(0f, 1f)
        if (c.x > width - margin) c.vx -= f * dt * (1f - (width - c.x) / margin).coerceIn(0f, 1f)
        if (c.y < margin) c.vy += f * dt * (1f - c.y / margin).coerceIn(0f, 1f)
        if (c.y > height - margin) c.vy -= f * dt * (1f - (height - c.y) / margin).coerceIn(0f, 1f)
        c.x = c.x.coerceIn(-margin, width + margin)
        c.y = c.y.coerceIn(-margin, height + margin)
    }

    private fun advanceParticles(dt: Float) {
        if (particles.isEmpty()) return
        val it = particles.iterator()
        while (it.hasNext()) {
            val p = it.next()
            p.life -= dt
            if (p.life <= 0f) { it.remove(); continue }
            p.x += p.vx * dt
            p.y += p.vy * dt
            p.vy += 520f * density * dt   // 重力
            p.vx *= (1f - 2.2f * dt).coerceIn(0f, 1f)
            p.vy *= (1f - 0.9f * dt).coerceIn(0f, 1f)
        }
    }

    /** 给绘制层用的蠕动偏移。 */
    fun wriggle(c: Creature, segment: Int, amp: Float): Float =
        if (!motionEnabled) 0f else amp * sin(c.phase * 0.06f + segment * 0.9f)

    /**
     * 一行诊断：生物数量 + 第一只的实际坐标。
     *
     * 排查"引擎里有虫、屏幕上没有"时，这行字是唯一能区分
     * 「摆放错」还是「窗口根本没显示」的证据。
     */
    fun debugLine(): String {
        val first = map.values.firstOrNull() ?: return "n=0"
        return "n=${map.size} vp=${width.toInt()}x${height.toInt()} " +
            "p0=${first.x.toInt()},${first.y.toInt()} age=${"%.1f".format(first.age)}"
    }

    companion object {
        /** 小于这个尺寸的视口一律视为"还没 layout 完"，直接忽略。 */
        private const val MIN_VIEWPORT_PX = 8

        /** 入场缩放速度（每秒）。约 0.31 秒从 0 长到 1，与动效开关无关。 */
        private const val SPAWN_RATE = 3.2f
    }
}
