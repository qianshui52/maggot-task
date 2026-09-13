package com.maggot.task.data

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import com.maggot.task.core.DailyTemplate
import com.maggot.task.core.MaggotConfig
import com.maggot.task.core.TaskItem
import com.maggot.task.core.TaskStatus
import com.maggot.task.core.WeeklyTemplate
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private const val DB_NAME = "maggot.db"

/** v1 = 首发；v2 = 每日任务；v3 = 每周任务（再加两列 + 一张模板表）。 */
private const val DB_VERSION = 3

private const val TABLE = "tasks"
private const val DAILY = "daily_templates"
private const val WEEKLY = "weekly_templates"

private const val PREFS = "maggot_config"
private const val KEY_CONFIG = "config_json"
private const val KEY_LAST_SEEN = "last_seen_at"

private val TASK_COLUMNS = arrayOf(
    "id", "title", "note", "status", "rot", "progress",
    "created_at", "last_cared_at", "completed_at", "generation", "parent_id", "gene",
    "daily_template_id", "daily_key",
    "weekly_template_id", "weekly_key",
)

private val DAILY_COLUMNS = arrayOf(
    "id", "title", "note", "enabled", "created_at", "sort_order",
)

private val WEEKLY_COLUMNS = arrayOf(
    "id", "title", "note", "enabled", "created_at", "sort_order", "weekday",
)

/**
 * 用系统自带的 SQLite，不引 Room —— 少一层注解处理器，冷启动更快，
 * 而且这个 schema 简单到不需要 ORM。
 */
class MaggotDb(context: Context) : SQLiteOpenHelper(context, DB_NAME, null, DB_VERSION) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE $TABLE (
                id                 TEXT PRIMARY KEY NOT NULL,
                title              TEXT NOT NULL,
                note               TEXT NOT NULL DEFAULT '',
                status             INTEGER NOT NULL,
                rot                REAL NOT NULL,
                progress           INTEGER NOT NULL,
                created_at         INTEGER NOT NULL,
                last_cared_at      INTEGER NOT NULL,
                completed_at       INTEGER,
                generation         INTEGER NOT NULL DEFAULT 0,
                parent_id          TEXT,
                gene               REAL NOT NULL DEFAULT 1.0,
                daily_template_id  TEXT,
                daily_key          TEXT,
                weekly_template_id TEXT,
                weekly_key         TEXT
            )
            """.trimIndent()
        )
        db.execSQL("CREATE INDEX idx_tasks_status ON $TABLE(status)")
        db.execSQL("CREATE INDEX idx_tasks_completed ON $TABLE(completed_at)")
        db.execSQL("CREATE INDEX idx_tasks_daily ON $TABLE(daily_template_id, daily_key)")
        db.execSQL("CREATE INDEX idx_tasks_weekly ON $TABLE(weekly_template_id, weekly_key)")
        db.execSQL(CREATE_DAILY)
        db.execSQL(CREATE_WEEKLY)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // 逐版增量升级，老数据一条都不丢。
        if (oldVersion < 2) {
            db.execSQL("ALTER TABLE $TABLE ADD COLUMN daily_template_id TEXT")
            db.execSQL("ALTER TABLE $TABLE ADD COLUMN daily_key TEXT")
            db.execSQL("CREATE INDEX IF NOT EXISTS idx_tasks_daily ON $TABLE(daily_template_id, daily_key)")
            db.execSQL(CREATE_DAILY)
        }
        if (oldVersion < 3) {
            db.execSQL("ALTER TABLE $TABLE ADD COLUMN weekly_template_id TEXT")
            db.execSQL("ALTER TABLE $TABLE ADD COLUMN weekly_key TEXT")
            db.execSQL("CREATE INDEX IF NOT EXISTS idx_tasks_weekly ON $TABLE(weekly_template_id, weekly_key)")
            db.execSQL(CREATE_WEEKLY)
        }
    }

    private companion object {
        val CREATE_DAILY = """
            CREATE TABLE IF NOT EXISTS $DAILY (
                id         TEXT PRIMARY KEY NOT NULL,
                title      TEXT NOT NULL,
                note       TEXT NOT NULL DEFAULT '',
                enabled    INTEGER NOT NULL DEFAULT 1,
                created_at INTEGER NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0
            )
        """.trimIndent()

        val CREATE_WEEKLY = """
            CREATE TABLE IF NOT EXISTS $WEEKLY (
                id         TEXT PRIMARY KEY NOT NULL,
                title      TEXT NOT NULL,
                note       TEXT NOT NULL DEFAULT '',
                enabled    INTEGER NOT NULL DEFAULT 1,
                created_at INTEGER NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                weekday    INTEGER NOT NULL DEFAULT 1
            )
        """.trimIndent()
    }
}

/**
 * 任务、每日模板与配置的读写。所有方法都切到 IO 线程，调用方直接 await 即可。
 */
class TaskStore(context: Context) {

    private val appContext = context.applicationContext
    private val helper = MaggotDb(appContext)
    private val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    // ────────────────────────── 任务 ──────────────────────────

    suspend fun loadTasks(): List<TaskItem> = withContext(Dispatchers.IO) {
        val out = ArrayList<TaskItem>()
        helper.readableDatabase.rawQuery(
            "SELECT ${TASK_COLUMNS.joinToString(",")} FROM $TABLE", null,
        ).use { c ->
            while (c.moveToNext()) {
                out += TaskItem(
                    id = c.getString(0),
                    title = c.getString(1),
                    note = c.getString(2) ?: "",
                    status = statusFromInt(c.getInt(3)),
                    rot = c.getFloat(4),
                    progress = c.getInt(5),
                    createdAt = c.getLong(6),
                    lastCaredAt = c.getLong(7),
                    completedAt = if (c.isNull(8)) null else c.getLong(8),
                    generation = c.getInt(9),
                    parentId = if (c.isNull(10)) null else c.getString(10),
                    gene = c.getFloat(11),
                    dailyTemplateId = if (c.isNull(12)) null else c.getString(12),
                    dailyKey = if (c.isNull(13)) null else c.getString(13),
                    weeklyTemplateId = if (c.isNull(14)) null else c.getString(14),
                    weeklyKey = if (c.isNull(15)) null else c.getString(15),
                )
            }
        }
        out
    }

    /** 全量覆盖写。任务量在几百级别，比逐条 diff 更简单也更不容易出错。 */
    suspend fun saveTasks(tasks: List<TaskItem>) = withContext(Dispatchers.IO) {
        val db = helper.writableDatabase
        db.beginTransaction()
        try {
            db.delete(TABLE, null, null)
            for (t in tasks) {
                db.insertWithOnConflict(TABLE, null, toValues(t), SQLiteDatabase.CONFLICT_REPLACE)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    suspend fun clearTasks() = withContext(Dispatchers.IO) {
        helper.writableDatabase.delete(TABLE, null, null)
    }

    private fun toValues(t: TaskItem) = ContentValues().apply {
        put("id", t.id)
        put("title", t.title)
        put("note", t.note)
        put("status", statusToInt(t.status))
        put("rot", t.rot)
        put("progress", t.progress)
        put("created_at", t.createdAt)
        put("last_cared_at", t.lastCaredAt)
        if (t.completedAt == null) putNull("completed_at") else put("completed_at", t.completedAt)
        put("generation", t.generation)
        if (t.parentId == null) putNull("parent_id") else put("parent_id", t.parentId)
        put("gene", t.gene)
        if (t.dailyTemplateId == null) putNull("daily_template_id") else put("daily_template_id", t.dailyTemplateId)
        if (t.dailyKey == null) putNull("daily_key") else put("daily_key", t.dailyKey)
        if (t.weeklyTemplateId == null) putNull("weekly_template_id") else put("weekly_template_id", t.weeklyTemplateId)
        if (t.weeklyKey == null) putNull("weekly_key") else put("weekly_key", t.weeklyKey)
    }

    // ────────────────────────── 每日任务模板 ──────────────────────────

    suspend fun loadDailyTemplates(): List<DailyTemplate> = withContext(Dispatchers.IO) {
        val out = ArrayList<DailyTemplate>()
        helper.readableDatabase.rawQuery(
            "SELECT ${DAILY_COLUMNS.joinToString(",")} FROM $DAILY ORDER BY sort_order ASC", null,
        ).use { c ->
            while (c.moveToNext()) {
                out += DailyTemplate(
                    id = c.getString(0),
                    title = c.getString(1),
                    note = c.getString(2) ?: "",
                    enabled = c.getInt(3) != 0,
                    createdAt = c.getLong(4),
                    sortOrder = c.getInt(5),
                )
            }
        }
        out
    }

    suspend fun saveDailyTemplates(templates: List<DailyTemplate>) = withContext(Dispatchers.IO) {
        val db = helper.writableDatabase
        db.beginTransaction()
        try {
            db.delete(DAILY, null, null)
            for (d in templates) {
                db.insertWithOnConflict(DAILY, null, ContentValues().apply {
                    put("id", d.id)
                    put("title", d.title)
                    put("note", d.note)
                    put("enabled", if (d.enabled) 1 else 0)
                    put("created_at", d.createdAt)
                    put("sort_order", d.sortOrder)
                }, SQLiteDatabase.CONFLICT_REPLACE)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    // ────────────────────────── 每周任务模板 ──────────────────────────

    suspend fun loadWeeklyTemplates(): List<WeeklyTemplate> = withContext(Dispatchers.IO) {
        val out = ArrayList<WeeklyTemplate>()
        helper.readableDatabase.rawQuery(
            "SELECT ${WEEKLY_COLUMNS.joinToString(",")} FROM $WEEKLY ORDER BY sort_order ASC", null,
        ).use { c ->
            while (c.moveToNext()) {
                out += WeeklyTemplate(
                    id = c.getString(0),
                    title = c.getString(1),
                    note = c.getString(2) ?: "",
                    enabled = c.getInt(3) != 0,
                    createdAt = c.getLong(4),
                    sortOrder = c.getInt(5),
                    weekday = c.getInt(6).coerceIn(1, 7),
                )
            }
        }
        out
    }

    suspend fun saveWeeklyTemplates(templates: List<WeeklyTemplate>) = withContext(Dispatchers.IO) {
        val db = helper.writableDatabase
        db.beginTransaction()
        try {
            db.delete(WEEKLY, null, null)
            for (w in templates) {
                db.insertWithOnConflict(WEEKLY, null, ContentValues().apply {
                    put("id", w.id)
                    put("title", w.title)
                    put("note", w.note)
                    put("enabled", if (w.enabled) 1 else 0)
                    put("created_at", w.createdAt)
                    put("sort_order", w.sortOrder)
                    put("weekday", w.weekday.coerceIn(1, 7))
                }, SQLiteDatabase.CONFLICT_REPLACE)
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    // ────────────────────────── 配置 ──────────────────────────

    fun saveConfig(config: MaggotConfig) {
        prefs.edit().putString(KEY_CONFIG, ConfigCodec.encode(config)).apply()
    }

    fun loadConfig(): MaggotConfig? =
        prefs.getString(KEY_CONFIG, null)?.let { ConfigCodec.decode(it) }

    fun markSeen(now: Long = System.currentTimeMillis()) {
        prefs.edit().putLong(KEY_LAST_SEEN, now).apply()
    }

    /** 上次退出时的时间戳；首次运行返回 0（不补算）。 */
    fun lastSeenAt(): Long = prefs.getLong(KEY_LAST_SEEN, 0L)

    private companion object {
        fun statusToInt(s: TaskStatus) = when (s) {
            TaskStatus.TODO -> 0
            TaskStatus.DOING -> 1
            TaskStatus.DONE -> 2
        }

        fun statusFromInt(v: Int) = when (v) {
            1 -> TaskStatus.DOING
            2 -> TaskStatus.DONE
            else -> TaskStatus.TODO
        }
    }
}

/**
 * 配置用手写 JSON —— 只有十一个字段，不值得为它引序列化库，
 * 而且这样 Windows 端和 Android 端的配置格式能保持肉眼可比。
 */
internal object ConfigCodec {

    fun encode(c: MaggotConfig): String = buildString {
        append('{')
        append("\"running\":").append(c.running).append(',')
        append("\"rotSpeed\":").append(c.rotSpeed).append(',')
        append("\"baseRotMinutes\":").append(c.baseRotMinutes).append(',')
        append("\"breedEnabled\":").append(c.breedEnabled).append(',')
        append("\"breedSeconds\":").append(c.breedSeconds).append(',')
        append("\"maxFlies\":").append(c.maxFlies).append(',')
        append("\"maxTasks\":").append(c.maxTasks).append(',')
        append("\"clickThrough\":").append(c.clickThrough).append(',')
        append("\"soundEnabled\":").append(c.soundEnabled).append(',')
        append("\"notifyOnFly\":").append(c.notifyOnFly).append(',')
        append("\"dailyEnabled\":").append(c.dailyEnabled).append(',')
        append("\"weeklyEnabled\":").append(c.weeklyEnabled)
        append('}')
    }

    fun decode(raw: String): MaggotConfig? = runCatching {
        val o = org.json.JSONObject(raw)
        val d = MaggotConfig()
        MaggotConfig(
            running = o.optBoolean("running", d.running),
            rotSpeed = o.optDouble("rotSpeed", d.rotSpeed.toDouble()).toFloat(),
            baseRotMinutes = o.optInt("baseRotMinutes", d.baseRotMinutes),
            breedEnabled = o.optBoolean("breedEnabled", d.breedEnabled),
            breedSeconds = o.optInt("breedSeconds", d.breedSeconds),
            maxFlies = o.optInt("maxFlies", d.maxFlies),
            maxTasks = o.optInt("maxTasks", d.maxTasks),
            clickThrough = o.optBoolean("clickThrough", d.clickThrough),
            soundEnabled = o.optBoolean("soundEnabled", d.soundEnabled),
            notifyOnFly = o.optBoolean("notifyOnFly", d.notifyOnFly),
            dailyEnabled = o.optBoolean("dailyEnabled", d.dailyEnabled),
            weeklyEnabled = o.optBoolean("weeklyEnabled", d.weeklyEnabled),
        )
    }.getOrNull()
}
