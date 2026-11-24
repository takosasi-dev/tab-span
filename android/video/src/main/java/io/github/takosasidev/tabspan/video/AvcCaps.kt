// video/avc のデコーダーの選び方と能力: ハードウェアの物(OMX.google. と c2.android. で始まらない物)を先に、
// 無ければ最初の物。Hello に入れる「復号できる大きさ」は候補 12 個のうち areSizeAndRateSupported が真の物。
package io.github.takosasidev.tabspan.video

import android.media.MediaCodecInfo
import android.media.MediaCodecList

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

    fun supported(info: MediaCodecInfo): List<VideoMode> {
        val video = info.getCapabilitiesForType(MIME).videoCapabilities
        return CANDIDATES.filter { video.areSizeAndRateSupported(it.width, it.height, it.fps.toDouble()) }
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
