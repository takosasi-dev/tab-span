// つなぎ方の順(FR-6): 127.0.0.1(USB) → 前回つながったアドレス → 手で入れたアドレス → 探索。PC ごとに覚えた値から決める。
// 純粋な関数(ネットワークに触れない)。IPv4 の形の確かめと、名前の解決を通さないバイト列への変換もここに置く。
package io.github.takosasidev.tabspan.link

enum class Route { USB, LAST, MANUAL, DISCOVER }

/**
 * 試す 1 つ。[host] は IPv4 の文字列(DISCOVER は null)。[port] は TCP のポート(DISCOVER は UDP の問い合わせ先)。
 * [pcInstanceId] は LAST・MANUAL の持ち主の PC(指紋はその PC の物だけで照合する)。
 */
data class Attempt(val route: Route, val host: String?, val port: Int, val pcInstanceId: String? = null)

object ConnectOrder {
    const val LOOPBACK = "127.0.0.1"

    /** [lan] が偽(Wi-Fi などが無い)なら USB だけ。同じ「アドレスとポート」は 1 度だけ試す。 */
    fun plan(pcs: List<PcEntry>, lan: Boolean): List<Attempt> {
        val out = ArrayList<Attempt>()
        val seen = HashSet<String>()
        fun add(a: Attempt) {
            if (seen.add("${a.route == Route.DISCOVER}|${a.host}:${a.port}")) out += a
        }
        pcs.forEach { add(Attempt(Route.USB, LOOPBACK, it.port)) }
        if (!lan) return out
        pcs.forEach { pc -> pc.lastHost?.takeIf { Ipv4.parse(it) != null && it != LOOPBACK }?.let { add(Attempt(Route.LAST, it, pc.port, pc.pcInstanceId)) } }
        pcs.forEach { pc -> pc.manualHost?.takeIf { Ipv4.parse(it) != null && it != LOOPBACK }?.let { add(Attempt(Route.MANUAL, it, pc.port, pc.pcInstanceId)) } }
        pcs.forEach { add(Attempt(Route.DISCOVER, null, it.port)) }
        return out
    }
}

object Ipv4 {
    /** 「192.168.1.10」の形(前後の空白は許す、各桁 0〜255、先頭の 0 は不可)なら 4 バイト、違えば null。 */
    fun parse(text: String): ByteArray? {
        val parts = text.trim().split('.')
        if (parts.size != 4) return null
        val out = ByteArray(4)
        for ((i, p) in parts.withIndex()) {
            if (p.isEmpty() || p.length > 3 || !p.all { it in '0'..'9' } || (p.length > 1 && p[0] == '0')) return null
            val v = p.toInt()
            if (v > 255) return null
            out[i] = v.toByte()
        }
        return out
    }

    /** 形を整えた文字列(空白を除いた物)。形が違えば null。 */
    fun normalize(text: String): String? = if (parse(text) != null) text.trim() else null
}
