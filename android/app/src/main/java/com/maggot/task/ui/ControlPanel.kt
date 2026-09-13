package com.maggot.task.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.maggot.task.UiSnapshot
import com.maggot.task.core.Stage
import com.maggot.task.core.TaskStatus
import com.maggot.task.ui.theme.BorderCol
import com.maggot.task.ui.theme.BorderHiCol
import com.maggot.task.ui.theme.Danger
import com.maggot.task.ui.theme.Fg
import com.maggot.task.ui.theme.FgMuted
import com.maggot.task.ui.theme.FgSubtle
import com.maggot.task.ui.theme.OnPrimary
import com.maggot.task.ui.theme.Primary
import com.maggot.task.ui.theme.Success
import com.maggot.task.ui.theme.SurfaceBg
import com.maggot.task.ui.theme.SurfaceHi
import com.maggot.task.ui.theme.Warn
import com.maggot.task.ui.theme.rotColor
import com.maggot.task.ui.theme.stageColor
import kotlin.math.ln
import kotlin.math.pow

/**
 * 悬浮控制面板。设计系统 §7。
 *
 * 刻意不用图标字体：全部是「文字按钮 + 手绘阶段圆点」。
 * 一是避免 emoji 当图标带来的跨设备不一致，二是不为一个图标引整包图标库。
 */
@Composable
fun MaggotControlPanel(
    ui: UiSnapshot,
    countdown: String,
    bypassSecondsLeft: Long,
    onDrag: (Float, Float) -> Unit,
    onToggleRun: () -> Unit,
    onRotSpeed: (Float) -> Unit,
    onReset: () -> Unit,
    onClear: () -> Unit,
    onOpenConsole: () -> Unit,
    onHide: () -> Unit,
    onToggleClickThrough: (Boolean) -> Unit,
    onToggleNotify: (Boolean) -> Unit,
    onToggleSound: (Boolean) -> Unit,
) {
    val cfg = ui.config
    var confirmingClear by remember { mutableStateOf(false) }
    // 每秒跳一次的时钟：它动 = 重组活着，它停 = 面板的重组管线死了。
    var clockTick by remember { mutableStateOf(System.currentTimeMillis()) }
    androidx.compose.runtime.LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(1000)
            clockTick = System.currentTimeMillis()
        }
    }

    // ⚠️ 宽度跟随窗口（窗口在服务侧按"min(280dp, 60% 屏宽)"显式给定），
    // 不再写死 300dp —— 在"显示尺寸=大"的机器上 300dp 能占掉大半块屏幕。
    // 高度封顶为屏幕的 68%，超出部分靠下面的 verticalScroll 在面板内滚动，
    // 绝不让悬浮窗自己长到整块桌面。
    val screenHeightDp = androidx.compose.ui.platform.LocalConfiguration.current.screenHeightDp
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(max = (screenHeightDp * 0.68f).dp)
            .clip(RoundedCornerShape(24.dp))
            .border(1.dp, BorderHiCol, RoundedCornerShape(24.dp)),
        color = SurfaceBg,
        shadowElevation = 18.dp,
    ) {
        Column(
            modifier = Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 12.dp),
        ) {

            // ── 拖动区 ──
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .pointerInput(Unit) {
                        detectDragGestures { change, drag ->
                            change.consume()
                            onDrag(drag.x, drag.y)
                        }
                    },
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                // 拖把手：加高 + 整行可拖（原来只有 4dp 高，在高密度屏上几乎捏不住）。
                Box(
                    modifier = Modifier
                        .padding(top = 6.dp, bottom = 8.dp)
                        .size(width = 64.dp, height = 10.dp)
                        .clip(RoundedCornerShape(5.dp))
                        .background(BorderHiCol)
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(
                        modifier = Modifier
                            .size(8.dp)
                            .clip(CircleShape)
                            .background(if (cfg.running) Success else FgSubtle)
                    )
                    Spacer(Modifier.width(8.dp))
                    Text("蛆虫任务", style = MaterialTheme.typography.titleMedium, color = Fg)
                    Spacer(Modifier.width(8.dp))
                    // 实时时钟：它不走 = 重组停了（截一张图就能远程判断面板死活）。
                    Text(
                        text = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US)
                            .format(java.util.Date(clockTick)),
                        style = MaterialTheme.typography.labelMedium,
                        color = FgSubtle,
                    )
                    Spacer(Modifier.weight(1f))
                    Text(
                        text = if (cfg.running) "运行中" else "已暂停",
                        style = MaterialTheme.typography.labelLarge,
                        color = if (cfg.running) Success else FgSubtle,
                    )
                    Spacer(Modifier.width(10.dp))
                    Text(
                        text = "收起",
                        style = MaterialTheme.typography.labelLarge,
                        color = FgMuted,
                        modifier = Modifier
                            .clip(RoundedCornerShape(8.dp))
                            .clickable { onHide() }
                            .padding(horizontal = 10.dp, vertical = 4.dp),
                    )
                }
            }

            Spacer(Modifier.height(12.dp))
            HRule()

            // ── 四阶段计数 ──
            Spacer(Modifier.height(12.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                StageCounter(Stage.EGG, ui.stats.egg)
                StageCounter(Stage.LARVA, ui.stats.larva)
                StageCounter(Stage.PUPA, ui.stats.pupa)
                StageCounter(Stage.FLY, ui.stats.fly)
            }

            Spacer(Modifier.height(14.dp))
            PressureBar(ui)

            Spacer(Modifier.height(6.dp))
            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("下一只变苍蝇", style = MaterialTheme.typography.labelLarge, color = FgSubtle)
                Spacer(Modifier.weight(1f))
                Text(
                    text = countdown,
                    style = MaterialTheme.typography.labelMedium,
                    color = if (ui.stats.fly > 0) Danger else FgMuted,
                )
            }

            // 每日任务今日进度。有模板时才出现，免得空占一行。
            if (ui.dailies.isNotEmpty()) {
                val todayInst = ui.tasks.filter {
                    it.dailyTemplateId != null && it.dailyKey == ui.todayKey
                }
                val done = todayInst.count { it.status == TaskStatus.DONE }
                Spacer(Modifier.height(4.dp))
                Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("今日每日任务", style = MaterialTheme.typography.labelLarge, color = FgSubtle)
                    Spacer(Modifier.weight(1f))
                    Text(
                        text = "$done/${todayInst.size}",
                        style = MaterialTheme.typography.labelMedium,
                        color = if (todayInst.isNotEmpty() && done == todayInst.size) Success else FgMuted,
                    )
                }
            }

            Spacer(Modifier.height(14.dp))
            HRule()
            Spacer(Modifier.height(14.dp))

            // ── 主行动 ──
            PanelButton(
                label = if (cfg.running) "暂停虫群" else "启动虫群",
                container = if (cfg.running) SurfaceHi else Primary,
                content = if (cfg.running) Fg else OnPrimary,
                modifier = Modifier.fillMaxWidth(),
                height = 52.dp,
                onClick = onToggleRun,
            )

            Spacer(Modifier.height(14.dp))

            // ── 腐化倍速（对数刻度：1.0 落在中段，两端都好调）──
            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("腐化倍速", style = MaterialTheme.typography.labelLarge, color = FgSubtle)
                Spacer(Modifier.weight(1f))
                Text(
                    text = "×" + formatSpeed(cfg.rotSpeed),
                    style = MaterialTheme.typography.labelMedium,
                    color = Primary,
                )
            }
            Slider(
                value = speedToSlider(cfg.rotSpeed),
                onValueChange = { onRotSpeed(sliderToSpeed(it)) },
                valueRange = 0f..1f,
                colors = SliderDefaults.colors(
                    thumbColor = Primary,
                    activeTrackColor = Primary,
                    inactiveTrackColor = BorderCol,
                ),
            )
            Text(
                text = "基准 ${cfg.baseRotMinutes} 分钟烂透",
                style = MaterialTheme.typography.labelMedium,
                color = FgSubtle,
            )

            Spacer(Modifier.height(12.dp))

            // ── 次级行动 ──
            if (confirmingClear) {
                val total = ui.stats.alive + ui.stats.squashed
                Text(
                    text = "将永久删除 $total 个任务，无法撤销。",
                    style = MaterialTheme.typography.bodyMedium,
                    color = Danger,
                )
                Spacer(Modifier.height(8.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    PanelButton("取消", SurfaceHi, Fg, Modifier.weight(1f)) { confirmingClear = false }
                    PanelButton("确认清空", Danger, Color.White, Modifier.weight(1f)) {
                        confirmingClear = false
                        onClear()
                    }
                }
            } else {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    PanelButton("新建", SurfaceHi, Fg, Modifier.weight(1f), onClick = onOpenConsole)
                    PanelButton("重置", SurfaceHi, Fg, Modifier.weight(1f), onClick = onReset)
                    PanelButton("清空", SurfaceHi, Danger, Modifier.weight(1f)) { confirmingClear = true }
                }
            }

            Spacer(Modifier.height(14.dp))
            HRule()
            Spacer(Modifier.height(4.dp))

            // ── 开关 ──
            ToggleRow(
                label = "穿透点击",
                hint = if (!cfg.clickThrough && bypassSecondsLeft > 0)
                    "${bypassSecondsLeft}s 后自动恢复穿透" else null,
                checked = cfg.clickThrough,
                onCheckedChange = onToggleClickThrough,
            )
            ToggleRow(
                label = "烂尾通知",
                checked = cfg.notifyOnFly,
                onCheckedChange = onToggleNotify,
            )
            ToggleRow(
                label = "拍死音效",
                checked = cfg.soundEnabled,
                onCheckedChange = onToggleSound,
            )

            Spacer(Modifier.height(4.dp))
            PanelButton("打开任务控制台", SurfaceHi, Fg, Modifier.fillMaxWidth()) { onOpenConsole() }
        }
    }
}

// ────────────────────────────── 子组件 ──────────────────────────────

@Composable
private fun HRule() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(1.dp)
            .background(BorderCol)
    )
}

@Composable
private fun StageCounter(stage: Stage, count: Int) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier
                    .size(7.dp)
                    .clip(CircleShape)
                    .background(stageColor(stage))
            )
            Spacer(Modifier.width(5.dp))
            Text(
                text = count.toString(),
                style = MaterialTheme.typography.titleMedium,
                color = if (count > 0) Fg else FgSubtle,
            )
        }
        Spacer(Modifier.height(2.dp))
        Text(stage.zh, style = MaterialTheme.typography.labelLarge, color = FgSubtle)
    }
}

/** 全部任务按阶段占比 + 整体腐烂度。这里颜色本身就在报警。 */
@Composable
private fun PressureBar(ui: UiSnapshot) {
    val s = ui.stats
    val alive = ui.tasks.filter { it.status != TaskStatus.DONE }
    val avg = if (alive.isEmpty()) 0f else alive.sumOf { it.rot.toDouble() }.toFloat() / alive.size

    Column {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(BorderCol),
        ) {
            val segments = listOf(
                Stage.EGG to s.egg,
                Stage.LARVA to s.larva,
                Stage.PUPA to s.pupa,
                Stage.FLY to s.fly,
            )
            if (s.alive == 0) {
                Box(Modifier.weight(1f).height(6.dp))
            } else {
                segments.forEach { (stage, n) ->
                    if (n > 0) {
                        Box(
                            modifier = Modifier
                                .weight(n.toFloat())
                                .height(6.dp)
                                .background(stageColor(stage))
                        )
                    }
                }
            }
        }
        Spacer(Modifier.height(5.dp))
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("整体腐烂度", style = MaterialTheme.typography.labelLarge, color = FgSubtle)
            Spacer(Modifier.weight(1f))
            Text(
                text = "${(avg.coerceIn(0f, 1f) * 100).toInt()}%",
                style = MaterialTheme.typography.labelMedium,
                color = rotColor(avg),
            )
        }
    }
}

@Composable
private fun PanelButton(
    label: String,
    container: Color,
    content: Color,
    modifier: Modifier = Modifier,
    // Android 触控目标下限是 48dp（pro-rules：iOS 44pt / Android 48dp）。
    height: Dp = 48.dp,
    onClick: () -> Unit,
) {
    Box(
        modifier = modifier
            .height(height)
            .clip(RoundedCornerShape(14.dp))
            .background(container)
            .clickableNoRipple(onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.Medium),
            color = content,
        )
    }
}

@Composable
private fun ToggleRow(
    label: String,
    checked: Boolean,
    hint: String? = null,
    onCheckedChange: (Boolean) -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(48.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(label, style = MaterialTheme.typography.bodyLarge, color = Fg)
            if (hint != null) {
                Text(hint, style = MaterialTheme.typography.labelMedium, color = Warn)
            }
        }
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = OnPrimary,
                checkedTrackColor = Primary,
                uncheckedThumbColor = FgMuted,
                uncheckedTrackColor = SurfaceHi,
                uncheckedBorderColor = BorderHiCol,
            ),
        )
    }
}

/** 去掉涟漪：悬浮窗里涟漪会被窗口边界裁切，看起来像渲染 bug。改用内容色反馈。 */
@Composable
private fun Modifier.clickableNoRipple(onClick: () -> Unit): Modifier {
    val source = remember { MutableInteractionSource() }
    return this.clickable(
        interactionSource = source,
        indication = null,
        onClick = onClick,
    )
}

// ────────────────────────────── 倍速对数映射 ──────────────────────────────

private const val SPEED_MIN = 0.25f
private const val SPEED_MAX = 8f

private fun speedToSlider(v: Float): Float {
    val c = v.coerceIn(SPEED_MIN, SPEED_MAX)
    return (ln(c / SPEED_MIN) / ln(SPEED_MAX / SPEED_MIN)).coerceIn(0f, 1f)
}

private fun sliderToSpeed(t: Float): Float {
    val c = t.coerceIn(0f, 1f)
    val raw = SPEED_MIN * (SPEED_MAX / SPEED_MIN).pow(c)
    // 吸附到 1.0，方便一键回到标准速度
    return if (raw > 0.93f && raw < 1.08f) 1f else (raw * 20f).toInt() / 20f
}

private fun formatSpeed(v: Float): String {
    val r = (v * 10f).toInt() / 10f
    return if (r == r.toInt().toFloat()) "${r.toInt()}.0" else "$r"
}
