package com.maggot.task.notify

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.maggot.task.R
import com.maggot.task.core.LifecycleEngine
import com.maggot.task.core.TaskItem
import com.maggot.task.ui.MainActivity

/**
 * 通知层。两条通道职责完全分开：
 *  - 警报通道（HIGH）：某个任务真的变成苍蝇了 —— 必须吵到你。
 *  - 服务通道（LOW）：虫群正在后台跑的常驻通知 —— 安静、可操作、不可划掉。
 */
class Notifier(private val context: Context) {

    private val manager = NotificationManagerCompat.from(context)

    init {
        createChannels()
    }

    private fun createChannels() {
        val alert = NotificationChannel(
            CH_ALERT,
            context.getString(R.string.channel_alert_name),
            NotificationManager.IMPORTANCE_HIGH,
        ).apply {
            description = context.getString(R.string.channel_alert_desc)
            enableVibration(true)
        }
        val service = NotificationChannel(
            CH_SERVICE,
            context.getString(R.string.channel_service_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = context.getString(R.string.channel_service_desc)
            setShowBadge(false)
        }
        // createNotificationChannel 幂等；重复调用只会更新已有通道。
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(alert)
        nm.createNotificationChannel(service)
    }

    private fun contentIntent(): PendingIntent = PendingIntent.getActivity(
        context,
        0,
        Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
        },
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun base(title: String, text: String, channel: String): NotificationCompat.Builder =
        NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(contentIntent())
            .setAutoCancel(true)

    /** 单个任务彻底烂尾。 */
    fun notifyBecameFly(task: TaskItem, generation: Int) {
        // 注意：必须用 ${} 包裹 —— 中文是合法的 Kotlin 标识符字符，
        // "$lineage它" 会被解析成一个变量名，而不是"变量 + 中文"。
        val lineage = if (generation > 0) "（第 $generation 代蝇卵孵出来的）" else ""
        notify(
            id = ALERT_BASE + task.id.hashCode(),
            n = base(
                title = "「${task.title}」烂尾了",
                text = "${lineage}它已经变成苍蝇，正在你屏幕上乱飞。再不处理就要下崽了。",
                channel = CH_ALERT,
            ).setPriority(NotificationCompat.PRIORITY_HIGH).build(),
        )
    }

    /** 逼近苍蝇线，先吓一吓。 */
    fun notifyApproaching(task: TaskItem) {
        notify(
            id = ALERT_BASE + task.id.hashCode(),
            n = base(
                title = "「${task.title}」快变苍蝇了",
                text = "它已经开始僵硬。现在点「我去做了」还来得及。",
                channel = CH_ALERT,
            ).setPriority(NotificationCompat.PRIORITY_DEFAULT).build(),
        )
    }

    /** 冷启动时的汇总，避免一次炸出几十条。 */
    fun notifyFlySummary(count: Int) {
        notify(
            id = ALERT_SUMMARY,
            n = base(
                title = "你的桌面已经有 $count 只苍蝇",
                text = "离开这段时间它们没闲着。打开「蛆虫任务」看看战况。",
                channel = CH_ALERT,
            ).setPriority(NotificationCompat.PRIORITY_HIGH).build(),
        )
    }

    fun notifyBred(childTitle: String, flyCount: Int) {
        notify(
            id = ALERT_BREED,
            n = base(
                title = "苍蝇交配了",
                text = "「$childTitle」已孵化。当前 $flyCount 只苍蝇，越拖越多。",
                channel = CH_ALERT,
            ).setPriority(NotificationCompat.PRIORITY_DEFAULT).build(),
        )
    }

    private fun notify(id: Int, n: Notification) {
        // 没有通知权限时静默失败，绝不因为通知炸掉主流程。
        runCatching { manager.notify(id, n) }
    }

    /** 常驻的前台服务通知。带两个动作：把收起的面板找回来 / 停掉虫群。 */
    fun buildServiceNotification(engine: LifecycleEngine): Notification {
        val s = engine.stats()
        val paused = !engine.config.running
        val text = if (paused) {
            "已暂停 · 虫群静止中"
        } else {
            "卵 ${s.egg} · 蛆 ${s.larva} · 蛹 ${s.pupa} · 苍蝇 ${s.fly}"
        }
        fun serviceAction(action: String, title: String) =
            NotificationCompat.Action(
                0,
                title,
                PendingIntent.getService(
                    context,
                    action.hashCode(),
                    Intent(context, com.maggot.task.service.SwarmService::class.java)
                        .setAction(action),
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
                ),
            )
        return NotificationCompat.Builder(context, CH_SERVICE)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(context.getString(R.string.app_name))
            .setContentText(text)
            .setContentIntent(contentIntent())
            .addAction(serviceAction(com.maggot.task.service.SwarmService.ACTION_TOGGLE_PAUSE, "暂停/继续"))
            .addAction(serviceAction(com.maggot.task.service.SwarmService.ACTION_SHOW_PANEL, "显示面板"))
            .addAction(serviceAction(com.maggot.task.service.SwarmService.ACTION_STOP, "停止虫群"))
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    companion object {
        const val CH_ALERT = "maggot_alert"
        const val CH_SERVICE = "maggot_service"
        const val SERVICE_NOTIFICATION_ID = 1001

        private const val ALERT_BASE = 2000
        private const val ALERT_SUMMARY = 2001
        private const val ALERT_BREED = 2002
    }
}
