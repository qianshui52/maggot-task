package com.maggot.task.core

import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.time.temporal.WeekFields

/**
 * 每周任务的周键工具（规格 §15.3）。
 *
 * 两个最容易写错的地方，这里都刻意写死：
 *
 * 1. **周键必须用「周基准年」**（week-based-year），不是日历年。
 *    比如 2027-01-01 在 ISO 周历里属于 **2026-W53**。
 * 2. **星期用 ISO 编号 1=周一 … 7=周日**，和 `DayOfWeek.value` 一致。
 *    （.NET 那边 `DayOfWeek` 是 0=周日，C# 实现必须转换 —— 写错会整体错一天。）
 */
object WeekClock {

    /**
     * 周键 `YYYY-Www`，例如 "2026-W53"。
     *
     * ⚠️ 这里**不能**用 `DateTimeFormatter.ofPattern("YYYY-'W'ww", Locale.ROOT)`。
     * 那个 API 取的是**该 Locale 的周规则**，而 `Locale.ROOT` 并不是 ISO ——
     * 它是「周日为一周第一天、最小天数 1」，于是 2027-01-01 会被算成 2027-W01，
     * 凭空多出一周。必须显式用 [WeekFields.ISO]。（这个坑是单元测试抓出来的。）
     */
    fun weekKey(date: LocalDate = LocalDate.now()): String {
        val year = date.get(WeekFields.ISO.weekBasedYear())
        val week = date.get(WeekFields.ISO.weekOfWeekBasedYear())
        return "%d-W%02d".format(year, week)
    }

    /** ISO 星期几：1=周一 … 7=周日。 */
    fun isoWeekday(date: LocalDate = LocalDate.now()): Int = date.dayOfWeek.value

    /** 供 UI 显示：中文星期名。 */
    fun weekdayLabel(iso: Int): String = when (iso) {
        1 -> "周一"; 2 -> "周二"; 3 -> "周三"; 4 -> "周四"
        5 -> "周五"; 6 -> "周六"; 7 -> "周日"
        else -> "周?"
    }

    /** "2026-W37" → "W37"，用于标题后缀。 */
    fun shortLabel(key: String): String = "W" + key.substringAfterLast('W')

    /**
     * 逾期周数。本周或未来返回 0。
     *
     * 先把周键还原成那一周的周一，再按周数相减 —— 直接比较周号会在跨年时出错。
     */
    fun overdueWeeks(weeklyKey: String?, currentKey: String): Int {
        if (weeklyKey == null) return 0
        if (weeklyKey >= currentKey) return 0
        val from = weekStart(weeklyKey) ?: return 0
        val to = weekStart(currentKey) ?: return 0
        return ChronoUnit.WEEKS.between(from, to).toInt().coerceAtLeast(0)
    }

    /**
     * "2026-W37" → 那一周的周一。
     *
     * 用「当年的 1 月 4 日」做锚点：ISO 规定第 1 周必定包含 1 月 4 日，
     * 所以 1/4 所在周的周一就是第 1 周的周一，再按周号平移即可。
     */
    private fun weekStart(key: String): LocalDate? = runCatching {
        val year = key.substringBefore("-W").toInt()
        val week = key.substringAfter("-W").toInt()
        LocalDate.of(year, 1, 4)
            .with(WeekFields.ISO.weekOfWeekBasedYear(), week.toLong())
            .with(WeekFields.ISO.dayOfWeek(), 1L)
    }.getOrNull()
}
