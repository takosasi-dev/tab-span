// MediaCodec で H.264(Annex B)を復号し、Surface に出す。Surface と大きさ(sps/pps)の両方が揃ったら作り、
// どちらかが変われば作り直す。キーフレームが来るまでは捨てる。到着と復号の時刻・捨てた数・エラーの数を数える。
package io.github.takosasidev.tabspan.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer
import java.util.ArrayDeque
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** 1 秒ぶんの数。[frames] は (seq, 到着 µs, 復号 µs) で最大 60 件。時刻は System.nanoTime() / 1000。 */
class DecodeStats(val decoded: Int, val dropped: Int, val decoderErrors: Int, val frames: List<LongArray>)

/**
 * 呼び出しはどのスレッドからでもよい(中では専用のスレッド 1 本で MediaCodec を扱う)。
 * [onNeedKeyframe] は、デコーダーのエラー・作り直し・捨てた後に、デコーダーのスレッドから呼ばれる。
 */
class VideoDecoder(private val onNeedKeyframe: () -> Unit) {
    private class Config(val width: Int, val height: Int, val sps: ByteArray, val pps: ByteArray)
    private class Input(val seq: Long, val data: ByteArray, val offset: Int, val length: Int, val arriveUs: Long)

    private val thread = HandlerThread("TabSpanDecoder").apply { start() }
    private val handler = Handler(thread.looper)

    // ここから下の 8 つはデコーダーのスレッドだけが触る
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var config: Config? = null
    private var waitingForKey = true
    private var lastPts = 0L
    private val freeInputs = ArrayDeque<Int>()
    private val pending = ArrayDeque<Input>()
    private val inFlight = ArrayDeque<LongArray>() // [pts, seq, 到着 µs]

    private val statsLock = Any()
    private var decoded = 0
    private var dropped = 0
    private var errors = 0
    private val frames = ArrayDeque<LongArray>()

    /** Surface を渡す・外す。外すときは、戻る前にデコーダーを止める(消えた Surface に描かせない)。 */
    fun setSurface(s: Surface?) = runAndWait {
        surface = s
        rebuild(fromConfig = false)
    }

    /** StreamConfig を受けた。sps/pps は開始コードを除いた NAL。 */
    fun configure(width: Int, height: Int, sps: ByteArray, pps: ByteArray) = handler.post {
        config = Config(width, height, sps, pps)
        dropPending()
        rebuild(fromConfig = true)
    }

    /** セッションが終わった。次の configure まで何も出さない。 */
    fun stop() = handler.post {
        config = null
        releaseCodec()
        dropPending()
    }

    /** VideoFrame の data([offset] から [length] バイト、Annex B)を入れる。[data] はこの後で書き換えない。 */
    fun queue(seq: Long, key: Boolean, data: ByteArray, offset: Int, length: Int, arriveUs: Long) = handler.post {
        // デコーダーがまだ無い(Surface 待ち)間も、キーフレームから先は溜めておき、できたら流す
        if (config == null) {
            countDropped(1)
            return@post
        }
        if (waitingForKey) {
            if (!key) {
                countDropped(1)
                return@post
            }
            waitingForKey = false
        }
        pending.add(Input(seq, data, offset, length, arriveUs))
        if (pending.size > PENDING_LIMIT) {
            // デコーダーが追いつかない: 溜まった分を捨ててキーフレームからやり直す
            countDropped(pending.size)
            pending.clear()
            needKey()
            return@post
        }
        feed()
    }

    /** 前に呼んでからの数を取り出して 0 に戻す。 */
    fun takeStats(): DecodeStats = synchronized(statsLock) {
        DecodeStats(decoded, dropped, errors, frames.toList()).also {
            decoded = 0
            dropped = 0
            errors = 0
            frames.clear()
        }
    }

    /** もう使わない。 */
    fun release() {
        runAndWait {
            releaseCodec()
            dropPending()
        }
        thread.quitSafely()
    }

    private fun rebuild(fromConfig: Boolean) {
        // 動いていたデコーダーを替えるなら、溜めた続きのフレームは新しい方では読めない
        if (codec != null) {
            releaseCodec()
            dropPending()
        }
        val s = surface ?: return
        val c = config ?: return
        if (!s.isValid) return
        val info = AvcCaps.pick()
        if (info == null) {
            countError("no video/avc decoder")
            return
        }
        try {
            val format = MediaFormat.createVideoFormat(AvcCaps.MIME, c.width, c.height)
            format.setByteBuffer("csd-0", ByteBuffer.wrap(START_CODE + c.sps))
            format.setByteBuffer("csd-1", ByteBuffer.wrap(START_CODE + c.pps))
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, minOf(c.width * c.height * 3 / 2, MAX_INPUT))
            val made = MediaCodec.createByCodecName(info.name)
            codec = made
            made.setCallback(Callbacks(made), handler)
            made.configure(format, s, null, 0)
            made.start()
            Log.i(TAG, "decoder ${info.name} ${c.width}x${c.height} started")
        } catch (e: Exception) {
            countError("decoder start failed: ${e.javaClass.simpleName}: ${e.message}")
            releaseCodec()
            return
        }
        // 新しい StreamConfig の直後は PC がキーフレームを送ってくる。溜めたキーフレームも無ければ頼む
        if (!fromConfig && pending.isEmpty()) needKey()
    }

    /** デコーダーを片付ける。溜めたフレーム(pending)は残す。 */
    private fun releaseCodec() {
        freeInputs.clear()
        inFlight.clear()
        val c = codec ?: return
        codec = null
        try {
            c.stop()
        } catch (_: Exception) {
        }
        try {
            c.release()
        } catch (e: Exception) {
            Log.w(TAG, "decoder release failed: ${e.javaClass.simpleName}")
        }
    }

    /** 溜めたフレームを捨て、次のキーフレームを待つ。 */
    private fun dropPending() {
        countDropped(pending.size)
        pending.clear()
        waitingForKey = true
    }

    private fun feed() {
        val c = codec ?: return
        while (freeInputs.isNotEmpty() && pending.isNotEmpty()) {
            val index = freeInputs.poll()!!
            val frame = pending.poll()!!
            try {
                val buffer = c.getInputBuffer(index)!!
                if (buffer.capacity() < frame.length) {
                    freeInputs.addFirst(index)
                    countDropped(1 + pending.size)
                    pending.clear()
                    Log.w(TAG, "frame ${frame.length} bytes exceeds input buffer ${buffer.capacity()}")
                    needKey()
                    return
                }
                buffer.clear()
                buffer.put(frame.data, frame.offset, frame.length)
                // pts は増えるだけの値にする(並べ替えはしないので、出てきた順に突き合わせる)
                val pts = maxOf(frame.arriveUs, lastPts + 1)
                lastPts = pts
                c.queueInputBuffer(index, 0, frame.length, pts, 0)
                inFlight.add(longArrayOf(pts, frame.seq, frame.arriveUs))
                if (inFlight.size > IN_FLIGHT_LIMIT) inFlight.poll()
            } catch (e: Exception) {
                onCodecError(c, e)
                return
            }
        }
    }

    private fun onCodecError(c: MediaCodec, e: Exception) {
        if (c !== codec) return
        countError("decoder error: ${e.javaClass.simpleName}: ${e.message}")
        // コールバックの中から release しない。次の順番で作り直す
        handler.post { if (codec === c) rebuild(fromConfig = false) }
    }

    private fun needKey() {
        waitingForKey = true
        onNeedKeyframe()
    }

    private fun countDropped(n: Int) {
        if (n > 0) synchronized(statsLock) { dropped += n }
    }

    private fun countError(message: String) {
        Log.w(TAG, message)
        synchronized(statsLock) { errors++ }
    }

    private fun runAndWait(block: () -> Unit) {
        val done = CountDownLatch(1)
        handler.post {
            try {
                block()
            } finally {
                done.countDown()
            }
        }
        // ponytail: デコーダーが固まったら 2 秒で諦めて進む(画面のスレッドを止め続けない)
        done.await(2, TimeUnit.SECONDS)
    }

    private inner class Callbacks(private val c: MediaCodec) : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            if (c !== this@VideoDecoder.codec) return
            freeInputs.add(index)
            feed()
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (c !== this@VideoDecoder.codec) return
            val now = System.nanoTime() / 1000
            val render = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM == 0
            try {
                c.releaseOutputBuffer(index, render)
            } catch (e: Exception) {
                onCodecError(c, e)
                return
            }
            if (!render) return
            // 出てきた順に突き合わせる。デコーダーが飲み込んだ入力は飛ばす
            var match: LongArray? = null
            while (inFlight.isNotEmpty()) {
                val head = inFlight.poll()!!
                if (head[0] == info.presentationTimeUs) {
                    match = head
                    break
                }
            }
            synchronized(statsLock) {
                decoded++
                if (match != null) {
                    frames.add(longArrayOf(match[1], match[2], now))
                    if (frames.size > STATS_FRAMES) frames.poll()
                }
            }
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) = onCodecError(c, e)

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
    }

    private companion object {
        const val TAG = "TabSpan"
        val START_CODE = byteArrayOf(0, 0, 0, 1)
        const val MAX_INPUT = 4 * 1024 * 1024
        const val PENDING_LIMIT = 30
        const val IN_FLIGHT_LIMIT = 120
        const val STATS_FRAMES = 60
    }
}
