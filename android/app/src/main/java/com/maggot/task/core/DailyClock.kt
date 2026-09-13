package com.maggot.task.core

import java.time.LocalDate
import java.time.format.DateTimeFormatter

/**
 * 每日任务的日期工具。
 *
 * **一律使用设备本地时区** —— 用户在 UTC+8 的晚上 8 点看到的"今天"，
 * 必须是他自己的今天，而不是 UTC 的今天。用 UTC 会让每日任务在傍晚就翻篇。
 *
 * 日期键统一是 ISO 的 `YYYY-MM-DD`（`LocalDate.toString()` 的格式），
 * 这样字符串比较即等价于日期比较，跨端也天然一致。
 */
object DailyClock {

    private val SHORT = DateTimeFormatter.ofPattern("MM-dd")

    /** 今天的日期键，例如 "2026-09-13"。 */
    fun todayKey(): String = LocalDate.now().toString()

    /** "2026-09-13" → "09-13"，用于任务标题后缀。 */
    fun shortLabel(key: String): String = runCatching {
        LocalDate.parse(key).format(SHORT)
    }.getOrDefault(key.takeLast(5))

    /** 某个每日实例逾期了几天。当天或未来返回 0。 */
    fun overdueDays(dailyKey: String?, todayKey: String): Int {
        if (dailyKey == null) return 0
        if (dailyKey >= todayKey) return 0
        return runCatching {
            java.time.temporal.ChronoUnit.DAYS
                .between(LocalDate.parse(dailyKey), LocalDate.parse(todayKey))
                .toInt()
        }.getOrDefault(0)
    }
}
