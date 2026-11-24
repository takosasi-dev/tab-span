// 枠の中身: 種類の番号、制御の JSON(キーは camelCase、null の項目は書かない)、映像・Ping・Pong のバイナリ。
// 端末が送る物は toJson、受ける物は parseXxx。必須の項目が無い・型が違う・形が壊れている → ProtocolException。
package io.github.takosasidev.tabspan.link

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.net.ProtocolException
import java.nio.ByteBuffer

object MessageType {
    const val HELLO = 0x01
    const val HELLO_ACK = 0x02
    const val PAIR_REQUEST = 0x03
    const val PAIR_CHALLENGE = 0x04
    const val PAIR_ACCEPT = 0x05
    const val PAIR_REJECT = 0x06
    const val STREAM_CONFIG = 0x10
    const val VIDEO_FRAME = 0x11
    const val REQUEST_KEYFRAME = 0x12
    const val STATS = 0x13
    const val TOUCH = 0x20
    const val PING = 0x30
    const val PONG = 0x31
    const val BYE = 0x3F
    const val NOTICE = 0x40
}

const val PROTOCOL_VERSION = 1

data class ScreenInfo(val widthPx: Int, val heightPx: Int, val densityDpi: Int)
data class SizeFps(val width: Int, val height: Int, val fps: Int)
data class DecoderInfo(val profiles: List<String>, val supported: List<SizeFps>)
data class HelloMsg(
    val protocolVersion: Int, val deviceId: String, val token: String?, val deviceName: String,
    val appVersion: String, val screen: ScreenInfo, val decoder: DecoderInfo,
)
data class HelloAckMsg(
    val ok: Boolean, val reason: String?, val pcInstanceId: String, val pcName: String,
    val appVersion: String, val touchEnabled: Boolean,
)
data class PairRequestMsg(val protocolVersion: Int, val deviceId: String, val deviceName: String, val clientNonce: String)
data class PairChallengeMsg(val pcInstanceId: String, val pcName: String, val serverNonce: String)
data class PairAcceptMsg(val token: String)
data class ReasonMsg(val reason: String)
data class StreamConfigMsg(
    val width: Int, val height: Int, val fps: Int, val profile: String,
    val sps: String, val pps: String, val sourceKind: String,
)
/** [value] は JSON の値そのまま(真偽なら Boolean)。 */
data class NoticeMsg(val kind: String, val value: Any?)
data class FrameTiming(val seq: Long, val arriveUs: Long, val decodedUs: Long)
data class StatsMsg(val decoded: Int, val dropped: Int, val decoderErrors: Int, val frames: List<FrameTiming>)

/** VideoFrame。映像の data は [payload] の [dataOffset] から最後まで(写さない)。 */
class VideoFrameMsg(val seq: Long, val key: Boolean, val captureUs: Long, val sendUs: Long, val payload: ByteArray) {
    val dataOffset get() = HEADER
    val dataLength get() = payload.size - HEADER

    companion object {
        const val HEADER = 4 + 1 + 8 + 8
    }
}

object Messages {
    /** Bye・HelloAck・PairReject の reason の値。 */
    val REASONS = arrayOf(
        "version", "auth", "busy", "blocked", "unpaired", "usb_only", "not_accepting",
        "full", "denied", "no_source", "decoder", "shutdown", "user",
    )

    // ---- 端末が送る JSON ----

    fun toJson(m: HelloMsg): ByteArray = JSONObject()
        .put("protocolVersion", m.protocolVersion)
        .put("deviceId", m.deviceId)
        .put("token", m.token) // null なら put は項目を消す(書かない)
        .put("deviceName", m.deviceName)
        .put("appVersion", m.appVersion)
        .put("screen", JSONObject().put("widthPx", m.screen.widthPx).put("heightPx", m.screen.heightPx).put("densityDpi", m.screen.densityDpi))
        .put("decoder", JSONObject()
            .put("profiles", JSONArray(m.decoder.profiles))
            .put("supported", JSONArray(m.decoder.supported.map { JSONObject().put("width", it.width).put("height", it.height).put("fps", it.fps) })))
        .bytes()

    fun toJson(m: PairRequestMsg): ByteArray = JSONObject()
        .put("protocolVersion", m.protocolVersion)
        .put("deviceId", m.deviceId)
        .put("deviceName", m.deviceName)
        .put("clientNonce", m.clientNonce)
        .bytes()

    fun toJson(m: ReasonMsg): ByteArray = JSONObject().put("reason", m.reason).bytes()

    fun toJson(m: StatsMsg): ByteArray = JSONObject()
        .put("decoded", m.decoded)
        .put("dropped", m.dropped)
        .put("decoderErrors", m.decoderErrors)
        .put("frames", JSONArray(m.frames.map { JSONObject().put("seq", it.seq).put("arriveUs", it.arriveUs).put("decodedUs", it.decodedUs) }))
        .bytes()

    // ---- 端末が受ける JSON ----

    fun parseHelloAck(p: ByteArray): HelloAckMsg = json(p) {
        HelloAckMsg(
            it.getBoolean("ok"), it.optStringOrNull("reason"), it.getString("pcInstanceId"), it.getString("pcName"),
            it.getString("appVersion"), it.getBoolean("touchEnabled"),
        )
    }

    fun parsePairChallenge(p: ByteArray): PairChallengeMsg = json(p) {
        PairChallengeMsg(it.getString("pcInstanceId"), it.getString("pcName"), it.getString("serverNonce"))
    }

    fun parsePairAccept(p: ByteArray): PairAcceptMsg = json(p) { PairAcceptMsg(it.getString("token")) }

    /** PairReject と Bye。 */
    fun parseReason(p: ByteArray): ReasonMsg = json(p) { ReasonMsg(it.getString("reason")) }

    fun parseStreamConfig(p: ByteArray): StreamConfigMsg = json(p) {
        StreamConfigMsg(
            it.getInt("width"), it.getInt("height"), it.getInt("fps"), it.getString("profile"),
            it.getString("sps"), it.getString("pps"), it.getString("sourceKind"),
        )
    }

    fun parseNotice(p: ByteArray): NoticeMsg = json(p) { NoticeMsg(it.getString("kind"), if (it.isNull("value")) null else it.get("value")) }

    // ---- バイナリ ----

    fun parseVideoFrame(p: ByteArray): VideoFrameMsg {
        if (p.size < VideoFrameMsg.HEADER) throw ProtocolException("VideoFrame too short: ${p.size}")
        val b = ByteBuffer.wrap(p)
        val seq = b.int.toLong() and 0xFFFF_FFFFL
        val flags = b.get().toInt()
        return VideoFrameMsg(seq, flags and 1 != 0, b.long, b.long, p)
    }

    fun ping(t1Us: Long): ByteArray = ByteBuffer.allocate(8).putLong(t1Us).array()

    fun parsePing(p: ByteArray): Long {
        if (p.size < 8) throw ProtocolException("Ping too short: ${p.size}")
        return ByteBuffer.wrap(p).long
    }

    fun pong(t1Us: Long, t2Us: Long): ByteArray = ByteBuffer.allocate(16).putLong(t1Us).putLong(t2Us).array()

    fun parsePong(p: ByteArray): Pair<Long, Long> {
        if (p.size < 16) throw ProtocolException("Pong too short: ${p.size}")
        val b = ByteBuffer.wrap(p)
        return b.long to b.long
    }

    private fun JSONObject.bytes() = toString().toByteArray(Charsets.UTF_8)

    private fun JSONObject.optStringOrNull(key: String): String? = if (isNull(key)) null else getString(key)

    private inline fun <T> json(p: ByteArray, read: (JSONObject) -> T): T = try {
        read(JSONObject(String(p, Charsets.UTF_8)))
    } catch (e: JSONException) {
        throw ProtocolException("bad json: ${e.message}")
    }
}
