// 端末の調べ物: Android の版、画面の大きさ、video/avc のデコーダーの能力と、候補ごとの configure の試し。
// 行は溜めずに出たそばから渡す(ネイティブ側で固まっても、logcat に止まった場所が残るように)。
// API 25 以上の呼び出しを書かない(dtab d-01J は API 24)。
package io.github.takosasidev.tabspan.probe

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.util.DisplayMetrics
import android.util.Log
import android.view.Display

object DeviceSurvey {
    private const val AVC = "video/avc"

    fun run(display: Display, emit: (String) -> Unit) {
        emit("--- 端末 ---")
        emit("Android: ${Build.VERSION.RELEASE}")
        emit("API: ${Build.VERSION.SDK_INT}")
        emit("メーカー: ${Build.MANUFACTURER}")
        emit("機種: ${Build.MODEL}")

        val metrics = DisplayMetrics()
        display.getRealMetrics(metrics)
        emit("画面(getRealMetrics): ${metrics.widthPixels}x${metrics.heightPixels}")
        emit("画面(横向きに直した値): ${ProbeText.landscape(metrics.widthPixels, metrics.heightPixels)}")
        emit("density: ${metrics.density}")
        emit("densityDpi: ${metrics.densityDpi}")
        emit("xdpi/ydpi: ${metrics.xdpi}/${metrics.ydpi}")

        val decoders = avcDecoders()
        emit("--- $AVC のデコーダー: ${decoders.size} 個 ---")
        decoders.forEachIndexed { index, info -> describe(index, info, emit) }

        // 実際に作って configure する試しは最後に回す(ベンダーの実装が固まる・落ちることがあるため)
        emit("--- configure の試し ---")
        for (info in decoders) {
            for ((size, fps) in ProbeText.CANDIDATES) {
                Log.i(MainActivity.TAG, "試す: ${info.name} $size@$fps")
                emit("[${info.name}] $size@$fps configure: ${tryConfigure(info.name, size, fps)}")
            }
        }
    }

    /** video/avc のデコーダー(MediaCodecList の順)。 */
    fun avcDecoders(): List<MediaCodecInfo> = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        .filter { info -> !info.isEncoder && info.supportedTypes.any { it.equals(AVC, ignoreCase = true) } }

    private fun describe(index: Int, info: MediaCodecInfo, emit: (String) -> Unit) {
        val name = info.name
        emit("デコーダー ${index + 1}: $name")
        val caps = info.getCapabilitiesForType(AVC)
        val profiles = caps.profileLevels.joinToString(", ") {
            "${ProbeText.avcProfileName(it.profile)}/${ProbeText.avcLevelName(it.level)}"
        }
        emit("[$name] プロファイル/レベル: $profiles")
        emit("[$name] 色の形式: ${caps.colorFormats.joinToString(", ") { ProbeText.colorFormatName(it) }}")
        val video = caps.videoCapabilities
        emit("[$name] 幅 ${video.supportedWidths} / 高さ ${video.supportedHeights} / 揃え ${video.widthAlignment}x${video.heightAlignment}")
        for ((size, fps) in ProbeText.CANDIDATES) {
            val asIs = video.areSizeAndRateSupported(size.width, size.height, fps.toDouble())
            val swap = size.swapped
            val swapped = video.areSizeAndRateSupported(swap.width, swap.height, fps.toDouble())
            emit("[$name] $size@$fps 申告: ${ProbeText.yesNo(asIs)} / 縦横を入れ替えた $swap@$fps: ${ProbeText.yesNo(swapped)}")
        }
    }

    private fun tryConfigure(name: String, size: Size, fps: Int): String {
        var codec: MediaCodec? = null
        var stage = "createByCodecName"
        val result = try {
            codec = MediaCodec.createByCodecName(name)
            stage = "configure"
            val format = MediaFormat.createVideoFormat(AVC, size.width, size.height)
            format.setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            codec.configure(format, null, null, 0)
            "成功"
        } catch (e: Exception) {
            "失敗 ($stage) ${describe(e)}"
        } finally {
            try {
                codec?.release()
            } catch (e: Exception) {
                Log.i(MainActivity.TAG, "[$name] $size@$fps release で例外: ${describe(e)}")
            }
        }
        return result
    }

    fun describe(e: Exception): String {
        val base = "${e.javaClass.name}: ${e.message}"
        // CodecException は message が空のことが多いので、diagnosticInfo とエラーの番号も添える
        return if (e is MediaCodec.CodecException) {
            "$base / diagnosticInfo=${e.diagnosticInfo} / errorCode=${e.errorCode}"
        } else {
            base
        }
    }
}
