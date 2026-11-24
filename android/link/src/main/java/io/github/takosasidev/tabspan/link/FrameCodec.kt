// 通信の枠の読み書き: [type u8][length u32 ビッグエンディアン][payload]。
// 長さが 4 MiB を超えていたら中身を読まずに FrameTooLargeException を投げる(呼び出し側は接続を閉じる。INV-15)。
package io.github.takosasidev.tabspan.link

import java.io.DataInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

/** 受け取った枠 1 つ。[type] は 0〜255。 */
class Frame(val type: Int, val payload: ByteArray)

class FrameTooLargeException(val length: Long) : IOException("frame length $length exceeds ${FrameCodec.MAX_PAYLOAD}")

object FrameCodec {
    const val MAX_PAYLOAD = 4 * 1024 * 1024

    fun encode(type: Int, payload: ByteArray): ByteArray {
        require(type in 0..255) { "type $type" }
        if (payload.size > MAX_PAYLOAD) throw FrameTooLargeException(payload.size.toLong())
        val out = ByteArray(5 + payload.size)
        out[0] = type.toByte()
        out[1] = (payload.size ushr 24).toByte()
        out[2] = (payload.size ushr 16).toByte()
        out[3] = (payload.size ushr 8).toByte()
        out[4] = payload.size.toByte()
        payload.copyInto(out, 5)
        return out
    }

    /** 1 つ読む。枠の境目で流れが終わったら null、枠の途中で終わったら EOFException。 */
    fun read(input: InputStream): Frame? {
        val data = DataInputStream(input)
        val type = data.read()
        if (type < 0) return null
        val length = data.readInt().toLong() and 0xFFFF_FFFFL
        if (length > MAX_PAYLOAD) throw FrameTooLargeException(length)
        val payload = ByteArray(length.toInt())
        data.readFully(payload)
        return Frame(type, payload)
    }

    fun write(output: OutputStream, type: Int, payload: ByteArray) {
        output.write(encode(type, payload))
        output.flush()
    }
}
