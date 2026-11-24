// LAN の部品の単体テスト: 探索の文字(§9.5)、つなぎ方の順(FR-6)、IPv4 の形、PC の行の保存の形、link の依存(INV-14)。
// UDP を実際に送らない(純粋な関数だけ)。
package io.github.takosasidev.tabspan.link

import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test
import java.io.File

class LanTest {
    private val known = setOf("pc-a")

    private fun reply(text: String) = text.toByteArray(Charsets.US_ASCII).let { DiscoveryProtocol.parseReply(it, it.size, known) }

    // ---- 探索 ----

    @Test
    fun queryIsOneAsciiLine() {
        assertEquals("TABSPAN-DISCOVER 1 0f8c-uuid", String(DiscoveryProtocol.query("0f8c-uuid"), Charsets.US_ASCII))
    }

    @Test
    fun replyFromKnownPcIsAccepted() {
        assertEquals(HereReply("pc-a", 47815), reply("TABSPAN-HERE 1 pc-a 47815"))
        assertEquals(HereReply("pc-a", 1), reply("TABSPAN-HERE 1 pc-a 1\r\n"))
        assertEquals(HereReply("pc-a", 65535), reply("TABSPAN-HERE 1 pc-a 65535\n"))
    }

    @Test
    fun replyFromUnknownPcIsDropped() {
        assertNull(reply("TABSPAN-HERE 1 pc-b 47815"))
    }

    @Test
    fun malformedRepliesAreDropped() {
        listOf(
            "", "TABSPAN-HERE", "TABSPAN-HERE 1 pc-a", "TABSPAN-HERE 2 pc-a 47815", "TABSPAN-DISCOVER 1 pc-a",
            "TABSPAN-HERE 1 pc-a 0", "TABSPAN-HERE 1 pc-a 65536", "TABSPAN-HERE 1 pc-a -1", "TABSPAN-HERE 1 pc-a 4781x",
            "TABSPAN-HERE 1 pc-a 47815 extra", "TABSPAN-HERE  1 pc-a 47815", "tabspan-here 1 pc-a 47815",
            "TABSPAN-HERE 1 pc-a 0000047815", "TABSPAN-HERE 1 pc-a\t47815",
        ).forEach { assertNull(it, reply(it)) }
        val nonAscii = "TABSPAN-HERE 1 pc-a 47815".toByteArray(Charsets.US_ASCII).also { it[15] = 0xE3.toByte() } // pc-a の p
        assertNull(DiscoveryProtocol.parseReply(nonAscii, nonAscii.size, known + "�c-a"))
    }

    @Test
    fun datagramOver512BytesIsDropped() {
        val ok = "TABSPAN-HERE 1 pc-a 47815".toByteArray(Charsets.US_ASCII)
        val big = ok.copyOf(DiscoveryProtocol.MAX_DATAGRAM + 1)
        assertNull(DiscoveryProtocol.parseReply(big, big.size, known))
        // 受けた長さだけを読む(バッファの残りは見ない)
        assertEquals(HereReply("pc-a", 47815), DiscoveryProtocol.parseReply(big, ok.size, known))
    }

    @Test
    fun searchTimingIsThreeSendsThenOneSecond() {
        assertEquals(3, Discovery.SEND_COUNT)
        assertEquals(500L, Discovery.SEND_INTERVAL_MS)
        assertEquals(1_000L, Discovery.WAIT_AFTER_LAST_MS)
    }

    // ---- つなぎ方の順 ----

    private fun pc(id: String, port: Int = 47815, last: String? = null, manual: String? = null) =
        PcEntry(id, id, "FP", "TOKEN", port = port, lastHost = last, manualHost = manual)

    @Test
    fun orderIsUsbThenLastThenManualThenDiscover() {
        val plan = ConnectOrder.plan(listOf(pc("a", last = "192.168.1.20", manual = "192.168.1.30")), lan = true)
        assertEquals(
            listOf(
                Attempt(Route.USB, "127.0.0.1", 47815),
                Attempt(Route.LAST, "192.168.1.20", 47815, "a"),
                Attempt(Route.MANUAL, "192.168.1.30", 47815, "a"),
                Attempt(Route.DISCOVER, null, 47815),
            ),
            plan,
        )
    }

    @Test
    fun allPcsLastAddressesComeBeforeManualOnes() {
        val plan = ConnectOrder.plan(listOf(pc("a", manual = "10.0.0.1"), pc("b", last = "10.0.0.2")), lan = true)
        assertEquals(listOf(Route.USB, Route.LAST, Route.MANUAL, Route.DISCOVER), plan.map { it.route })
        assertEquals("10.0.0.2", plan[1].host)
        assertEquals("b", plan[1].pcInstanceId)
    }

    @Test
    fun withoutLanOnlyUsb() {
        assertEquals(listOf(Attempt(Route.USB, "127.0.0.1", 47815)), ConnectOrder.plan(listOf(pc("a", last = "10.0.0.2")), lan = false))
    }

    @Test
    fun duplicatesAndBadAddressesAreSkipped() {
        val plan = ConnectOrder.plan(
            listOf(pc("a", last = "10.0.0.2", manual = "10.0.0.2"), pc("b", last = "127.0.0.1", manual = "nope")),
            lan = true,
        )
        assertEquals(
            listOf(Attempt(Route.USB, "127.0.0.1", 47815), Attempt(Route.LAST, "10.0.0.2", 47815, "a"), Attempt(Route.DISCOVER, null, 47815)),
            plan,
        )
    }

    @Test
    fun eachRememberedPortIsTried() {
        val plan = ConnectOrder.plan(listOf(pc("a"), pc("b", port = 50000)), lan = true)
        assertEquals(
            listOf(
                Attempt(Route.USB, "127.0.0.1", 47815), Attempt(Route.USB, "127.0.0.1", 50000),
                Attempt(Route.DISCOVER, null, 47815), Attempt(Route.DISCOVER, null, 50000),
            ),
            plan,
        )
    }

    @Test
    fun noPcsNoAttempts() {
        assertEquals(emptyList<Attempt>(), ConnectOrder.plan(emptyList(), lan = true))
    }

    // ---- IPv4 ----

    @Test
    fun ipv4Accepted() {
        assertArrayEquals(byteArrayOf(192.toByte(), 168.toByte(), 1, 10), Ipv4.parse("192.168.1.10"))
        assertArrayEquals(byteArrayOf(0, 0, 0, 0), Ipv4.parse("0.0.0.0"))
        assertArrayEquals(byteArrayOf(-1, -1, -1, -1), Ipv4.parse("255.255.255.255"))
        assertEquals("10.0.0.5", Ipv4.normalize("  10.0.0.5 "))
    }

    @Test
    fun ipv4Rejected() {
        listOf("", "192.168.1", "192.168.1.10.5", "256.1.1.1", "1.2.3.-4", "a.b.c.d", "01.2.3.4", "1..2.3", "1.2.3.4:80", "pc.local", "1 .2.3.4", "１.2.3.4")
            .forEach { assertNull(it, Ipv4.parse(it)) }
    }

    // ---- 保存の形 ----

    @Test
    fun entryRoundTrips() {
        val e = PcEntry("id", "name", "FP", "TOK", pairedAt = 1_700_000_000_000, port = 50000, lastHost = "10.0.0.2", manualHost = null)
        assertEquals(e, PcEntry.fromJson(JSONObject(e.toJson().toString())))
        assertFalse(e.toJson().has("manualHost"))
    }

    @Test
    fun v020EntryStillReads() {
        val old = JSONObject("""{"pcInstanceId":"id","pcName":"name","fingerprint":"FP","token":"TOK"}""")
        assertEquals(PcEntry("id", "name", "FP", "TOK", 0, LinkSocket.PORT, null, null), PcEntry.fromJson(old))
    }

    // ---- 依存(INV-14) ----

    @Test
    fun AC40_linkDependsOnNeitherVideoNorApp() {
        val gradle = File("build.gradle.kts").readText() // 単体テストは link のフォルダで動く
        assertFalse(gradle.contains("project(\":video\")"))
        assertFalse(gradle.contains("project(\":app\")"))
    }
}
