// video/avc のデコーダーの選び方と能力: ハードウェアの物(OMX.google. と c2.android. で始まらない物)を先に、
// 無ければ最初の物。Hello に入れる「復号できる大きさ」は、申告(areSizeAndRateSupported)が真の物に、
// 実際に configure が通った物を足す(申告は実力より狭い端末がある。復号に失敗すれば PC が 1 段ずつ下げる)。
package io.github.takosasidev.tabspan.video

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.util.Log

/** 大きさとフレームレート 1 組。 */
data class VideoMode(val width: Int, val height: Int, val fps: Int)

object AvcCaps {
    const val MIME = "video/avc"

    /** 送る大きさの候補(横向き)。PC 側の計画と同じ表。 */
    val CANDIDATES: List<VideoMode> = listOf(2560 to 1600, 2560 to 1440, 1920 to 1200, 1920 to 1080, 1280 to 800, 1280 to 720)
        .flatMap { (w, h) -> listOf(VideoMode(w, h, 60), VideoMode(w, h, 30)) }

    /** 使うデコーダー。無ければ null。 */
    fun pick(): MediaCodecInfo? {
        val all = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
            !info.isEncoder && !info.name.endsWith(".secure") && info.supportedTypes.any { it.equals(MIME, ignoreCase = true) }
        }
        return all.firstOrNull { !it.name.startsWith("OMX.google.") && !it.name.startsWith("c2.android.") } ?: all.firstOrNull()
    }

    @Volatile private var cached: Pair<String, List<VideoMode>>? = null

    /** 申告が真の物と、configure が通った物の和。デコーダーを作って試すので裏のスレッドから呼ぶ。結果はプロセスの間覚える。 */
    fun supported(info: MediaCodecInfo): List<VideoMode> {
        cached?.let { (name, modes) -> if (name == info.name) return modes }
        val video = info.getCapabilitiesForType(MIME).videoCapabilities
        val modes = CANDIDATES.filter { video.areSizeAndRateSupported(it.width, it.height, it.fps.toDouble()) || configures(info.name, it) }
        cached = info.name to modes
        return modes
    }

    private fun configures(codecName: String, m: VideoMode): Boolean {
        var codec: MediaCodec? = null
        return try {
            codec = MediaCodec.createByCodecName(codecName)
            val format = MediaFormat.createVideoFormat(MIME, m.width, m.height).apply { setInteger(MediaFormat.KEY_FRAME_RATE, m.fps) }
            codec.configure(format, null, null, 0)
            true
        } catch (e: Exception) {
            Log.i("TabSpan", "configure できない: ${m.width}x${m.height}@${m.fps} ${e.javaClass.simpleName}")
            false
        } finally {
            try {
                codec?.release()
            } catch (_: Exception) {
            }
        }
    }

    /** "baseline" "main" "high" のうち、デコーダーが挙げた物(重なりなし)。 */
    fun profiles(info: MediaCodecInfo): List<String> {
        val names = info.getCapabilitiesForType(MIME).profileLevels.mapNotNull {
            when (it.profile) {
                // 0x10000 と 0x80000 は Constrained Baseline / Constrained High(定数は API 27 からなので数で書く)
                MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline, 0x10000 -> "baseline"
                MediaCodecInfo.CodecProfileLevel.AVCProfileMain -> "main"
                MediaCodecInfo.CodecProfileLevel.AVCProfileHigh, 0x80000 -> "high"
                else -> null
            }
        }.toSet()
        return listOf("baseline", "main", "high").filter { it in names }
    }
}
