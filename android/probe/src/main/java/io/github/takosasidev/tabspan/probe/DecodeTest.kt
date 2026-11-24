// 復号のテスト: getExternalFilesDir の tabspan_<幅>x<高さ>_<fps>.h264 を、video/avc のデコーダーごとに実際に復号して速さと遅れを測る。
// 出力は ImageReader(PRIVATE)の Surface に描いて捨てる。Surface が使えなければ ByteBuffer の出力で 1 回やり直す。
// API 25 以上の呼び出しを書かない(dtab d-01J は API 24)。
package io.github.takosasidev.tabspan.probe

import android.graphics.ImageFormat
import android.media.ImageReader
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import java.io.File
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

object DecodeTest {
    private const val TIMEOUT_NS = 20_000_000_000L // 測定 1 回(速さ・遅れのそれぞれ)ごと
    private const val POLL_US = 1_000L

    /** 測定 1 回の結果。時間切れ・例外のときも、そこまでの数を持って [error] に理由を入れる。 */
    private class Run(
        val frames: Int,
        val seconds: Double,
        val latenciesMs: List<Double>,
        val firstAfter: Int,
        val maxQueueLateMs: Double,
        val images: Int,
        val imageError: String?,
        val outputFormat: String?,
        val error: String?,
        /** 表に出す短い理由(「時間切れ」か例外の型の名前)。 */
        val errorShort: String?,
    ) {
        val fps get() = if (seconds > 0) frames / seconds else null
    }

    /** ImageReader の用意か configure で失敗した(ByteBuffer でやり直す合図)。 */
    private class SurfaceFailed(val stage: String, cause: Exception) : Exception(cause)

    private class Timeout(message: String) : Exception(message)

    fun run(dir: File?, emit: (String) -> Unit) {
        emit("--- 復号のテスト ---")
        val files = dir?.listFiles()?.filter { DecodeText.parseFileName(it.name) != null }?.sortedBy { it.name }.orEmpty()
        emit("置き場: ${dir?.absolutePath ?: "(getExternalFilesDir が null)"} / ファイル ${files.size} 本")
        val decoders = DeviceSurvey.avcDecoders().map { it.name }.filter { !it.endsWith(".secure") }
        emit("デコーダー: ${decoders.joinToString(", ")}(.secure は飛ばす)")
        emit("時間切れ: 測定 1 回ごとに ${TIMEOUT_NS / 1_000_000_000} 秒 / SPS・PPS は csd-0・csd-1 で configure に渡し、最初のユニットにも残す")

        val cells = ArrayList<DecodeCell>()
        val clips = ArrayList<Clip>()
        for (file in files) {
            val clip = DecodeText.parseFileName(file.name)!!
            val data = try {
                file.readBytes()
            } catch (e: Exception) {
                emit("${file.name}: 読めない ${DeviceSurvey.describe(e)}")
                continue
            }
            val units = DecodeText.splitAccessUnits(data)
            if (units.isEmpty()) {
                emit("${file.name}: AUD(NAL 種類 9)が無いので区切れない (${data.size} バイト)")
                continue
            }
            clips.add(clip)
            val sps = DecodeText.firstNal(units[0], DecodeText.SPS)
            val pps = DecodeText.firstNal(units[0], DecodeText.PPS)
            emit("${file.name}: ${data.size} バイト / ユニット ${units.size} 個 / 最大 ${units.maxOf { it.size }} バイト / " +
                "最初のユニットの NAL ${DecodeText.nalTypes(units[0])} / SPS ${sps?.size ?: "無し"} PPS ${pps?.size ?: "無し"}")
            for (name in decoders) cells.add(measure(name, clip, units, sps, pps, emit))
        }

        emit("--- 表(速さの実測 fps / 遅れの中央値。BB は ByteBuffer の出力で測った物) ---")
        DecodeText.table(clips, decoders, cells).forEach(emit)
        emit(DecodeText.r4Line(cells))
    }

    /** ファイル 1 本 × デコーダー 1 つ: 速さ → 遅れ の順に測って行を出す。 */
    private fun measure(name: String, clip: Clip, units: List<ByteArray>, sps: ByteArray?, pps: ByteArray?, emit: (String) -> Unit): DecodeCell {
        val tag = "[$name] $clip"
        var surface = true
        fun once(paced: Boolean): Run {
            val what = if (paced) "遅れ" else "速さ"
            if (surface) {
                Log.i(MainActivity.TAG, "試す: $tag $what Surface")
                try {
                    val run = decode(name, clip, units, sps, pps, paced, true)
                    // 失敗して、絵を受け取れなかったか 1 枚も出なかったら、Surface が合わない恐れがあるので ByteBuffer でやり直す
                    if (run.error == null || (run.imageError == null && run.frames > 0)) return run
                    emit("$tag $what: Surface で測れない(${run.error} / ${run.frames} 枚 / 受け取りの例外 ${run.imageError ?: "無し"})→ ByteBuffer でやり直す")
                } catch (e: SurfaceFailed) {
                    emit("$tag $what: Surface で ${e.stage} 失敗 ${DeviceSurvey.describe(e.cause as Exception)} → ByteBuffer でやり直す")
                }
                surface = false
            }
            Log.i(MainActivity.TAG, "試す: $tag $what ByteBuffer")
            return try {
                decode(name, clip, units, sps, pps, paced, false)
            } catch (e: SurfaceFailed) {
                Run(0, 0.0, emptyList(), -1, 0.0, 0, null, null, "${e.stage} 失敗 ${DeviceSurvey.describe(e.cause as Exception)}", "${e.stage}失敗")
            }
        }

        val speed = once(paced = false)
        val speedSurface = surface // 遅れの測定で ByteBuffer に替わっても、表には速さを測った方を出す
        emit("$tag 出力=${if (surface) "Surface" else "ByteBuffer"} 速さ: ${speed.frames}/${units.size} 枚 ${DecodeText.f1(speed.seconds)} 秒 ${DecodeText.f1(speed.fps)} fps" +
            (if (surface) " / Surface のリスナー ${speed.images} 回" else "") +
            (speed.imageError?.let { " / 受け取りで例外 $it" } ?: "") +
            (speed.outputFormat?.let { " / 出力の形式 $it" } ?: "") +
            (speed.error?.let { " / $it" } ?: ""))
        if (speed.frames == 0) {
            emit("$tag 遅れ: 速さの測定で 1 枚も出なかったので測らない")
            return DecodeCell(clip, name, speedSurface, 0, units.size, speed.fps, null, "${speed.errorShort ?: "0枚"} 0/${units.size}枚")
        }
        val late = once(paced = true)
        val median = DecodeText.median(late.latenciesMs)
        emit("$tag 出力=${if (surface) "Surface" else "ByteBuffer"} 遅れ: 中央 ${DecodeText.f1(median)} ms / 最大 ${DecodeText.f1(late.latenciesMs.maxOrNull())} ms / " +
            "最初の 1 枚は ${if (late.firstAfter < 0) "-" else late.firstAfter} 個入れた後 / ${late.frames}/${units.size} 枚 / " +
            "予定からの入れ遅れ 最大 ${DecodeText.f1(late.maxQueueLateMs)} ms" + (late.imageError?.let { " / 受け取りで例外 $it" } ?: "") + (late.error?.let { " / $it" } ?: ""))

        val failure = speed.errorShort?.let { "$it ${speed.frames}/${units.size}枚" }
        return DecodeCell(clip, name, speedSurface, speed.frames, units.size, speed.fps, median, failure)
    }

    /**
     * 1 回の復号。[paced] が偽ならできるだけ速く入れ、真なら fps の間隔(開始 + k×間隔の絶対時刻)で入れる。
     * ImageReader の用意と configure の失敗だけは [SurfaceFailed] で投げ、それ以外の例外・時間切れは Run.error に入れて返す。
     */
    private fun decode(
        name: String, clip: Clip, units: List<ByteArray>, sps: ByteArray?, pps: ByteArray?,
        paced: Boolean, useSurface: Boolean,
    ): Run {
        var codec: MediaCodec? = null
        var reader: ImageReader? = null
        var thread: HandlerThread? = null
        val images = AtomicInteger()
        val imageError = AtomicReference<String?>()
        val queuedAt = LongArray(units.size)
        val latencies = ArrayList<Double>()
        var frames = 0
        var next = 0
        var firstAfter = -1
        var maxLateNs = 0L
        var outputFormat: String? = null
        var began = 0L // 最初のユニットを入れに行った時刻。速さはここから最後の絵までで測る
        var last = 0L
        fun result(error: Exception?) = Run(
            frames, if (began == 0L) 0.0 else (last - began) / 1e9, latencies, firstAfter, maxLateNs / 1e6,
            images.get(), imageError.get(), outputFormat, error?.let { DeviceSurvey.describe(it) },
            error?.let { if (it is Timeout) "時間切れ" else it.javaClass.simpleName },
        )
        try {
            val format = MediaFormat.createVideoFormat("video/avc", clip.size.width, clip.size.height)
            format.setInteger(MediaFormat.KEY_FRAME_RATE, clip.fps) // 起動時の configure の試しと同じ形にそろえる
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, units.maxOf { it.size })
            sps?.let { format.setByteBuffer("csd-0", ByteBuffer.wrap(it)) }
            pps?.let { format.setByteBuffer("csd-1", ByteBuffer.wrap(it)) }
            var stage = "ImageReader の用意"
            val c = try {
                if (useSurface) {
                    val t = HandlerThread("TabSpanImages")
                    thread = t
                    t.start()
                    val r0 = ImageReader.newInstance(clip.size.width, clip.size.height, ImageFormat.PRIVATE, 4)
                    reader = r0
                    r0.setOnImageAvailableListener({ r ->
                        // ここで投げるとプロセスごと落ちるので、全部受けて最初の 1 つを残す
                        try {
                            r.acquireLatestImage()?.close()
                            images.incrementAndGet()
                        } catch (e: Exception) {
                            imageError.compareAndSet(null, DeviceSurvey.describe(e))
                        }
                    }, Handler(t.looper))
                }
                stage = "createByCodecName"
                val made = MediaCodec.createByCodecName(name)
                codec = made
                stage = "configure"
                made.configure(format, reader?.surface, null, 0)
                made
            } catch (e: Exception) {
                if (useSurface && stage != "createByCodecName") throw SurfaceFailed(stage, e)
                throw e
            }
            c.start()

            val frameNs = 1_000_000_000L / clip.fps
            val info = MediaCodec.BufferInfo()
            var eosQueued = false
            began = System.nanoTime()
            last = began
            while (frames < units.size) {
                val now = System.nanoTime()
                if (now - began > TIMEOUT_NS) throw Timeout("時間切れ(入れた $next/${units.size}、出た $frames)")
                val due = began + next * frameNs
                if (!eosQueued && (!paced || now >= due)) {
                    val index = c.dequeueInputBuffer(0)
                    if (index >= 0) {
                        if (next < units.size) {
                            val unit = units[next]
                            val buffer = c.getInputBuffer(index)!!
                            buffer.clear()
                            if (buffer.capacity() < unit.size) {
                                throw IllegalStateException("入力のバッファが小さい(容量 ${buffer.capacity()} < ユニット $next の ${unit.size} バイト)")
                            }
                            buffer.put(unit)
                            val queued = System.nanoTime()
                            if (paced) maxLateNs = maxOf(maxLateNs, queued - due)
                            queuedAt[next] = queued
                            c.queueInputBuffer(index, 0, unit.size, DecodeText.ptsOf(next, clip.fps), 0)
                            next++
                        } else {
                            c.queueInputBuffer(index, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                            eosQueued = true
                        }
                    }
                }
                val out = c.dequeueOutputBuffer(info, POLL_US)
                if (out >= 0) {
                    val got = System.nanoTime()
                    val eos = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                    if (!(eos && info.size == 0)) {
                        frames++
                        last = got
                        if (firstAfter < 0) firstAfter = next
                        DecodeText.indexOf(info.presentationTimeUs, clip.fps, units.size)?.let { k ->
                            if (queuedAt[k] != 0L) latencies.add((got - queuedAt[k]) / 1e6)
                        }
                    }
                    c.releaseOutputBuffer(out, useSurface)
                    if (eos) break // 出た数がユニットより少なくても、EOS が来たら終わる
                } else if (out == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    outputFormat = c.outputFormat.toString()
                }
            }
            return result(null)
        } catch (e: SurfaceFailed) {
            throw e
        } catch (e: Exception) {
            return result(e)
        } finally {
            // codec → reader → スレッドの順に片付ける
            try {
                codec?.release()
            } catch (e: Exception) {
                Log.i(MainActivity.TAG, "[$name] $clip release で例外: ${DeviceSurvey.describe(e)}")
            }
            reader?.close()
            thread?.quitSafely()
        }
    }
}
