package com.maggot.task.diag

import android.app.Application
import android.content.Context
import android.os.Build
import android.os.Process
import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 崩溃记录器 —— 让"点进去就闪退"这种问题不再无迹可寻。
 *
 * 存在的理由很直接：悬浮层 / 启动崩溃这一类问题，在没有 USB 调试、
 * 拿不到 `adb logcat` 的机器上完全无法定位。用户能提供的只有一句
 * "点进去就闪退，所以是黑的"，而这句话里没有一个字节是可用的技术信息。
 *
 * 这里做的是：把**未捕获异常的完整堆栈**同步落盘，下次启动时直接显示在屏幕上
 * （可长按复制）。于是一次截图就等价于一次 logcat。
 *
 * 三条硬约束，任何一条破了这个类就比它要解决的问题更糟：
 *  1. **绝不抛异常** —— 崩溃处理器自己再崩，用户会看到"应用反复停止"。
 *  2. **必须同步写** —— 进程马上就要死了，不能等协程。
 *  3. **不依赖任何应用代码** —— 不用 Compose、不用主题、不用日志框架，
 *     因为它们都可能正是崩溃的原因。
 */
object CrashLog {

    private const val FILE_FATAL = "latest_crash.txt"
    private const val PREFS = "maggot_diag"
    private const val KEY_BOOT_ERROR = "boot_error"
    private const val KEY_STATE = "boot_state"
    private const val KEY_STATE_AT = "boot_state_at"
    private const val STATE_STARTING = "starting"
    private const val STATE_OK = "ok"

    /** 落盘内容的长度上限，避免一个递归爆栈把文件写成几十 MB。 */
    private const val MAX_CHARS = 60_000

    @Volatile
    private var installed = false

    /**
     * 安装全局崩溃处理器。
     *
     * 必须在 `Application.onCreate` 的**最前面**调用 —— 它之后的任何一行
     * （包括读数据库、建通知通道）都有可能在真机上炸。
     */
    fun install(app: Application) {
        if (installed) return
        installed = true
        // 保留系统原有的处理器：崩溃对话框、系统的崩溃上报都还要照常工作。
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, t ->
            try {
                writeFatal(app, thread, t)
            } catch (_: Throwable) {
                // 记录失败也不能改变崩溃本身的走向。
            }
            if (previous != null) previous.uncaughtException(thread, t)
            else Process.killProcess(Process.myPid())
        }
    }

    // ────────────────────────── 致命崩溃 ──────────────────────────

    private fun writeFatal(context: Context, thread: Thread, t: Throwable) {
        val text = buildString {
            append("═══ 崩溃报告 ═══\n")
            append("时间　").append(now()).append('\n')
            append("线程　").append(thread.name).append('\n')
            append("设备　").append(Build.MANUFACTURER).append(' ').append(Build.MODEL)
            append(" · Android ").append(Build.VERSION.RELEASE)
            append(" (API ").append(Build.VERSION.SDK_INT).append(")\n")
            append("应用　").append(appVersion(context)).append('\n')
            append('\n')
            append(stackOf(t))
        }
        writeTo(context, FILE_FATAL, text)
    }

    /** 上次崩溃的完整文本；没有则返回 null。 */
    fun readFatal(context: Context): String? = try {
        val f = File(context.filesDir, FILE_FATAL)
        if (f.exists()) f.readText().takeIf { it.isNotBlank() } else null
    } catch (_: Throwable) {
        null
    }

    /** 崩溃报告落盘的**绝对路径**，方便用户用文件管理器/数据线取走。 */
    fun fatalPath(context: Context): String = File(context.filesDir, FILE_FATAL).absolutePath

    fun clearFatal(context: Context) {
        try {
            File(context.filesDir, FILE_FATAL).delete()
        } catch (_: Throwable) {
            // 忽略
        }
    }

    // ────────────────────────── 启动标记 ──────────────────────────

    /**
     * 标记"这次启动开始了"。
     *
     * 存在的理由：有一整类"闪退"**不会产生任何 Java 堆栈** ——
     * 被系统低内存回收、被 ROM 的后台策略强停、原生 SIGSEGV。
     * 没有这个标记，我们只能看到"又一次什么都没留下"，然后继续瞎猜；
     * 有了它就能把两类死因分开，因为它们的下一步完全不同：
     *
     *  · 有 Java 堆栈  → 是我们的 bug，堆栈里直接有类名和行号。
     *  · 没有堆栈、启动也没走完 → 不是我们抛的，要去系统/ROM 那边找记录。
     */
    fun markBootStart(context: Context) = putState(context, STATE_STARTING)

    /** 界面/服务真的起来了。与 [markBootStart] 配对。 */
    fun markBootOk(context: Context) = putState(context, STATE_OK)

    /**
     * 上次启动是否"没走完就被中断了"，且**没有**留下 Java 异常。
     * 这正是最难查的那一类（logcat 里往往也什么都没有）。
     *
     * ⚠️ 语义是**一次性读出（consume）**：检测到 starting 后立刻把它复位成 ok，
     * 再返回报告文本。理由有二：
     *  1. 报告只该弹一次 —— 用户看完后进程再死，不该下次启动又弹同一份旧账；
     *  2. 调用方在读完之后才会写本次的 starting，如果这里不复位，
     *     "读"就永远读到同一份旧标记。
     *
     * 因此调用顺序是硬约束：**必须先 read，后 [markBootStart]**。
     * 1.2.4 正是把这个顺序写反了（Application/Activity 都先 mark 再 read），
     * 读到的永远是**本次**刚写入的 starting —— 每次冷启动都误报"上次没走完"，
     * 报告页上的「清除并进入应用」一 recreate 又回到同一页，看起来就是"没反应"。
     */
    fun readUnexpectedDeath(context: Context): String? = try {
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (p.getString(KEY_STATE, STATE_OK) != STATE_STARTING) {
            null
        } else {
            // 先把时间戳读出来（复位会覆盖它），再消费标记，最后生成报告。
            val at = try { p.getLong(KEY_STATE_AT, 0L) } catch (_: Throwable) { 0L }
            p.edit()
                .putString(KEY_STATE, STATE_OK)
                .apply()
            buildString {
                append("上次启动没有走完，而且没有留下 Java 异常堆栈。\n")
                if (at > 0L) {
                    append("上次启动时间：")
                    append(SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date(at)))
                    append('\n')
                }
                append('\n')
                append("这通常说明它不是应用代码抛出来的，而是被系统直接中断的：\n")
                append("  · 被省电 / 后台策略强制停止（国产 ROM 最常见）\n")
                append("  · 内存不足被系统回收\n")
                append("  · 原生层崩溃（SIGSEGV 之类，Java 层看不到）\n")
                append('\n')
                append("可以试着看：设置 → 应用 → 蛆虫任务 → 是否有「运行异常 / 崩溃记录」一项；\n")
                append("并把它的「电池 / 后台管理」设为「无限制 / 允许后台活动」后再打开。\n")
                append('\n')
                append("如果之后不再出现，可以忽略这条。")
            }
        }
    } catch (_: Throwable) {
        null
    }

    private fun putState(context: Context, state: String) {
        try {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString(KEY_STATE, state)
                .putLong(KEY_STATE_AT, System.currentTimeMillis())
                .apply()
        } catch (_: Throwable) {
            // 忽略
        }
    }

    // ────────────────────────── 非致命错误 ──────────────────────────

    /**
     * 记录"被捕获但值得知道"的错误（冷启动失败、悬浮层挂载失败……）。
     *
     * 用 SharedPreferences 而不是文件：控制台每 160ms 发布一次快照，
     * 从这里读文字必须是内存级开销。
     */
    fun recordNonFatal(context: Context, tag: String, t: Throwable) {
        val text = "【$tag】${now()}\n${stackOf(t)}"
        try {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(KEY_BOOT_ERROR, text).apply()
        } catch (_: Throwable) {
            // 忽略
        }
    }

    fun readNonFatal(context: Context): String? = try {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_BOOT_ERROR, null)
            ?.takeIf { it.isNotBlank() }
    } catch (_: Throwable) {
        null
    }

    fun clearNonFatal(context: Context) {
        try {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().remove(KEY_BOOT_ERROR).apply()
        } catch (_: Throwable) {
            // 忽略
        }
    }

    // ────────────────────────── 事件面包屑 ──────────────────────────

    private const val KEY_NOTES = "notes"

    /**
     * 追加一条**诊断面包屑**（触摸事件、窗口动作这类"发生过没有"的事实）。
     * 与崩溃报告一起被看到：用户复制全文时，这些行直接回答"事件死在了哪一层"。
     * 只保留最近 12 条 —— 它是证据，不是日志系统。
     */
    fun recordNote(context: Context, text: String) {
        try {
            val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val prev = p.getString(KEY_NOTES, "") ?: ""
            val stamp = SimpleDateFormat("HH:mm:ss", Locale.US).format(Date())
            val line = "$stamp $text"
            val lines = (prev + "\n" + line).split('\n').filter { it.isNotBlank() }
            p.edit().putString(KEY_NOTES, lines.takeLast(12).joinToString("\n")).apply()
        } catch (_: Throwable) {
            // 忽略
        }
    }

    /** 面包屑全文（可能为 null）；[readFatal] 有内容时调用方通常不展示它。 */
    fun readNotes(context: Context): String? = try {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_NOTES, null)?.takeIf { it.isNotBlank() }
    } catch (_: Throwable) {
        null
    }

    fun clearNotes(context: Context) {
        try {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().remove(KEY_NOTES).apply()
        } catch (_: Throwable) {
            // 忽略
        }
    }

    // ────────────────────────── 内部 ──────────────────────────

    /**
     * 同时写内部存储和外部私有目录。
     *
     * 内部那份是权威副本（一定写得进去）；外部那份是给用户取件用的 ——
     * `/Android/data/<包名>/files/` 在数据线连接时可以拷出来。
     */
    private fun writeTo(context: Context, name: String, text: String) {
        val body = if (text.length > MAX_CHARS) text.take(MAX_CHARS) + "\n…（已截断）" else text
        try {
            File(context.filesDir, name).writeText(body)
        } catch (_: Throwable) {
            // 忽略
        }
        try {
            context.getExternalFilesDir(null)?.let { File(it, name).writeText(body) }
        } catch (_: Throwable) {
            // 外部目录可能不可用（未挂载 / 被 ROM 限制），忽略。
        }
    }

    private fun stackOf(t: Throwable): String {
        val sw = StringWriter()
        PrintWriter(sw).use { t.printStackTrace(it) }
        // 链式 cause 在 printStackTrace 里只会打一层，这里补全因果链。
        var cause = t.cause
        var depth = 0
        while (cause != null && depth < 5) {
            sw.append("\n由以下原因引起 (caused by)：\n")
            PrintWriter(sw).use { cause.printStackTrace(it) }
            cause = cause.cause
            depth++
        }
        return sw.toString()
    }

    private fun now(): String =
        SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())

    /** 应用版本号文本，诊断报告用。 */
    fun appVersion(context: Context): String = try {
        val pi = context.packageManager.getPackageInfo(context.packageName, 0)
        "${pi.versionName} (${pi.longVersionCode})"
    } catch (_: Throwable) {
        "unknown"
    }
}
