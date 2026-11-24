// バイト列の書き方: Base64(標準の字と = の埋め。JSON の nonce・token・sps・pps)と、指紋の 16 進と SHA-256。
// java.util.Base64 は API 26 から、android.util.Base64 は JVM の単体テストで動かないので、ここに持つ。
package io.github.takosasidev.tabspan.link

import java.net.ProtocolException
import java.security.MessageDigest

object Base64Codec {
    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
    private val VALUES = IntArray(128) { -1 }.also { table -> ALPHABET.forEachIndexed { i, c -> table[c.code] = i } }

    fun encode(bytes: ByteArray): String {
        val sb = StringBuilder((bytes.size + 2) / 3 * 4)
        var i = 0
        while (i < bytes.size) {
            val b0 = bytes[i].toInt() and 0xFF
            val b1 = if (i + 1 < bytes.size) bytes[i + 1].toInt() and 0xFF else 0
            val b2 = if (i + 2 < bytes.size) bytes[i + 2].toInt() and 0xFF else 0
            val n = (b0 shl 16) or (b1 shl 8) or b2
            sb.append(ALPHABET[n ushr 18 and 63]).append(ALPHABET[n ushr 12 and 63])
            sb.append(if (i + 1 < bytes.size) ALPHABET[n ushr 6 and 63] else '=')
            sb.append(if (i + 2 < bytes.size) ALPHABET[n and 63] else '=')
            i += 3
        }
        return sb.toString()
    }

    /** 埋めの付いた標準の Base64 だけを受ける。形が違えば ProtocolException。 */
    fun decode(text: String): ByteArray {
        if (text.length % 4 != 0) throw ProtocolException("base64 length")
        val pad = when {
            text.endsWith("==") -> 2
            text.endsWith("=") -> 1
            else -> 0
        }
        val out = ByteArray(text.length / 4 * 3 - pad)
        var o = 0
        var i = 0
        while (i < text.length) {
            var n = 0
            for (k in 0 until 4) {
                val c = text[i + k]
                val v = if (c == '=' && i + k >= text.length - pad) 0 else if (c.code < 128) VALUES[c.code] else -1
                if (v < 0) throw ProtocolException("base64 char")
                n = (n shl 6) or v
            }
            if (o < out.size) out[o++] = (n ushr 16).toByte()
            if (o < out.size) out[o++] = (n ushr 8).toByte()
            if (o < out.size) out[o++] = n.toByte()
            i += 4
        }
        return out
    }
}

object Hex {
    private const val DIGITS = "0123456789ABCDEF"

    fun of(bytes: ByteArray): String {
        val sb = StringBuilder(bytes.size * 2)
        bytes.forEach { sb.append(DIGITS[it.toInt() shr 4 and 15]).append(DIGITS[it.toInt() and 15]) }
        return sb.toString()
    }

    fun parse(text: String): ByteArray {
        require(text.length % 2 == 0) { "hex length" }
        return ByteArray(text.length / 2) { text.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}

fun sha256(vararg parts: ByteArray): ByteArray {
    val md = MessageDigest.getInstance("SHA-256")
    parts.forEach { md.update(it) }
    return md.digest()
}
