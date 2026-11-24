// ペアリングの 6 桁: SHA-256(証明書の DER ‖ deviceId の UTF-8 ‖ clientNonce ‖ serverNonce) の先頭 4 バイトを
// ビッグエンディアンの符号なし整数で読み、1,000,000 で割った余りを先頭 0 埋めの 6 桁にする。PC 側と同じ計算。
package io.github.takosasidev.tabspan.link

object PairingCode {
    fun compute(certDer: ByteArray, deviceId: String, clientNonce: ByteArray, serverNonce: ByteArray): String {
        val h = sha256(certDer, deviceId.toByteArray(Charsets.UTF_8), clientNonce, serverNonce)
        val n = ((h[0].toLong() and 0xFF) shl 24) or ((h[1].toLong() and 0xFF) shl 16) or
            ((h[2].toLong() and 0xFF) shl 8) or (h[3].toLong() and 0xFF)
        return (n % 1_000_000).toString().padStart(6, '0')
    }
}
