package com.maggot.task.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.floor
import kotlin.math.sin

/**
 * 全部音效。
 *
 * 目标是**恶心**，不是"提示"。系统提示音一律不合格 ——
 * 那听起来像 Outlook 收信，不像有东西在你桌上爆开。
 *
 * 全部程序化合成，零音频资源文件。这与生物绘制是同一个原则：
 * 整个产品不带任何素材。
 */
enum class Sfx(internal val durationMs: Int) {
    /** 拍死爆浆：噪声爆裂 + 下行音高扫频。 */
    SPLAT(170),
    /** 苍蝇嗡鸣：低频锯齿 + 快速颤音，挥之不去。 */
    BUZZ(420),
    /** 卵孵化：极短的低频"啵"，黏腻破壳感。 */
    HATCH(110),
    /** 繁殖：两声快速上行短音 —— "更多了"。 */
    BREED(280),
}

/**
 * 每种音效各自常驻一块 MODE_STATIC buffer，重复播放只需 reload + play，
 * 不会每次事件都新建 AudioTrack。
 */
class SfxPlayer {

    private val tracks = HashMap<Sfx, AudioTrack>()
    private val lastPlayAt = HashMap<Sfx, Long>()

    /**
     * 播放一个音效。任何失败都静默 —— 音效永远不该拖垮渲染循环或主流程。
     *
     * @param force 跳过连点保护（开关打开时的试听要用它）
     */
    fun play(sfx: Sfx, force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (!force) {
            val last = lastPlayAt[sfx] ?: 0L
            // 30ms 内不重复：密集事件否则会叠成一团噪音。
            if (now - last < RETRIGGER_GUARD_MS) return
        }
        lastPlayAt[sfx] = now

        val track = ensureTrack(sfx) ?: return
        runCatching {
            if (track.playState == AudioTrack.PLAYSTATE_PLAYING) track.stop()
            track.reloadStaticData()
            track.play()
        }
    }

    fun splat(force: Boolean = false) = play(Sfx.SPLAT, force)
    fun buzz() = play(Sfx.BUZZ)
    fun hatch() = play(Sfx.HATCH)
    fun breed() = play(Sfx.BREED)

    fun release() {
        tracks.values.forEach { runCatching { it.release() } }
        tracks.clear()
    }

    // ────────────────────────── 内部 ──────────────────────────

    private fun ensureTrack(sfx: Sfx): AudioTrack? {
        tracks[sfx]?.let { if (it.state == AudioTrack.STATE_INITIALIZED) return it }

        val samples = synth(sfx)
        val bytes = samples.size * 2

        val audio = try {
            AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build()
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(SAMPLE_RATE)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                        .build()
                )
                .setBufferSizeInBytes(bytes)
                .setTransferMode(AudioTrack.MODE_STATIC)
                .build()
        } catch (e: Throwable) {
            return null
        }

        return try {
            audio.write(samples, 0, samples.size)
            tracks[sfx] = audio
            audio
        } catch (e: Throwable) {
            runCatching { audio.release() }
            null
        }
    }

    private companion object {
        const val SAMPLE_RATE = 44100
        const val RETRIGGER_GUARD_MS = 30L

        /** 锯齿波，用于苍蝇嗡鸣。 */
        fun saw(phase: Float): Float = 2f * (phase - floor(phase + 0.5f))

        /**
         * 合成一块 PCM。
         *
         * 用固定的 xorshift 而不是 Random，保证每次听到的音效完全一致 ——
         * 随机的噪声每次都不一样会让人以为播放出了问题。
         */
        fun synth(sfx: Sfx): ShortArray {
            val n = SAMPLE_RATE * sfx.durationMs / 1000
            val out = ShortArray(n)
            var seed = 0x13579BDF
            val twoPi = 2f * PI.toFloat()

            for (i in 0 until n) {
                val t = i.toFloat() / SAMPLE_RATE
                val p = i.toFloat() / n

                seed = seed xor (seed shl 13)
                seed = seed xor (seed ushr 17)
                seed = seed xor (seed shl 5)
                val noise = (seed and 0xFFFF) / 32768f - 1f

                val v: Float = when (sfx) {
                    // 噪声爆裂 + 频率从 900Hz 滑到 120Hz + 指数衰减。
                    // 起手 12% 是爆裂（噪声为主），之后是塌陷（音调为主）。
                    Sfx.SPLAT -> {
                        val freq = 900f - 780f * p
                        val tone = sin(twoPi * freq * t)
                        val mix = if (p < 0.12f) 0.75f else 0.18f
                        val env = exp(-9.5f * p) * (1f - exp(-90f * p))
                        (tone * (1f - mix) + noise * mix) * env * 0.62f
                    }

                    // 低频锯齿 + 35Hz 颤音 + 轻微失谐，做出"翅膀在耳朵边"的烦躁感。
                    Sfx.BUZZ -> {
                        val f1 = 168f + 10f * sin(twoPi * 6.5f * t)
                        val f2 = f1 * 1.013f
                        val body = saw(f1 * t) * 0.55f + saw(f2 * t) * 0.35f
                        val tremolo = 0.55f + 0.45f * sin(twoPi * 34f * t)
                        // 两端各 25ms 淡入淡出，避免起止爆音
                        val fade = minOf(1f, p / 0.06f, (1f - p) / 0.06f).coerceAtLeast(0f)
                        body * tremolo * fade * 0.34f
                    }

                    // 极短的低频"啵"：150Hz 衰减正弦 + 起手一个噪声小咔哒。
                    Sfx.HATCH -> {
                        val body = sin(twoPi * (150f - 40f * p) * t) * exp(-26f * p)
                        val click = noise * exp(-95f * p) * 0.45f
                        (body * 0.72f + click) * 0.7f
                    }

                    // 两声快速上行短音，中间留一个短间隙。
                    Sfx.BREED -> {
                        val local: Float
                        val gain: Float
                        when {
                            p < 0.40f -> { local = p / 0.40f; gain = 1f }
                            p < 0.55f -> { local = 0f; gain = 0f }
                            p < 0.95f -> { local = (p - 0.55f) / 0.40f; gain = 1f }
                            else -> { local = 0f; gain = 0f }
                        }
                        if (gain == 0f) 0f
                        else {
                            val f = 300f + 520f * local
                            val env = exp(-4.2f * local) * minOf(1f, local * 12f)
                            sin(twoPi * f * t) * env * 0.5f
                        }
                    }
                }

                out[i] = (v.coerceIn(-1f, 1f) * Short.MAX_VALUE).toInt().toShort()
            }

            // 全局归一化：避免叠出来的峰值削波（削波听感是"破音"，不是"恶心"）。
            var peak = 0f
            for (s in out) {
                val a = abs(s.toInt())
                if (a > peak) peak = a.toFloat()
            }
            if (peak > 0f) {
                val k = (Short.MAX_VALUE * 0.92f) / peak
                if (k < 1f) {
                    for (i in out.indices) {
                        out[i] = (out[i] * k).toInt().toShort()
                    }
                }
            }
            return out
        }
    }
}
