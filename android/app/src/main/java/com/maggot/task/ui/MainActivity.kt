package com.maggot.task.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.provider.Settings
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
// 注意：必须给 AndroidX 的 Lifecycle 起别名 —— com.maggot.task.core.Lifecycle
// （生命周期引擎的常量对象）和它重名，两个都导入会直接编译失败。
import androidx.lifecycle.Lifecycle as AndroidLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import com.maggot.task.SwarmDiag
import com.maggot.task.UiSnapshot
import com.maggot.task.appGraph
import com.maggot.task.core.Lifecycle
import com.maggot.task.core.Stage
import com.maggot.task.core.TaskItem
import com.maggot.task.core.TaskStatus
import com.maggot.task.core.WeekClock
import com.maggot.task.diag.CrashLog
import com.maggot.task.render.SwarmView
import com.maggot.task.service.SwarmService
import com.maggot.task.ui.theme.BorderCol
import com.maggot.task.ui.theme.BorderHiCol
import com.maggot.task.ui.theme.Danger
import com.maggot.task.ui.theme.Deep
import com.maggot.task.ui.theme.Fg
import com.maggot.task.ui.theme.FgMuted
import com.maggot.task.ui.theme.FgSubtle
import com.maggot.task.ui.theme.MaggotTheme
import com.maggot.task.ui.theme.OnPrimary
import com.maggot.task.ui.theme.Primary
import com.maggot.task.ui.theme.SecondaryCol
import com.maggot.task.ui.theme.Success
import com.maggot.task.ui.theme.SurfaceBg
import com.maggot.task.ui.theme.SurfaceHi
import com.maggot.task.ui.theme.Warn
import com.maggot.task.ui.theme.rotColor
import com.maggot.task.ui.theme.stageColor
import kotlinx.coroutines.delay

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // ⚠️ 顺序是硬约束：**先读上一次的死亡记录，再标记本次启动开始**。
        //
        // 1.2.4 把 markBootStart 放在了最前面，于是 readUnexpectedDeath 读到的
        // 永远是本次刚写入的 starting —— 每次冷启动都误报「上次启动没有走完」，
        // 而且点「清除并进入应用」→ recreate → 又先 mark 再 read → 回到同一页，
        // 用户看到的就是「点了没反应」。读序颠倒已修复，CrashLog 侧也改成了
        // 一次性消费语义，两边互为保险。
        val fatal = CrashLog.readFatal(this)
        // 没有 Java 堆栈时才去看"是不是被系统中断的" —— 有堆栈就以堆栈为准。
        val oddDeath = if (fatal == null) CrashLog.readUnexpectedDeath(this) else null
        CrashLog.markBootStart(this)
        if (fatal != null || oddDeath != null) {
            setContentView(
                buildCrashScreen(
                    title = if (fatal != null) "上次运行崩溃了" else "上次启动没有走完",
                    report = fatal ?: oddDeath!!,
                    onDismiss = {
                        CrashLog.clearFatal(this)
                        // 清掉标记：用户已经看过报告了，别再每次启动都弹。
                        CrashLog.markBootOk(this)
                        recreate()
                    },
                )
            )
            return
        }

        // Activity 在 Activity 作用域里取全局容器；它和 Service 用的是同一个引擎实例。
        val graph = appGraph

        setContent {
            MaggotTheme {
                // 悬浮服务没在跑的时候，由任务控制台接手驱动腐烂时钟，
                // 否则用户在这个界面里会看到腐烂进度一动不动（像卡死了）。
                val lifecycleOwner = LocalLifecycleOwner.current
                LaunchedEffect(Unit) {
                    lifecycleOwner.repeatOnLifecycle(AndroidLifecycle.State.RESUMED) {
                        var last = SystemClock.uptimeMillis()
                        while (true) {
                            delay(50)
                            val now = SystemClock.uptimeMillis()
                            val dt = ((now - last) / 1000f).coerceIn(0f, 0.5f)
                            last = now
                            if (!graph.serviceActive) graph.tick(dt, System.currentTimeMillis())
                        }
                    }
                }

                val ui by graph.ui.collectAsStateWithLifecycle()
                ConsoleScreen(
                    ui = ui,
                    onAdd = { graph.addTask(it) },
                    onCare = { graph.care(it) },
                    onComplete = { graph.complete(it) },
                    onDelete = { graph.delete(it) },
                    onReset = { graph.resetAll() },
                    onClear = { graph.clearAll() },
                    onAddDaily = { graph.addDailyTemplate(it) },
                    onToggleDaily = { id, on -> graph.setDailyTemplateEnabled(id, on) },
                    onRemoveDaily = { graph.removeDailyTemplate(it) },
                    onAddWeekly = { title, wd -> graph.addWeeklyTemplate(title, wd) },
                    onToggleWeekly = { id, on -> graph.setWeeklyTemplateEnabled(id, on) },
                    onWeekdayWeekly = { id, wd -> graph.setWeeklyTemplateWeekday(id, wd) },
                    onRemoveWeekly = { graph.removeWeeklyTemplate(it) },
                )
            }
        }
    }
}

/**
 * 崩溃报告界面 —— 纯 Android View，**刻意不用 Compose、不用主题、不用自定义字体**。
 *
 * 它是崩溃之后才显示的，所以任何"正常界面用到的依赖"都可能是崩溃本身的原因。
 * 一个 LinearLayout + ScrollView + TextView 是安卓上最不容易出错的东西。
 *
 * 正文设为**可选中**：用户长按就能复制全文发给我，这比截图有用得多
 * （堆栈里的类名、行号能被直接搜索）。
 */
private fun Context.buildCrashScreen(title: String, report: String, onDismiss: () -> Unit): View {
    val density = resources.displayMetrics.density
    fun dp(v: Int) = (v * density).toInt()

    val titleView = TextView(this).apply {
        this.text = title
        textSize = 18f
        typeface = android.graphics.Typeface.DEFAULT_BOLD
        setTextColor(android.graphics.Color.parseColor("#FF6B6B"))
    }
    val hint = TextView(this).apply {
        this.text = "下面是详情，长按可复制。若要只看重点，就找最后一条 “Caused by:” ——" +
            "真正的元凶通常在那里。"
        textSize = 12f
        setTextColor(android.graphics.Color.parseColor("#9AA4B2"))
        setPadding(0, dp(6), 0, dp(12))
    }
    val body = TextView(this).apply {
        this.text = report
        textSize = 11f
        typeface = android.graphics.Typeface.MONOSPACE
        setTextColor(android.graphics.Color.parseColor("#D8DEE9"))
        setTextIsSelectable(true)
    }
    val path = TextView(this).apply {
        this.text = "同一份报告也在：\n${CrashLog.fatalPath(this@buildCrashScreen)}"
        textSize = 10f
        setTextColor(android.graphics.Color.parseColor("#6B7280"))
        setPadding(0, dp(12), 0, 0)
    }

    val inner = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        addView(titleView, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
        addView(hint, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
        addView(body, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
        addView(path, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
    }
    val scroll = ScrollView(this).apply {
        setPadding(dp(16), dp(20), dp(16), dp(8))
        addView(inner)
    }

    val copyBtn = Button(this).apply {
        this.text = "复制全文"
        setOnClickListener {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("maggot-crash", report))
            Toast.makeText(this@buildCrashScreen, "已复制，粘贴发我即可", Toast.LENGTH_SHORT).show()
        }
    }
    val dismissBtn = Button(this).apply {
        this.text = "清除并进入应用"
        setOnClickListener { onDismiss() }
    }
    val bar = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        setPadding(dp(16), dp(8), dp(16), dp(20))
        addView(copyBtn, LinearLayout.LayoutParams(0, dp(48), 1f))
        addView(View(this@buildCrashScreen), LinearLayout.LayoutParams(dp(10), 1))
        addView(dismissBtn, LinearLayout.LayoutParams(0, dp(48), 1f))
    }

    return LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setBackgroundColor(android.graphics.Color.parseColor("#101216"))
        addView(scroll, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
        addView(bar, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
    }
}

/**
 * 一个**不参与 Compose 状态**的 View 引用槽。
 *
 * 为什么不用 `mutableStateOf`：AndroidView 的 factory 是在组合阶段被调用的，
 * 在那里写 Compose state 只会白白引入一次额外重组 —— 而我们要的仅仅是
 * "拿到引用好去调 invalidate()"，这跟界面状态没有关系。
 */
private class ViewSlot {
    var view: SwarmView? = null
}

@Composable
private fun ConsoleScreen(
    ui: UiSnapshot,
    onAdd: (String) -> Unit,
    onCare: (String) -> Unit,
    onComplete: (String) -> Unit,
    onDelete: (String) -> Unit,
    onReset: () -> Unit,
    onClear: () -> Unit,
    onAddDaily: (String) -> Unit,
    onToggleDaily: (String, Boolean) -> Unit,
    onRemoveDaily: (String) -> Unit,
    onAddWeekly: (String, Int) -> Unit,
    onToggleWeekly: (String, Boolean) -> Unit,
    onWeekdayWeekly: (String, Int) -> Unit,
    onRemoveWeekly: (String) -> Unit,
) {
    val context = LocalContext.current
    val graph = context.appGraph

    // 组合真正跑通 = 这次启动"走完了"。放在 LaunchedEffect 里而不是 onCreate 里，
    // 是因为它**在组合成功之后**才执行：如果组合本身崩了，这一行不会被执行，
    // 而下次启动的"启动没走完"判定正好需要这个区别。
    LaunchedEffect(Unit) { CrashLog.markBootOk(context) }
    var draft by remember { mutableStateOf("") }
    var overlayGranted by remember { mutableStateOf(canDrawOverlays(context)) }
    var notifyGranted by remember {
        mutableStateOf(
            Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(
                context, Manifest.permission.POST_NOTIFICATIONS
            ) == PackageManager.PERMISSION_GRANTED
        )
    }
    var confirmingClear by remember { mutableStateOf(false) }

    /**
     * 应用内虫群兜底层。
     *
     * 悬浮窗能不能上桌由系统决定 —— 国产 ROM 各种拦截都可能让
     * `WindowManager.addView` 直接抛异常。那种情况下如果什么都没了，
     * 产品就等于完全失效。所以这里铺一层同样的虫群在控制台底下：
     * 虫子至少是看得见的，产品的主承诺还在。
     */
    // 开关状态要落盘：用户之所以会打开它，正是因为桌面上根本看不到虫群 ——
    // 每冷启动一次就让他再翻一次菜单是不可接受的。
    val uiPrefs = remember {
        context.getSharedPreferences(PREFS_UI, android.content.Context.MODE_PRIVATE)
    }
    var inAppSwarm by remember {
        mutableStateOf(uiPrefs.getBoolean(KEY_IN_APP_SWARM, false))
    }
    val swarmSlot = remember { ViewSlot() }

    // 只在**真的失败**时自动兜底（权限被拒、ROM 拒绝挂载……）。
    // 用户主动停掉虫群不算失败 —— 那种情况下别擅自把虫子塞回控制台里。
    LaunchedEffect(ui.swarm.overlayError) {
        if (ui.swarm.overlayError.isNotEmpty()) inAppSwarm = true
    }

    LaunchedEffect(inAppSwarm) {
        uiPrefs.edit().putBoolean(KEY_IN_APP_SWARM, inAppSwarm).apply()
    }

    // 兜底层不在屏幕上时，AppGraph 就不必为它每帧算一遍生物位置。
    DisposableEffect(inAppSwarm) {
        graph.fallbackVisible = inAppSwarm
        onDispose {
            graph.fallbackVisible = false
            swarmSlot.view = null
        }
    }

    // 兜底层是普通 View，不会自己重绘；跟着 Compose 的帧时钟手动 invalidate。
    LaunchedEffect(inAppSwarm) {
        if (!inAppSwarm) return@LaunchedEffect
        while (true) {
            withFrameNanos { }
            swarmSlot.view?.invalidate()
        }
    }

    val notifyLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { notifyGranted = it }

    val alive = ui.tasks.filter { it.status != TaskStatus.DONE }.sortedByDescending { it.rot }
    val squashed = ui.tasks.filter { it.status == TaskStatus.DONE }
        .sortedByDescending { it.completedAt ?: 0L }

    if (confirmingClear) {
        AlertDialog(
            onDismissRequest = { confirmingClear = false },
            title = { Text("清空全部任务？", color = Fg) },
            text = {
                Text(
                    "将永久删除 ${alive.size + squashed.size} 个任务，无法撤销。",
                    color = FgMuted,
                )
            },
            confirmButton = {
                TextButton(onClick = { confirmingClear = false; onClear() }) {
                    Text("确认清空", color = Danger)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmingClear = false }) {
                    Text("取消", color = FgMuted)
                }
            },
            containerColor = SurfaceHi,
        )
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Deep),
    ) {
        if (inAppSwarm) {
            AndroidView(
                modifier = Modifier.fillMaxSize(),
                factory = { ctx ->
                    SwarmView(ctx, graph, ownSimulation = graph.fallbackSimulation).apply {
                        // 这一层铺满全屏，绝不能吃触摸，否则点空白处会误触「我去做了」。
                        interactive = false
                        swarmSlot.view = this
                    }
                },
            )
        }

        // ⚠️ 整页只有 LazyColumn 一个滚动面。
        //
        // 1.2.9 曾在外面套过 verticalScroll —— 外层滚动 + 内层 LazyColumn
        // 嵌套打架，测量塌陷，「每周任务」直接和底部按钮叠在一起。
        // 现在顶部所有内容（标题/卡片/按钮/输入框）都搬进 LazyColumn 当 item，
        // 每日/每周/任务列表自然跟在后面，一滚到底；「重置腐烂/清空全部」
        // 固定在底部不随滚动。
        Column(
            modifier = Modifier
                .fillMaxSize()
                .imePadding()
                .padding(horizontal = 16.dp),
        ) {
            Spacer(Modifier.height(24.dp))

            LazyColumn(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                item {
                    Column {
                        Text("蛆虫任务", style = MaterialTheme.typography.displaySmall, color = Fg)
                        Text(
                            "MAGGOT TASK · 拖延的终点是苍蝇",
                            style = MaterialTheme.typography.labelLarge,
                            color = FgSubtle,
                        )
                    }
                }

                // 冷启动没恢复成功时，这条必须在最显眼的位置 ——
                // 否则用户看到的是一个"空的控制台"，会以为任务全丢了。
                if (ui.startupError.isNotEmpty()) {
                    item {
                        StartupErrorCard(
                            message = ui.startupError,
                            bootMillis = ui.bootMillis,
                            onDismiss = { graph.clearStartupError() },
                        )
                    }
                }

                if (!overlayGranted) {
                    item {
                        PermissionCard(
                            title = "还没有悬浮窗权限",
                            body = "没有它，虫子只能待在应用里，爬不到你的桌面上。",
                            onAction = {
                                runCatching {
                                    context.startActivity(
                                        Intent(
                                            Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                                            Uri.parse("package:${context.packageName}"),
                                        )
                                    )
                                }
                                // 从设置页回来后重新读一次授权状态。
                                overlayGranted = canDrawOverlays(context)
                            },
                        )
                    }
                }
                if (!notifyGranted) {
                    item {
                        PermissionCard(
                            title = "通知权限未开启",
                            body = "任务烂尾成苍蝇时，就没法第一时间提醒你了。",
                            onAction = {
                                if (Build.VERSION.SDK_INT >= 33) {
                                    notifyLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                                }
                            },
                        )
                    }
                }

                item { StatsCard(ui) }

                item {
                    SwarmStatusCard(
                        diag = ui.swarm,
                        inAppSwarm = inAppSwarm,
                        onToggleInApp = { inAppSwarm = it },
                    )
                }

                item {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        BigButton(
                            label = if (ui.config.running) "暂停虫群" else "启动虫群",
                            container = if (ui.config.running) SurfaceHi else Primary,
                            content = if (ui.config.running) Fg else OnPrimary,
                            modifier = Modifier.weight(1f),
                            onClick = { SwarmService.togglePause(context) },
                        )
                        BigButton(
                            label = "让虫群上桌",
                            container = SurfaceHi,
                            content = Fg,
                            modifier = Modifier.weight(1f),
                            onClick = {
                                if (canDrawOverlays(context)) {
                                    SwarmService.start(context)
                                } else {
                                    overlayGranted = false
                                }
                            },
                        )
                    }
                }

                item {
                    OutlinedTextField(
                        value = draft,
                        onValueChange = { draft = it.take(80) },
                        modifier = Modifier.fillMaxWidth(),
                        placeholder = { Text("新建一个任务…它会从卵开始", color = FgSubtle) },
                        singleLine = true,
                        shape = RoundedCornerShape(14.dp),
                        colors = TextFieldDefaults.colors(
                            focusedContainerColor = SurfaceBg,
                            unfocusedContainerColor = SurfaceBg,
                            focusedTextColor = Fg,
                            unfocusedTextColor = Fg,
                            cursorColor = Primary,
                            focusedIndicatorColor = Primary,
                            unfocusedIndicatorColor = BorderCol,
                            focusedPlaceholderColor = FgSubtle,
                            unfocusedPlaceholderColor = FgSubtle,
                        ),
                        trailingIcon = {
                            Box(
                                modifier = Modifier
                                    .heightIn(min = 48.dp)
                                    .clickable(enabled = draft.isNotBlank()) {
                                        onAdd(draft)
                                        draft = ""
                                    }
                                    .padding(horizontal = 12.dp),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    text = "产卵",
                                    style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.Medium),
                                    color = if (draft.isBlank()) FgSubtle else Primary,
                                )
                            }
                        },
                    )
                }

                item {
                    DailySection(
                        ui = ui,
                        onAddDaily = onAddDaily,
                        onToggleDaily = onToggleDaily,
                        onRemoveDaily = onRemoveDaily,
                    )
                }

                item {
                    WeeklySection(
                        ui = ui,
                        onAddWeekly = onAddWeekly,
                        onToggleWeekly = onToggleWeekly,
                        onWeekdayWeekly = onWeekdayWeekly,
                        onRemoveWeekly = onRemoveWeekly,
                    )
                }

                item { SectionLabel("活着 ${alive.size} 个") }
                if (alive.isEmpty()) {
                    item { EmptyHint("还没有任务。新建一个，然后开始拖。") }
                }
                items(alive, key = { it.id }) { task ->
                    TaskRow(
                        task = task,
                        todayKey = ui.todayKey,
                        weekKey = ui.weekKey,
                        onCare = { onCare(task.id) },
                        onComplete = { onComplete(task.id) },
                        onDelete = { onDelete(task.id) },
                    )
                }

                if (squashed.isNotEmpty()) {
                    item {
                        Spacer(Modifier.height(10.dp))
                        SectionLabel("战果 ${squashed.size} 只（已拍死）")
                    }
                    items(squashed, key = { it.id }) { task ->
                        SquashedRow(task, onDelete = { onDelete(task.id) })
                    }
                }

                item { Spacer(Modifier.height(16.dp)) }
            }

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 8.dp, bottom = 20.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                BigButton("重置腐烂", SurfaceHi, Fg, Modifier.weight(1f), onClick = onReset)
                BigButton("清空全部", SurfaceBg, Danger, Modifier.weight(1f)) { confirmingClear = true }
            }
        }
    }
}

// ────────────────────────────── 组件 ──────────────────────────────

@Composable
private fun SectionLabel(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelLarge,
        color = FgSubtle,
        modifier = Modifier.padding(bottom = 2.dp),
    )
}

@Composable
private fun EmptyHint(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.bodyMedium,
        color = FgSubtle,
        modifier = Modifier.padding(vertical = 10.dp),
    )
}

@Composable
private fun PermissionCard(title: String, body: String, onAction: () -> Unit) {
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, Warn.copy(alpha = 0.45f), RoundedCornerShape(18.dp)),
        color = SurfaceBg,
    ) {
        Column(Modifier.padding(14.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, color = Warn)
            Spacer(Modifier.height(4.dp))
            Text(body, style = MaterialTheme.typography.bodyMedium, color = FgMuted)
            Spacer(Modifier.height(10.dp))
            BigButton("去授权", Primary, OnPrimary, Modifier.fillMaxWidth(), onAction)
        }
    }
}

/**
 * 冷启动失败的告示。
 *
 * 为什么需要它：`Application.onCreate` 里恢复状态失败，以前**整个进程就没了** ——
 * 用户看到的是"点进去就闪退"，我们能拿到的信息是零。现在失败会降级，
 * 于是必须把"为什么降级"明确写在界面上，否则用户会以为任务全丢了。
 *
 * 正文可选中：用户长按复制就能把真实堆栈发出来，不用猜、也不用连数据线。
 */
@Composable
private fun StartupErrorCard(message: String, bootMillis: Long, onDismiss: () -> Unit) {
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, Danger.copy(alpha = 0.55f), RoundedCornerShape(18.dp)),
        color = SurfaceBg,
    ) {
        Column(Modifier.padding(14.dp)) {
            Text("冷启动没恢复成功", style = MaterialTheme.typography.titleMedium, color = Danger)
            Spacer(Modifier.height(4.dp))
            Text(
                text = "任务数据没有读进来，所以下面的列表是空的。数据并没有丢，" +
                    "重新打开应用会再试一次（本次耗时 ${bootMillis}ms）。",
                style = MaterialTheme.typography.bodyMedium,
                color = FgMuted,
            )
            Spacer(Modifier.height(8.dp))
            SelectionContainer {
                Text(
                    text = message,
                    style = MaterialTheme.typography.labelMedium,
                    color = Danger,
                )
            }
            Spacer(Modifier.height(10.dp))
            BigButton("我知道了", SurfaceHi, Fg, Modifier.fillMaxWidth(), onDismiss)
        }
    }
}

@Composable
private fun StatsCard(ui: UiSnapshot) {
    val s = ui.stats
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, BorderCol, RoundedCornerShape(18.dp)),
        color = SurfaceBg,
    ) {
        Column(Modifier.padding(14.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                StatCell(Stage.EGG, s.egg)
                StatCell(Stage.LARVA, s.larva)
                StatCell(Stage.PUPA, s.pupa)
                StatCell(Stage.FLY, s.fly)
            }
            Spacer(Modifier.height(12.dp))
            KeyValueRow(
                key = "整体腐烂度",
                value = "${(s.averageRot.coerceIn(0f, 1f) * 100).toInt()}%",
                valueColor = rotColor(s.averageRot),
            )
            Spacer(Modifier.height(4.dp))
            KeyValueRow(
                key = "血脉世代",
                value = if (ui.maxGeneration == 0) "原生" else "第 ${ui.maxGeneration} 代",
                valueColor = if (ui.maxGeneration > 0) Warn else FgMuted,
            )
            if (ui.bredTotal > 0) {
                Spacer(Modifier.height(4.dp))
                KeyValueRow(
                    key = "繁殖次数",
                    value = "${ui.bredTotal} 次",
                    valueColor = Warn,
                )
            }
        }
    }
}

/**
 * 虫群体检卡。
 *
 * 悬浮窗活在"另一个窗口层面"：它在不在、多大、有没有在画，应用里原本完全看不出来 ——
 * 用户只能看到"统计里有 4 只蛆，屏幕上什么都没有"。
 * 把这三个数字（视口 / 生物数 / 帧计数）摆到台面上之后，一张截图就能定位到
 * 是窗口没建起来、视口算错了、还是时钟压根没跑。
 */
@Composable
private fun SwarmStatusCard(
    diag: SwarmDiag,
    inAppSwarm: Boolean,
    onToggleInApp: (Boolean) -> Unit,
) {
    val dot = when {
        diag.mismatched -> Danger
        !diag.overlayOk -> Danger
        diag.degenerateViewport || diag.frames == 0L -> Warn
        else -> Success
    }
    val label = when {
        diag.mismatched -> "同步异常：引擎里有虫，模拟里 0 只"
        !diag.overlayOk -> "悬浮层未上桌"
        diag.degenerateViewport -> "已挂载，但视口是 0"
        diag.frames == 0L -> "已挂载，但时钟没跑"
        diag.aliveTasks == 0 -> "正常（当前没有任务）"
        else -> "悬浮层正常"
    }
    // 只有当"窗口确实挂上了、也确实有虫可画"时，才提示可能是 ROM 没合成这一层。
    val overlaySuspicious =
        diag.overlayOk && !diag.degenerateViewport && diag.frames > 0 && diag.creatures > 0

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, BorderCol, RoundedCornerShape(18.dp)),
        color = SurfaceBg,
    ) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(7.dp).clip(CircleShape).background(dot))
                Spacer(Modifier.width(7.dp))
                Text(
                    text = "虫群 · $label",
                    style = MaterialTheme.typography.titleMedium,
                    color = Fg,
                )
                Spacer(Modifier.weight(1f))
                Text(
                    text = "${diag.creatures} 只",
                    style = MaterialTheme.typography.labelMedium,
                    color = if (diag.creatures > 0) FgMuted else FgSubtle,
                )
            }
            Spacer(Modifier.height(6.dp))
            Text(
                text = "视口 ${diag.viewportW}×${diag.viewportH} · 帧 ${diag.frames} · ${diag.firstAt}",
                style = MaterialTheme.typography.labelMedium,
                color = FgSubtle,
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = "引擎存活 ${diag.aliveTasks} · 模拟 ${diag.creatures}" +
                    " · 动效 ${if (diag.motionOff) "已关闭（生物静止）" else "开启"}",
                style = MaterialTheme.typography.labelMedium,
                color = if (diag.mismatched) Danger else FgSubtle,
            )
            if (overlaySuspicious && !inAppSwarm) {
                Spacer(Modifier.height(6.dp))
                Text(
                    text = "窗口已挂载、也已测量到视口。如果桌面上仍然看不到虫群，" +
                        "说明系统没有合成这个悬浮层（部分 ROM 的省电/后台策略会这样）—— " +
                        "打开下面的开关即可在控制台里看到虫群。",
                    style = MaterialTheme.typography.labelMedium,
                    color = Warn,
                )
            }
            if (diag.overlayError.isNotEmpty()) {
                Spacer(Modifier.height(4.dp))
                Text(
                    text = diag.overlayError,
                    style = MaterialTheme.typography.labelMedium,
                    color = Danger,
                )
            }
            Spacer(Modifier.height(8.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text(
                        text = "在应用内显示虫群",
                        style = MaterialTheme.typography.bodyMedium,
                        color = FgMuted,
                    )
                    Text(
                        text = "桌面上看不到虫群时打开 —— 虫群改画在控制台里，不影响桌面显示",
                        style = MaterialTheme.typography.labelMedium,
                        color = FgSubtle,
                    )
                }
                Switch(
                    checked = inAppSwarm,
                    onCheckedChange = onToggleInApp,
                    colors = SwitchDefaults.colors(
                        checkedThumbColor = OnPrimary,
                        checkedTrackColor = Primary,
                        uncheckedThumbColor = FgMuted,
                        uncheckedTrackColor = SurfaceHi,
                        uncheckedBorderColor = BorderHiCol,
                    ),
                )
            }
            // 远程排障的取证入口：把版本、体检数据、触摸面包屑一次性塞进剪贴板。
            // 用户粘贴回来 = 一份不需要数据线的现场报告。
            Spacer(Modifier.height(6.dp))
            val ctx = LocalContext.current
            Text(
                text = "复制诊断信息",
                style = MaterialTheme.typography.labelLarge,
                color = Primary,
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .clickable {
                        val report = buildString {
                            append("版本 ${CrashLog.appVersion(ctx)}\n")
                            append("悬浮层ok=${diag.overlayOk} 视口=${diag.viewportW}x${diag.viewportH} ")
                            append("帧=${diag.frames} 模拟=${diag.creatures} 引擎存活=${diag.aliveTasks}\n")
                            append("动效关=${diag.motionOff}\n")
                            if (diag.overlayError.isNotEmpty()) append("原因：${diag.overlayError}\n")
                            CrashLog.readNotes(ctx)?.let { append("面包屑：\n$it\n") }
                        }
                        val cm = ctx.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        cm.setPrimaryClip(ClipData.newPlainText("maggot-diag", report))
                        android.widget.Toast.makeText(ctx, "诊断信息已复制", android.widget.Toast.LENGTH_SHORT).show()
                    }
                    .padding(horizontal = 6.dp, vertical = 4.dp),
            )
        }
    }
}

/**
 * 控制台自身的界面偏好。
 *
 * 刻意**不放进 [com.maggot.task.core.MaggotConfig]** —— 那份配置是 Android 与
 * Windows 共享的契约（`docs/lifecycle-spec.md`），不该为"安卓端要用应用内兜底"
 * 这种事去改两端的数据格式。
 */
private const val PREFS_UI = "maggot_ui"
private const val KEY_IN_APP_SWARM = "in_app_swarm"

@Composable
private fun KeyValueRow(key: String, value: String, valueColor: Color) {
    Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(key, style = MaterialTheme.typography.labelLarge, color = FgSubtle)
        Spacer(Modifier.weight(1f))
        Text(value, style = MaterialTheme.typography.labelMedium, color = valueColor)
    }
}

@Composable
private fun StatCell(stage: Stage, count: Int) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(7.dp).clip(CircleShape).background(stageColor(stage)))
            Spacer(Modifier.width(5.dp))
            Text(
                text = count.toString(),
                style = MaterialTheme.typography.headlineMedium,
                color = if (count > 0) Fg else FgSubtle,
            )
        }
        Text(stage.zh, style = MaterialTheme.typography.labelLarge, color = FgSubtle)
    }
}

@Composable
private fun TaskRow(
    task: TaskItem,
    todayKey: String,
    weekKey: String,
    onCare: () -> Unit,
    onComplete: () -> Unit,
    onDelete: () -> Unit,
) {
    val stage = Lifecycle.stageOf(task)
    val rot = task.rot.coerceIn(0f, 1f)
    val overdue = if (task.status != TaskStatus.DONE) task.overdueDays(todayKey) else 0
    val overdueW = if (task.status != TaskStatus.DONE) task.overdueWeeks(weekKey) else 0
    val borderColor = when {
        stage == Stage.FLY -> Danger.copy(alpha = 0.55f)
        overdue > 0 || overdueW > 0 -> Warn.copy(alpha = 0.55f)
        else -> BorderCol
    }

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, borderColor, RoundedCornerShape(18.dp)),
        color = if (stage == Stage.FLY) SurfaceHi else SurfaceBg,
    ) {
        Row(Modifier.height(IntrinsicSize.Min)) {
            // 阶段色竖条：扫一眼就知道谁最危险
            Box(
                Modifier
                    .width(3.dp)
                    .fillMaxHeight()
                    .background(stageColor(stage))
            )
            Column(
                Modifier
                    .padding(12.dp)
                    .weight(1f)
            ) {
                Text(
                    text = task.title,
                    style = MaterialTheme.typography.bodyLarge,
                    color = Fg,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Spacer(Modifier.height(6.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    StageChip(stage)
                    if (task.isDaily) {
                        Spacer(Modifier.width(6.dp))
                        TinyChip("每日", SecondaryCol)
                    }
                    if (task.isWeekly) {
                        Spacer(Modifier.width(6.dp))
                        TinyChip("每周", SecondaryCol)
                    }
                    if (overdue > 0) {
                        Spacer(Modifier.width(6.dp))
                        TinyChip("已逾期 $overdue 天", Warn)
                    }
                    if (overdueW > 0) {
                        Spacer(Modifier.width(6.dp))
                        TinyChip("已逾期 $overdueW 周", Warn)
                    }
                    Spacer(Modifier.width(8.dp))
                    Text(
                        text = "${(rot * 100).toInt()}%",
                        style = MaterialTheme.typography.labelMedium,
                        color = rotColor(task.rot),
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                        text = statusText(task),
                        style = MaterialTheme.typography.labelMedium,
                        color = if (stage == Stage.FLY) Danger else FgSubtle,
                    )
                }
                Spacer(Modifier.height(7.dp))
                Box(
                    Modifier
                        .fillMaxWidth()
                        .height(4.dp)
                        .clip(RoundedCornerShape(2.dp))
                        .background(BorderCol)
                ) {
                    Box(
                        Modifier
                            .fillMaxWidth(rot.coerceAtLeast(0.02f))
                            .height(4.dp)
                            .clip(RoundedCornerShape(2.dp))
                            .background(rotColor(task.rot))
                    )
                }
                Spacer(Modifier.height(10.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    BigButton("我去做了", SurfaceHi, Primary, Modifier.weight(1f), onCare)
                    BigButton("拍死它", Success.copy(alpha = 0.16f), Success, Modifier.weight(1f), onComplete)
                    BigButton("删除", SurfaceBg, Danger, Modifier.width(60.dp), onDelete)
                }
            }
        }
    }
}

@Composable
private fun StageChip(stage: Stage) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(999.dp))
            .background(stageColor(stage).copy(alpha = 0.16f))
            .padding(horizontal = 8.dp, vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(6.dp).clip(CircleShape).background(stageColor(stage)))
        Spacer(Modifier.width(5.dp))
        Text(stage.zh, style = MaterialTheme.typography.labelLarge, color = stageColor(stage))
    }
}

/** 小号信息胶囊，用于「每日」「已逾期 N 天」这类标记。 */
@Composable
private fun TinyChip(text: String, color: Color) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelLarge,
        color = color,
        modifier = Modifier
            .clip(RoundedCornerShape(999.dp))
            .background(color.copy(alpha = 0.16f))
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

/**
 * 每日任务区（规格 §12）。
 *
 * 模板不是任务本身，而是「每天生成一个新实例」的配方。
 * 漏掉的实例**不会自动消失**，所以这里必须把逾期明确标出来 ——
 * 否则用户会以为每天不做事也没代价。
 */
@Composable
private fun DailySection(
    ui: UiSnapshot,
    onAddDaily: (String) -> Unit,
    onToggleDaily: (String, Boolean) -> Unit,
    onRemoveDaily: (String) -> Unit,
) {
    var draft by remember { mutableStateOf("") }
    val todayInstances = ui.tasks.filter {
        it.dailyTemplateId != null && it.dailyKey == ui.todayKey
    }
    val doneCount = todayInstances.count { it.status == TaskStatus.DONE }

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, BorderCol, RoundedCornerShape(18.dp)),
        color = SurfaceBg,
    ) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("每日任务", style = MaterialTheme.typography.titleMedium, color = Fg)
                Spacer(Modifier.weight(1f))
                if (ui.dailies.isNotEmpty()) {
                    val allDone = todayInstances.isNotEmpty() && doneCount == todayInstances.size
                    Text(
                        text = "今天 $doneCount/${todayInstances.size}",
                        style = MaterialTheme.typography.labelMedium,
                        color = if (allDone) Success else FgMuted,
                    )
                }
            }
            Spacer(Modifier.height(2.dp))
            Text(
                text = "每天自动生成一只带当天日期的虫子。没做完的不会消失，它会继续烂。",
                style = MaterialTheme.typography.bodyMedium,
                color = FgSubtle,
            )

            if (ui.dailies.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                ui.dailies.forEach { tpl ->
                    DailyTemplateRow(
                        title = tpl.title,
                        enabled = tpl.enabled,
                        todayTask = todayInstances.firstOrNull { it.dailyTemplateId == tpl.id },
                        overdue = ui.tasks
                            .filter { it.dailyTemplateId == tpl.id && it.status != TaskStatus.DONE }
                            .maxOfOrNull { it.overdueDays(ui.todayKey) } ?: 0,
                        onToggle = { on -> onToggleDaily(tpl.id, on) },
                        onRemove = { onRemoveDaily(tpl.id) },
                    )
                }
            }

            Spacer(Modifier.height(8.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = draft,
                    onValueChange = { draft = it.take(60) },
                    modifier = Modifier.weight(1f),
                    placeholder = { Text("加一个每日任务，如「锻炼」", color = FgSubtle) },
                    singleLine = true,
                    shape = RoundedCornerShape(12.dp),
                    colors = TextFieldDefaults.colors(
                        focusedContainerColor = SurfaceHi,
                        unfocusedContainerColor = SurfaceHi,
                        focusedTextColor = Fg,
                        unfocusedTextColor = Fg,
                        cursorColor = Primary,
                        focusedIndicatorColor = Primary,
                        unfocusedIndicatorColor = BorderCol,
                        focusedPlaceholderColor = FgSubtle,
                        unfocusedPlaceholderColor = FgSubtle,
                    ),
                )
                Spacer(Modifier.width(8.dp))
                BigButton(
                    label = "添加",
                    container = if (draft.isBlank()) SurfaceHi else Primary,
                    content = if (draft.isBlank()) FgSubtle else OnPrimary,
                    modifier = Modifier.width(96.dp),
                ) {
                    if (draft.isNotBlank()) {
                        onAddDaily(draft)
                        draft = ""
                    }
                }
            }
        }
    }
}

@Composable
private fun DailyTemplateRow(
    title: String,
    enabled: Boolean,
    todayTask: TaskItem?,
    overdue: Int,
    onToggle: (Boolean) -> Unit,
    onRemove: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Switch(
            checked = enabled,
            onCheckedChange = onToggle,
            colors = SwitchDefaults.colors(
                checkedThumbColor = OnPrimary,
                checkedTrackColor = Primary,
                uncheckedThumbColor = FgMuted,
                uncheckedTrackColor = SurfaceHi,
                uncheckedBorderColor = BorderHiCol,
            ),
        )
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.bodyLarge,
                color = if (enabled) Fg else FgSubtle,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val pending = todayTask != null && todayTask.status != TaskStatus.DONE
            val sub = when {
                !enabled -> "已停用"
                todayTask == null -> "今天还没生成"
                todayTask.status == TaskStatus.DONE -> "今天已完成"
                else -> "今天 · ${Lifecycle.stageOf(todayTask).zh}"
            }
            Text(
                text = sub,
                style = MaterialTheme.typography.labelMedium,
                color = if (pending) Warn else FgSubtle,
            )
        }
        if (overdue > 0) {
            TinyChip("逾期 $overdue 天", Warn)
            Spacer(Modifier.width(6.dp))
        }
        // 明确写「删模板」：删模板不等于删任务，已有实例会留下来继续烂。
        Box(
            modifier = Modifier
                .heightIn(min = 48.dp)
                .clickable(onClick = onRemove)
                .padding(horizontal = 8.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text("删模板", style = MaterialTheme.typography.labelLarge, color = Danger)
        }
    }
}

/**
 * 每周任务区（规格 §15）。与每日任务同构，只多一个「星期几」。
 *
 * 实例在指定星期**或之后**生成 —— 周一没开电脑、周三才打开也照样补上，
 * 不会因为迟到就跳过这一周。
 */
@Composable
private fun WeeklySection(
    ui: UiSnapshot,
    onAddWeekly: (String, Int) -> Unit,
    onToggleWeekly: (String, Boolean) -> Unit,
    onWeekdayWeekly: (String, Int) -> Unit,
    onRemoveWeekly: (String) -> Unit,
) {
    var draft by remember { mutableStateOf("") }
    var weekday by remember { mutableStateOf(1) }
    val weekInstances = ui.tasks.filter {
        it.weeklyTemplateId != null && it.weeklyKey == ui.weekKey
    }
    val doneCount = weekInstances.count { it.status == TaskStatus.DONE }

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .border(1.dp, BorderCol, RoundedCornerShape(18.dp)),
        color = SurfaceBg,
    ) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("每周任务", style = MaterialTheme.typography.titleMedium, color = Fg)
                Spacer(Modifier.weight(1f))
                if (ui.weeklies.isNotEmpty()) {
                    val allDone = weekInstances.isNotEmpty() && doneCount == weekInstances.size
                    Text(
                        text = "本周 $doneCount/${weekInstances.size}",
                        style = MaterialTheme.typography.labelMedium,
                        color = if (allDone) Success else FgMuted,
                    )
                }
            }
            Spacer(Modifier.height(2.dp))
            Text(
                text = "每周固定生成一只带周号的虫子，如「写周报 · W37」。漏掉的那一周不会消失。",
                style = MaterialTheme.typography.bodyMedium,
                color = FgSubtle,
            )

            if (ui.weeklies.isNotEmpty()) {
                Spacer(Modifier.height(8.dp))
                ui.weeklies.forEach { tpl ->
                    WeeklyTemplateRow(
                        title = tpl.title,
                        enabled = tpl.enabled,
                        weekday = tpl.weekday,
                        thisWeekTask = weekInstances.firstOrNull { it.weeklyTemplateId == tpl.id },
                        overdue = ui.tasks
                            .filter { it.weeklyTemplateId == tpl.id && it.status != TaskStatus.DONE }
                            .maxOfOrNull { it.overdueWeeks(ui.weekKey) } ?: 0,
                        onToggle = { on -> onToggleWeekly(tpl.id, on) },
                        onCycleWeekday = { onWeekdayWeekly(tpl.id, if (tpl.weekday >= 7) 1 else tpl.weekday + 1) },
                        onRemove = { onRemoveWeekly(tpl.id) },
                    )
                }
            }

            Spacer(Modifier.height(8.dp))
            OutlinedTextField(
                value = draft,
                onValueChange = { draft = it.take(60) },
                modifier = Modifier.fillMaxWidth(),
                placeholder = { Text("加一个每周任务，如「写周报」", color = FgSubtle) },
                singleLine = true,
                shape = RoundedCornerShape(12.dp),
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = SurfaceHi,
                    unfocusedContainerColor = SurfaceHi,
                    focusedTextColor = Fg,
                    unfocusedTextColor = Fg,
                    cursorColor = Primary,
                    focusedIndicatorColor = Primary,
                    unfocusedIndicatorColor = BorderCol,
                    focusedPlaceholderColor = FgSubtle,
                    unfocusedPlaceholderColor = FgSubtle,
                ),
            )
            Spacer(Modifier.height(8.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                for (d in 1..7) {
                    WeekdayChip(
                        label = WeekClock.weekdayLabel(d).removePrefix("周"),
                        selected = d == weekday,
                        onClick = { weekday = d },
                        modifier = Modifier.weight(1f),
                    )
                    if (d < 7) Spacer(Modifier.width(4.dp))
                }
                Spacer(Modifier.width(8.dp))
                BigButton(
                    label = "添加",
                    container = if (draft.isBlank()) SurfaceHi else Primary,
                    content = if (draft.isBlank()) FgSubtle else OnPrimary,
                    modifier = Modifier.width(96.dp),
                ) {
                    if (draft.isNotBlank()) {
                        onAddWeekly(draft, weekday)
                        draft = ""
                    }
                }
            }
            Text(
                text = "生成日：${WeekClock.weekdayLabel(weekday)}（当天或之后打开都会补上）",
                style = MaterialTheme.typography.labelMedium,
                color = FgSubtle,
            )
        }
    }
}

@Composable
private fun WeekdayChip(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            // 44dp 高：手指能稳定命中的下限（34dp 在高密度屏上几乎点不准）。
            .heightIn(min = 44.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(if (selected) Primary else SurfaceHi)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.labelLarge,
            color = if (selected) OnPrimary else FgMuted,
        )
    }
}

@Composable
private fun WeeklyTemplateRow(
    title: String,
    enabled: Boolean,
    weekday: Int,
    thisWeekTask: TaskItem?,
    overdue: Int,
    onToggle: (Boolean) -> Unit,
    onCycleWeekday: () -> Unit,
    onRemove: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Switch(
            checked = enabled,
            onCheckedChange = onToggle,
            colors = SwitchDefaults.colors(
                checkedThumbColor = OnPrimary,
                checkedTrackColor = Primary,
                uncheckedThumbColor = FgMuted,
                uncheckedTrackColor = SurfaceHi,
                uncheckedBorderColor = BorderHiCol,
            ),
        )
        Spacer(Modifier.width(8.dp))
        // 点星期胶囊可以循环切换周几，省一整个下拉框。
        Box(
            modifier = Modifier
                .heightIn(min = 34.dp)
                .clip(RoundedCornerShape(9.dp))
                .background(SurfaceHi)
                .clickable(onClick = onCycleWeekday)
                .padding(horizontal = 8.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = WeekClock.weekdayLabel(weekday),
                style = MaterialTheme.typography.labelLarge,
                color = Primary,
            )
        }
        Spacer(Modifier.width(8.dp))
        Column(Modifier.weight(1f)) {
            Text(
                text = title,
                style = MaterialTheme.typography.bodyLarge,
                color = if (enabled) Fg else FgSubtle,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val pending = thisWeekTask != null && thisWeekTask.status != TaskStatus.DONE
            val sub = when {
                !enabled -> "已停用"
                thisWeekTask == null -> "本周还没生成"
                thisWeekTask.status == TaskStatus.DONE -> "本周已完成"
                else -> "本周 · ${Lifecycle.stageOf(thisWeekTask).zh}"
            }
            Text(
                text = sub,
                style = MaterialTheme.typography.labelMedium,
                color = if (pending) Warn else FgSubtle,
            )
        }
        if (overdue > 0) {
            TinyChip("逾期 $overdue 周", Warn)
            Spacer(Modifier.width(6.dp))
        }
        Box(
            modifier = Modifier
                .heightIn(min = 48.dp)
                .clickable(onClick = onRemove)
                .padding(horizontal = 8.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text("删模板", style = MaterialTheme.typography.labelLarge, color = Danger)
        }
    }
}

@Composable
private fun SquashedRow(task: TaskItem, onDelete: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(SurfaceBg.copy(alpha = 0.55f))
            .padding(horizontal = 12.dp, vertical = 9.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(6.dp).clip(CircleShape).background(Success))
        Spacer(Modifier.width(9.dp))
        Text(
            text = task.title,
            style = MaterialTheme.typography.bodyMedium,
            color = FgSubtle,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Box(
            modifier = Modifier
                .heightIn(min = 48.dp)
                .clickable(onClick = onDelete)
                .padding(horizontal = 10.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text("删除", style = MaterialTheme.typography.labelLarge, color = FgSubtle)
        }
    }
}

@Composable
private fun BigButton(
    label: String,
    container: Color,
    content: Color,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    Box(
        modifier = modifier
            // 48dp 是 Android 的触控目标下限，低于它手指就会点不准。
            .height(48.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(container)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.Medium),
            color = content,
        )
    }
}

// ────────────────────────────── 工具 ──────────────────────────────

/** 人类可读的"你多久没管它了"，比绝对时间戳有用得多。 */
private fun statusText(task: TaskItem): String {
    if (task.rot >= Lifecycle.FLY_T) return "已失控"
    val neglectedMin = (System.currentTimeMillis() - task.lastCaredAt) / 60000L
    return when {
        neglectedMin < 1 -> "刚刚管过"
        neglectedMin < 60 -> "已放置 $neglectedMin 分钟"
        else -> "已放置 ${neglectedMin / 60} 小时"
    }
}

private fun canDrawOverlays(context: Context): Boolean =
    Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(context)
