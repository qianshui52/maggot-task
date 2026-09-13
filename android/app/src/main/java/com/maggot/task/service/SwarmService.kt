package com.maggot.task.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.view.Gravity
import android.view.WindowManager
import android.widget.Toast
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.core.app.ServiceCompat
import com.maggot.task.AppGraph
import com.maggot.task.MaggotApp
import com.maggot.task.R
import com.maggot.task.diag.CrashLog
import com.maggot.task.notify.Notifier
import com.maggot.task.render.SwarmView
import com.maggot.task.ui.MaggotControlPanel
import com.maggot.task.ui.MainActivity
import com.maggot.task.ui.OverlayComposeView
import com.maggot.task.ui.theme.MaggotTheme

/**
 * 虫群的宿主：一个前台服务 + 两个 WindowManager 悬浮窗。
 *
 *  - 虫群层：全屏、`FLAG_NOT_TOUCHABLE`，所以鼠标/手指完全穿透，不挡任何操作。
 *  - 面板层：小窗、可触摸、可拖动；**后添加**因此永远盖在虫群层之上。
 *
 * 时间只有一条来源：这里的 [tick] 循环。可见时 60fps，隐藏时降到 10fps 省电。
 */
class SwarmService : Service() {

    private lateinit var graph: AppGraph
    private lateinit var wm: WindowManager
    private val handler = Handler(Looper.getMainLooper())

    private var swarmView: SwarmView? = null
    private var swarmParams: WindowManager.LayoutParams? = null
    private var panelView: OverlayComposeView? = null
    private var panelParams: WindowManager.LayoutParams? = null

    private var lastTickAt = 0L
    private var lastNotifAt = 0L

    /** 关掉穿透后自动恢复的时刻，避免用户被自己困在"虫群吃点击"的状态里。 */
    private var bypassUntil = 0L

    private val loop = object : Runnable {
        override fun run() {
            // ⚠️ 单帧异常绝不许炸掉进程：渲染循环一死，用户就回到
            // "面板还在屏上、虫群却全没了"的灵异状态。记下来、跳过这一帧。
            try {
                val now = SystemClock.uptimeMillis()
                val dt = if (lastTickAt == 0L) 0f
                else ((now - lastTickAt) / 1000f).coerceIn(0f, 0.5f)
                lastTickAt = now

                // graph.tick 里已经包含 simulation.advance —— 这里是唯一的一处推进。
                graph.tick(dt, System.currentTimeMillis())
                swarmView?.invalidate()
                graph.reportFrame()

                expireBypassIfNeeded(now)
                maybeUpdateNotification(now)
            } catch (t: Throwable) {
                runCatching { CrashLog.recordNonFatal(applicationContext, "tick", t) }
            }
            val attached = swarmView?.isAttachedToWindow == true
            handler.postDelayed(this, if (attached) FRAME_MS else IDLE_MS)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()

        // ⚠️ 整个 onCreate 必须"绝不抛异常"。
        //
        // Service.onCreate 抛出去 = 进程被系统直接杀掉，用户看到的现象是
        // "一点（让虫群上桌）就闪退"，而且连异常类名都留不下来。
        // 现在统一在这里收口：记录原因 → 报到控制台 → 自己停下（干净降级）。
        val g = runCatching { (application as MaggotApp).graph }.getOrElse { t ->
            CrashLog.recordNonFatal(this, "SwarmService.graph", t)
            stopSelf()
            return
        }
        graph = g
        wm = getSystemService(Context.WINDOW_SERVICE) as WindowManager

        // 前台服务通知。这一步在部分 ROM / 系统版本上会因为后台限制而抛 ——
        // 它失败只意味着"这个服务跑不了"，不该把整个应用陪葬。
        val fgError = runCatching { startForegroundSafely() }.exceptionOrNull()
        if (fgError != null) {
            CrashLog.recordNonFatal(this, "startForeground", fgError)
            toast("无法进入前台服务：${describe(fgError)}")
            stopSelf()
            return
        }

        // ⚠️ 两个悬浮窗必须**各自独立**地成败。
        //
        // 之前是"任一失败就把两个都拆掉、整个服务停掉"，于是一个控制面板的异常
        // 就能让全屏虫群层一起消失 —— 用户看到的现象是"有 4 只蛆，屏幕上什么都没有"，
        // 而且失败原因被 try/catch 吃掉，连日志都没有。
        // 现在：虫群层失败才算真失败；面板失败只降级，并把原因报到控制台上。
        val swarmError = runCatching { addSwarmWindow() }.exceptionOrNull()
        val panelError = if (swarmError == null) {
            runCatching { addPanelWindow() }.exceptionOrNull()
        } else {
            null
        }

        val ok = swarmError == null
        graph.reportOverlay(
            ok = ok,
            error = buildString {
                if (swarmError != null) append("虫群层 ").append(describe(swarmError))
                if (panelError != null) {
                    if (isNotEmpty()) append("；")
                    append("面板 ").append(describe(panelError))
                }
            },
        )

        if (!ok) {
            // 悬浮窗挂不上是**可预期**的（国产 ROM 的权限/后台策略），
            // 所以不算崩溃，只记一条非致命错误，等控制台里的兜底开关接手。
            CrashLog.recordNonFatal(this, "addSwarmWindow", swarmError!!)
            toast("虫群上桌失败：${describe(swarmError)}")
            stopSelf()
            return
        }

        if (panelError != null) CrashLog.recordNonFatal(this, "addPanelWindow", panelError)

        toast(
            if (panelError == null) "虫群已上桌 · ${graph.engine.tasks.size} 只"
            else "虫群已上桌，控制面板失败：${describe(panelError)}"
        )

        graph.serviceActive = true
        lastTickAt = 0L
        handler.post(loop)
        // 服务起来了也算"这次启动走完了"：应用可能只被后台拉起（START_STICKY），
        // 界面并不参与。不在这里标记的话，下次打开界面会误报"上次启动没走完"。
        CrashLog.markBootOk(this)
    }

    /**
     * 进入前台服务。
     *
     * 单独抽出来是为了让"通知渠道没建好 / 系统拒绝"这类可预期失败**只影响这一步**。
     * 通知内容本身是次要的：拿不到统计就退回一句固定文案，绝不能因此起不来。
     */
    private fun startForegroundSafely() {
        val notification = runCatching { graph.notifier.buildServiceNotification(graph.engine) }
            .getOrElse { android.app.Notification() }
        ServiceCompat.startForeground(
            this,
            Notifier.SERVICE_NOTIFICATION_ID,
            notification,
            if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0,
        )
    }

    private fun describe(t: Throwable): String =
        "${t.javaClass.simpleName}: ${t.message ?: "(无 message)"}"

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_TOGGLE_PAUSE -> {
                graph.updateConfig { it.running = !it.running }
            }
            ACTION_SHOW_PANEL -> setPanelVisible(true)
            ACTION_HIDE_PANEL -> setPanelVisible(false)
            else -> {
                // 普通启动（控制台点「让虫群上桌」）：服务可能已经在跑，
                // 这时它就是"把面板找回来"的入口 —— 面板收起后唯一的召回路径之一。
                setPanelVisible(true)
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        handler.removeCallbacks(loop)
        handler.removeCallbacks(viewportSelfHeal)
        // 交还时钟所有权，任务控制台会接手继续推进腐烂。
        graph.serviceActive = false
        // 悬浮层已经拆了，控制台上的体检卡必须跟着变 —— 但保留失败原因，别一起擦掉。
        // 给一个默认原因：被系统/用户停掉时，卡上不该只有一句没头没尾的「未上桌」。
        graph.markOverlayDown("服务已停止（被系统回收或手动停止）")
        removeOverlays()
        // 落盘放在最后，并且单独兜住：onDestroy 里抛异常同样会杀进程，
        // 而"存不上"绝不该等于"应用崩了"。
        runCatching { graph.saveBlocking() }
            .onFailure { CrashLog.recordNonFatal(this, "saveBlocking", it) }
        super.onDestroy()
    }

    // ────────────────────────── 悬浮窗 ──────────────────────────

    private fun addSwarmWindow() {
        val view = SwarmView(this, graph)
        view.motionEnabled = systemMotionEnabled()
        graph.motionEnabled = view.motionEnabled
        view.onCreatureTap = { id ->
            // 非穿透模式下短按一下 = 「我去做了」，非破坏性，不会误删任务。
            val t = graph.engine.find(id)
            if (t != null) {
                graph.care(id)
                Toast.makeText(this, "已照看「${t.title}」", Toast.LENGTH_SHORT).show()
            }
        }
        view.onCreatureInspect = { id ->
            // 长按 600ms = 看看这只是哪个任务（手机上没有悬停）。
            graph.engine.find(id)?.let {
                Toast.makeText(this, graph.describeTask(it), Toast.LENGTH_LONG).show()
            }
        }

        // ⚠️ 直接用显式像素尺寸，不用 MATCH_PARENT。
        //
        // vivo 等ROM 会把 MATCH_PARENT 的悬浮窗测量成 0×0 —— 视口被拒、
        // 所有虫被 enforceVisibility 夹到一个像素点上，"统计里有虫、屏幕上没虫"。
        // 之前靠 1.2 秒后的 viewportSelfHeal 兜底，但那 1.2 秒里虫群是不可见的，
        // 而且一旦服务在那之前被杀就永远没机会自愈。显式尺寸没有这个依赖。
        // 代价是旋转屏幕后尺寸陈旧 —— 可接受的取舍，自愈逻辑仍然保留。
        val dm = resources.displayMetrics
        val params = WindowManager.LayoutParams(
            dm.widthPixels,
            dm.heightPixels,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            swarmFlags(clickThrough = graph.engine.config.clickThrough),
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = 0
            y = 0
        }

        wm.addView(view, params)
        swarmView = view
        swarmParams = params
        handler.postDelayed(viewportSelfHeal, VIEWPORT_CHECK_MS)
    }

    private fun swarmFlags(clickThrough: Boolean): Int {
        // 刻意**不加** FLAG_LAYOUT_NO_LIMITS：那个标记允许窗口伸展到屏幕之外，
        // 会让"测量到的视口"和"用户真正看得见的那块区域"不再是同一件事 ——
        // 而 enforceVisibility 是拿视口尺寸当依据把虫往里夹的。
        // 虫群层只需要盖住屏幕，不需要画到屏幕外面去。
        var f = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
            WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
            WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED
        if (clickThrough) f = f or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        return f
    }

    /**
     * 视口自检。
     *
     * 有 ROM 会把 MATCH_PARENT 的悬浮窗测量成 0×0。那样的话 [SwarmSimulation.setViewport]
     * 会拒绝这个尺寸，虫群就一直停在默认的 1080×1920 上 —— 在真机上坐标全错。
     * 一秒多一点之后还没拿到真实尺寸，就改成显式像素尺寸再布局一次。
     */
    private val viewportSelfHeal = Runnable {
        val (w, h) = graph.swarmViewport
        if (w > 8 && h > 8) return@Runnable
        val view = swarmView ?: return@Runnable
        val params = swarmParams ?: return@Runnable
        val dm = resources.displayMetrics
        params.width = dm.widthPixels
        params.height = dm.heightPixels
        runCatching { wm.updateViewLayout(view, params) }
        // 部分 OEM 的 updateViewLayout 会把窗口重新压回 z 序顶端。
        // 虫群层一旦盖到面板之上（且穿透关闭），整屏触摸都会被它吃掉 ——
        // 表现恰好是"面板每个按钮都点不到"。每次动了虫群层就把面板顶回去。
        raisePanelAboveSwarm()
    }

    /** 把面板重新挂到虫群层之上。摘除 → 重挂；失败照旧留证据，绝不让它悄无声息。 */
    private fun raisePanelAboveSwarm() {
        val view = panelView ?: return
        val params = panelParams ?: return
        if (view.parent == null) return
        runCatching { wm.removeView(view) }
            .onFailure { t -> CrashLog.recordNonFatal(this, "raisePanel.remove", t) }
        runCatching { wm.addView(view, params) }
            .onFailure { t -> CrashLog.recordNonFatal(this, "raisePanel.add", t) }
    }

    private fun addPanelWindow() {
        val view = OverlayComposeView(this) {
            val ui by graph.ui.collectAsState()
            MaggotTheme {
                MaggotControlPanel(
                    ui = ui,
                    countdown = graph.countdownText(),
                    bypassSecondsLeft = remainingBypassSeconds(),
                    onDrag = ::dragPanel,
                    onToggleRun = {
                        // 每个回调都留一条面包屑：下一份诊断报告能直接分辨
                        // 「点击处理器没执行」和「执行了但界面没跟上」。
                        CrashLog.recordNote(this, "面板点击:暂停/启动虫群")
                        graph.updateConfig { it.running = !it.running }
                    },
                    onRotSpeed = { v ->
                        graph.updateConfig { it.rotSpeed = v }
                    },
                    onReset = {
                        CrashLog.recordNote(this, "面板点击:重置")
                        graph.resetAll()
                        graph.simulation.reset()
                        toast("虫群已重置，全部退回卵")
                    },
                    onClear = {
                        CrashLog.recordNote(this, "面板点击:清空")
                        graph.clearAll()
                        graph.simulation.reset()
                        toast("已清空，桌面干净了")
                    },
                    onOpenConsole = {
                        CrashLog.recordNote(this, "面板点击:打开任务控制台")
                        openConsole()
                    },
                    onHide = {
                        CrashLog.recordNote(this, "面板点击:收起")
                        setPanelVisible(false)
                        toast("面板已收起：下拉通知点「显示面板」，或再点一次「让虫群上桌」")
                    },
                    onToggleClickThrough = { want ->
                        CrashLog.recordNote(this, "面板点击:穿透点击=$want")
                        setClickThrough(want)
                    },
                    onToggleNotify = { want ->
                        CrashLog.recordNote(this, "面板点击:烂尾通知=$want")
                        graph.updateConfig { it.notifyOnFly = want }
                    },
                    onToggleSound = { want ->
                        CrashLog.recordNote(this, "面板点击:拍死音效=$want")
                        graph.updateConfig { it.soundEnabled = want }
                        // force：刚刚可能已经放过音效，但试听必须让用户听见。
                        if (want) graph.sfx.splat(force = true)
                    },
                )
            }
        }

        // ⚠️ 显式宽度，且**和用户实际的显示尺寸设置挂钩**。
        //
        // 内容根布局原来是固定 300dp —— 在"显示尺寸=大"的机器上（vivo 常见），
        // 300dp 能占到屏宽的 ~85%，面板看起来就是"占满桌面的巨窗"。
        // 现在：宽度 = min(280dp, 60% 屏宽)，高度交给 WRAP_CONTENT +
        // 内容里的 heightIn 封顶（内容自带滚动，超高部分在面板内滚）。
        val dm = resources.displayMetrics
        val density = dm.density
        val panelWidth = minOf((280 * density).toInt(), (dm.widthPixels * 0.60f).toInt())

        val params = WindowManager.LayoutParams(
            panelWidth,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            // FLAG_NOT_TOUCH_MODAL：面板边界之外的触摸放行给后面的窗口 ——
            // 否则这个窗口默认会吞掉全屏的触摸（touch-modal）。
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = panelPrefs().first
            y = panelPrefs().second
        }

        wm.addView(view, params)
        panelView = view
        panelParams = params
    }

    private fun removeOverlays() {
        swarmView?.let {
            runCatching { wm.removeView(it) }
                .onFailure { t -> CrashLog.recordNonFatal(this, "removeSwarm", t) }
        }
        swarmView = null
        swarmParams = null
        panelView?.let {
            if (it.parent != null) {
                runCatching { wm.removeView(it) }
                    .onFailure { t ->
                        // 摘不下来的面板会变成"孤儿窗"：服务死了它还杵在桌面上，
                        // 用户看到的正是这种没法关闭的鬼东西。必须留证据。
                        CrashLog.recordNonFatal(this, "removePanel", t)
                    }
            }
            it.destroy()
        }
        panelView = null
        panelParams = null
    }

    private fun setPanelVisible(visible: Boolean) {
        val view = panelView ?: return
        val params = panelParams ?: return
        // ⚠️ 用「真摘除 / 重挂」而不是 visibility=GONE：
        // GONE 的悬浮窗仍然占着原来的窗口矩形，只是看不见 —— 面积这么大，
        // 会变成一块看不见但**挡触摸**的区域，用户以为手机坏了。
        if (visible) {
            if (view.parent == null) runCatching { wm.addView(view, params) }
        } else {
            if (view.parent != null) runCatching { wm.removeView(view) }
        }
    }

    // ────────────────────────── 穿透切换 ──────────────────────────

    private fun setClickThrough(enabled: Boolean) {
        graph.updateConfig { it.clickThrough = enabled }
        val view = swarmView ?: return
        val params = swarmParams ?: return
        params.flags = swarmFlags(clickThrough = enabled)
        runCatching { wm.updateViewLayout(view, params) }
        // 同 viewportSelfHeal：动了虫群层就把面板顶回最上层，保证面板永远可点。
        raisePanelAboveSwarm()

        if (enabled) {
            bypassUntil = 0L
            toast("已恢复穿透，虫群不再挡操作")
        } else {
            // 45 秒后自动恢复，否则用户会被自己关在"点什么都没反应"的状态里。
            bypassUntil = SystemClock.uptimeMillis() + BYPASS_MS
            toast("已关闭穿透：点一下虫子＝「我去做了」。${BYPASS_MS / 1000}s 后自动恢复")
        }
    }

    private fun expireBypassIfNeeded(now: Long) {
        if (bypassUntil == 0L || now < bypassUntil) return
        bypassUntil = 0L
        setClickThrough(true)
    }

    private fun remainingBypassSeconds(): Long {
        if (bypassUntil == 0L) return 0L
        return ((bypassUntil - SystemClock.uptimeMillis()) / 1000L).coerceAtLeast(0L)
    }

    // ────────────────────────── 拖动面板 ──────────────────────────

    private fun dragPanel(dx: Float, dy: Float) {
        val params = panelParams ?: return
        val view = panelView ?: return
        val metrics = resources.displayMetrics
        params.x = (params.x + dx.toInt()).coerceIn(0, (metrics.widthPixels - view.width).coerceAtLeast(0))
        params.y = (params.y + dy.toInt()).coerceIn(0, (metrics.heightPixels - view.height).coerceAtLeast(0))
        runCatching { wm.updateViewLayout(view, params) }
        savePanelPos(params.x, params.y)
    }

    private fun panelPrefs(): Pair<Int, Int> {
        val p = getSharedPreferences(PREFS, MODE_PRIVATE)
        return p.getInt(KEY_X, 24) to p.getInt(KEY_Y, 180)
    }

    private fun savePanelPos(x: Int, y: Int) {
        getSharedPreferences(PREFS, MODE_PRIVATE).edit()
            .putInt(KEY_X, x).putInt(KEY_Y, y).apply()
    }

    // ────────────────────────── 杂项 ──────────────────────────

    private fun openConsole() {
        startActivity(
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
            }
        )
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()

    /** 系统"减少动态效果"开启时，生物静止、不扇翅、不爆浆。 */
    private fun systemMotionEnabled(): Boolean {
        val scale = Settings.Global.getFloat(
            contentResolver,
            Settings.Global.ANIMATOR_DURATION_SCALE,
            1f,
        )
        return scale > 0f
    }

    private fun maybeUpdateNotification(now: Long) {
        if (now - lastNotifAt < 5000L) return
        lastNotifAt = now

        // 用户随时可能在系统设置里切换"减少动态效果"，每 5 秒重新读一次。
        // 必须同时写到共享模拟上 —— 只写 View 的话，模拟照样在跑，
        // 结果是"静止"模式下虫子依旧乱爬、翅膀照扇。
        val motion = systemMotionEnabled()
        graph.motionEnabled = motion
        swarmView?.motionEnabled = motion

        runCatching {
            androidx.core.app.NotificationManagerCompat.from(this).notify(
                Notifier.SERVICE_NOTIFICATION_ID,
                graph.notifier.buildServiceNotification(graph.engine),
            )
        }
    }

    companion object {
        const val ACTION_STOP = "com.maggot.task.STOP"
        const val ACTION_TOGGLE_PAUSE = "com.maggot.task.TOGGLE_PAUSE"
        const val ACTION_SHOW_PANEL = "com.maggot.task.SHOW_PANEL"
        const val ACTION_HIDE_PANEL = "com.maggot.task.HIDE_PANEL"

        private const val FRAME_MS = 16L
        private const val IDLE_MS = 100L
        private const val BYPASS_MS = 45_000L
        /** 视口自检的等待时间：layout 正常的话几十毫秒就有了。 */
        private const val VIEWPORT_CHECK_MS = 1_200L
        private const val PREFS = "maggot_panel"
        private const val KEY_X = "panel_x"
        private const val KEY_Y = "panel_y"

        fun start(context: Context) {
            val i = Intent(context, SwarmService::class.java)
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(i)
            else context.startService(i)
        }

        fun stop(context: Context) {
            context.startService(
                Intent(context, SwarmService::class.java).setAction(ACTION_STOP)
            )
        }

        fun togglePause(context: Context) {
            context.startService(
                Intent(context, SwarmService::class.java).setAction(ACTION_TOGGLE_PAUSE)
            )
        }
    }
}
