// PC への TLS 1.2 の接続 1 本。ペアリング済みなら、握手の中で証明書の DER の SHA-256 を覚えた指紋と比べ、
// 合わなければ何も送らずに閉じる(FingerprintMismatchException)。ペアリングの時だけ証明書を受け入れて DER を控える。
package io.github.takosasidev.tabspan.link

import android.annotation.SuppressLint
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** 相手の証明書が、覚えたどの指紋とも合わなかった。 */
class FingerprintMismatchException : java.io.IOException("server certificate does not match the paired fingerprint")

class LinkSocket private constructor(private val raw: Socket, private val tls: SSLSocket, val certDer: ByteArray) : Closeable {
    /** 相手の証明書の DER の SHA-256(16 進の大文字)。 */
    val fingerprint: String = Hex.of(sha256(certDer))
    private val input = BufferedInputStream(tls.inputStream, 64 * 1024)
    private val output = BufferedOutputStream(tls.outputStream, 64 * 1024)
    private val writeLock = Any()

    /** 次の枠。流れが終わったら null。読みの待ちが [connect] の readTimeoutMs を超えたら SocketTimeoutException。 */
    fun read(): Frame? = FrameCodec.read(input)

    /** どのスレッドから呼んでもよい(枠が混ざらないよう 1 つのロックを通す)。メインスレッドからは呼ばない。 */
    fun send(type: Int, payload: ByteArray = EMPTY) {
        synchronized(writeLock) { FrameCodec.write(output, type, payload) }
    }

    /** 読みで待っている別のスレッドも起こすため、下の TCP から閉じる。 */
    override fun close() {
        try {
            raw.close()
        } catch (_: Exception) {
        }
        try {
            tls.close()
        } catch (_: Exception) {
        }
    }

    companion object {
        const val PORT = 47815
        private val EMPTY = ByteArray(0)
        private const val CONNECT_TIMEOUT_MS = 3_000

        /**
         * 127.0.0.1:[port] へつなぐ。[trusted] が null ならペアリング用(証明書を受け入れる)、
         * そうでなければ指紋(16 進)の集まりで、どれとも合わなければ FingerprintMismatchException。
         */
        fun connect(port: Int, trusted: Set<String>?, readTimeoutMs: Int): LinkSocket {
            val trust = PinningTrustManager(trusted)
            val context = SSLContext.getInstance("TLSv1.2")
            context.init(null, arrayOf(trust), null)
            val raw = Socket()
            try {
                // 名前の解決を通さず、ループバックの番地をそのまま使う
                raw.connect(InetSocketAddress(InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1)), port), CONNECT_TIMEOUT_MS)
                raw.tcpNoDelay = true
                raw.soTimeout = readTimeoutMs
                val tls = context.socketFactory.createSocket(raw, "127.0.0.1", port, true) as SSLSocket
                tls.enabledProtocols = arrayOf("TLSv1.2")
                try {
                    tls.startHandshake() // 明示で呼ぶ(getSession だけだと失敗が例外にならない)
                } catch (e: Exception) {
                    if (trust.mismatch) throw FingerprintMismatchException()
                    throw e
                }
                val der = trust.certDer ?: throw java.io.IOException("no server certificate")
                return LinkSocket(raw, tls, der)
            } catch (e: Exception) {
                try {
                    raw.close()
                } catch (_: Exception) {
                }
                throw e
            }
        }
    }

    /** 相手の先頭の証明書の DER を控え、[trusted] があればその指紋と比べる。ホスト名は見ない(指紋で置き換える)。 */
    @SuppressLint("CustomX509TrustManager")
    private class PinningTrustManager(private val trusted: Set<String>?) : X509TrustManager {
        @Volatile var certDer: ByteArray? = null
        @Volatile var mismatch = false

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
            throw CertificateException("client certificates are not used")

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val der = chain?.firstOrNull()?.encoded ?: throw CertificateException("no certificate")
            certDer = der
            if (trusted != null && Hex.of(sha256(der)) !in trusted) {
                mismatch = true
                throw CertificateException("fingerprint mismatch")
            }
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
    }
}
