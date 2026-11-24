// LAN の探索(§9.5): 「TABSPAN-DISCOVER 1 <deviceId>」を 255.255.255.255:<port> へ 500 ms おきに 3 回送り、最後から 1 秒待つ。
// 返事「TABSPAN-HERE 1 <pcInstanceId> <tcpPort>」は、覚えている pcInstanceId の物だけ受ける。
// 文字の組み立てと読み取りは DiscoveryProtocol(純粋な関数)、UDP を実際に使うのは Discovery.search だけ。
package io.github.takosasidev.tabspan.link

import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/** 返事の中身。 */
data class HereReply(val pcInstanceId: String, val tcpPort: Int)

/** 見つかった PC。[host] は返事の送り元。 */
data class Found(val host: InetAddress, val pcInstanceId: String, val tcpPort: Int)

object DiscoveryProtocol {
    const val MAX_DATAGRAM = 512

    /** 問い合わせのバイト列(ASCII、末尾の改行なし)。 */
    fun query(deviceId: String): ByteArray = "TABSPAN-DISCOVER 1 $deviceId".toByteArray(Charsets.US_ASCII)

    /**
     * 返事を読む。512 バイトを超える・ASCII でない・形が違う・版が 1 でない・ポートが 1〜65535 でない・
     * [known] に無い pcInstanceId の物は null。末尾の CR/LF は許す。
     */
    fun parseReply(data: ByteArray, length: Int, known: Set<String>): HereReply? {
        if (length < 0 || length > MAX_DATAGRAM || length > data.size) return null
        for (i in 0 until length) if (data[i] < 0x20 && data[i] != '\r'.code.toByte() && data[i] != '\n'.code.toByte() || data[i] >= 0x7F) return null
        val parts = String(data, 0, length, Charsets.US_ASCII).trimEnd('\r', '\n').split(' ')
        if (parts.size != 4 || parts[0] != "TABSPAN-HERE" || parts[1] != "1") return null
        val id = parts[2]
        val portText = parts[3]
        if (id.isEmpty() || id !in known) return null
        if (portText.isEmpty() || portText.length > 5 || !portText.all { it in '0'..'9' }) return null
        val port = portText.toInt()
        return if (port in 1..65535) HereReply(id, port) else null
    }
}

object Discovery {
    const val SEND_COUNT = 3
    const val SEND_INTERVAL_MS = 500L
    const val WAIT_AFTER_LAST_MS = 1_000L
    private val BROADCAST: InetAddress = InetAddress.getByAddress(byteArrayOf(-1, -1, -1, -1))

    /**
     * 1 回の探索。最初に受けた正しい返事を返す。見つからない・ネットワークが無い・[alive] が偽になったら null。
     * 時間は最長で (SEND_COUNT - 1) * SEND_INTERVAL_MS + WAIT_AFTER_LAST_MS。
     */
    fun search(port: Int, deviceId: String, known: Set<String>, alive: () -> Boolean): Found? {
        val query = DiscoveryProtocol.query(deviceId)
        val buffer = ByteArray(DiscoveryProtocol.MAX_DATAGRAM + 1) // 1 バイト多く受けて、512 を超えた物を見分ける
        val start = System.nanoTime()
        fun elapsedMs() = (System.nanoTime() - start) / 1_000_000
        val end = (SEND_COUNT - 1) * SEND_INTERVAL_MS + WAIT_AFTER_LAST_MS
        return try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                var sent = 0
                while (alive()) {
                    val now = elapsedMs()
                    if (sent < SEND_COUNT && now >= sent * SEND_INTERVAL_MS) {
                        socket.send(DatagramPacket(query, query.size, BROADCAST, port))
                        sent++
                        continue
                    }
                    val until = if (sent < SEND_COUNT) sent * SEND_INTERVAL_MS else end
                    if (sent >= SEND_COUNT && now >= end) return null
                    socket.soTimeout = maxOf(1L, until - now).toInt()
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(packet)
                    } catch (_: SocketTimeoutException) {
                        continue
                    }
                    DiscoveryProtocol.parseReply(packet.data, packet.length, known)?.let {
                        return Found(packet.address, it.pcInstanceId, it.tcpPort)
                    }
                }
                null
            }
        } catch (_: IOException) {
            null // Wi-Fi が無い(送れない)など
        }
    }
}
