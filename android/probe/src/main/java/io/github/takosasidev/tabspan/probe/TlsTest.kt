// 127.0.0.1 の指定のポートへ TLS 1.2 でつなぎ、握手の結果(版・暗号スイート・相手の証明書・時間)を出す。
// 相手は PC の TabSpan.Probe の `tls <port>`(握手の後は何も送らず読まずに閉じる)なので、こちらも握手だけして閉じる。
// 証明書は検査しない(probe 専用。相手を確かめるのは、PC の出す SHA-256 の指紋を人が見比べること)。
package io.github.takosasidev.tabspan.probe

import android.annotation.SuppressLint
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.security.interfaces.RSAPublicKey
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

object TlsTest {
    private const val HOST = "127.0.0.1"
    private const val TIMEOUT_MS = 5_000

    fun run(port: Int, emit: (String) -> Unit) {
        emit("--- TLS のテスト ($HOST:$port) ---")
        try {
            // 検査しない TrustManager は、この 1 本のソケット用の SSLContext の中だけに閉じる
            val context = SSLContext.getInstance("TLSv1.2")
            context.init(null, arrayOf(TrustAllForProbe), null)
            Socket().use { raw ->
                // 名前の解決を通さず、ループバックの番地をそのまま使う
                val address = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))
                val connectStart = System.nanoTime()
                raw.connect(InetSocketAddress(address, port), TIMEOUT_MS)
                val connectMs = (System.nanoTime() - connectStart) / 1_000_000
                raw.soTimeout = TIMEOUT_MS // 握手の途中で相手が黙ったときに戻るため
                (context.socketFactory.createSocket(raw, HOST, port, true) as SSLSocket).use { tls ->
                    tls.enabledProtocols = arrayOf("TLSv1.2")
                    val start = System.nanoTime()
                    tls.startHandshake() // 明示で呼ぶ(getSession だけだと失敗が例外にならない)
                    val handshakeMs = (System.nanoTime() - start) / 1_000_000
                    val session = tls.session
                    val cert = session.peerCertificates.firstOrNull() as? X509Certificate
                    emit("TLS の結果: 成功")
                    emit("TCP の接続: $connectMs ms")
                    emit("握手: $handshakeMs ms")
                    emit("protocol: ${session.protocol}")
                    emit("cipher suite: ${session.cipherSuite}")
                    if (cert == null) {
                        emit("相手の証明書: 無し")
                    } else {
                        emit("相手の証明書の鍵: ${keyDescription(cert)}")
                        emit("相手の証明書の署名: ${cert.sigAlgName}")
                        emit("相手の証明書の主体: ${cert.subjectX500Principal.name}")
                        val digest = MessageDigest.getInstance("SHA-256").digest(cert.encoded)
                        emit("相手の証明書の SHA-256 の指紋: ${ProbeText.fingerprint(digest)}")
                    }
                }
            }
        } catch (e: Exception) {
            emit("TLS の結果: 失敗")
            emit("例外の種類: ${e.javaClass.name}")
            emit("例外のメッセージ: ${e.message}")
            e.cause?.let { emit("原因: ${it.javaClass.name}: ${it.message}") }
        }
    }

    private fun keyDescription(cert: X509Certificate): String {
        val key = cert.publicKey
        return when (key) {
            is ECPublicKey -> "${key.algorithm} ${key.params.curve.field.fieldSize} bit"
            is RSAPublicKey -> "${key.algorithm} ${key.modulus.bitLength()} bit"
            else -> key.algorithm
        }
    }

    // probe 専用。接続先は 127.0.0.1 に固定し(HOST)、他のホストへは使わない。本番の link はペアリングで固定した証明書を見る(§9.4)
    @SuppressLint("TrustAllX509TrustManager", "CustomX509TrustManager")
    private object TrustAllForProbe : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
        override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
    }
}
