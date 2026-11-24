// link の純粋な部品(枠・メッセージ・Base64・ペアリングの番号)の単体テスト。
// docs/test-vectors の frames.json・pairing.json があれば読み、無ければそのテストは飛ばす(Assume)。
package io.github.takosasidev.tabspan.link

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.File
import java.net.ProtocolException
import java.util.Random

class LinkTest {
    // ---- FrameCodec ----

    @Test
    fun encodeWritesTypeAndBigEndianLength() {
        assertEquals("1100000003010203", Hex.of(FrameCodec.encode(0x11, byteArrayOf(1, 2, 3))))
        assertEquals("1200000000", Hex.of(FrameCodec.encode(0x12, ByteArray(0))))
    }

    @Test
    fun readsFramesBackToBackThenNullAtEnd() {
        val out = ByteArrayOutputStream()
        FrameCodec.write(out, 0x30, Messages.ping(42))
        FrameCodec.write(out, 0x12, ByteArray(0))
        val input = ByteArrayInputStream(out.toByteArray())
        val a = FrameCodec.read(input)!!
        assertEquals(0x30, a.type)
        assertEquals(42L, Messages.parsePing(a.payload))
        val b = FrameCodec.read(input)!!
        assertEquals(0x12, b.type)
        assertEquals(0, b.payload.size)
        assertNull(FrameCodec.read(input))
    }

    @Test(expected = EOFException::class)
    fun endInsideFrameIsEof() {
        FrameCodec.read(ByteArrayInputStream(Hex.parse("110000000501")))
    }

    @Test
    fun lengthOverLimitThrowsWithoutReadingPayload() {
        // ヘッダーだけ(中身は無い)。中身を読みに行けば EOFException になるので、FrameTooLarge が出れば読んでいない
        try {
            FrameCodec.read(ByteArrayInputStream(Hex.parse("1100400001")))
            fail()
        } catch (e: FrameTooLargeException) {
            assertEquals(FrameCodec.MAX_PAYLOAD + 1L, e.length)
        }
        try {
            FrameCodec.read(ByteArrayInputStream(Hex.parse("11FFFFFFFF")))
            fail()
        } catch (e: FrameTooLargeException) {
            assertEquals(0xFFFF_FFFFL, e.length)
        }
    }

    @Test(expected = EOFException::class)
    fun lengthExactlyAtLimitIsAccepted() {
        FrameCodec.read(ByteArrayInputStream(Hex.parse("1100400000"))) // 上限ちょうどは受ける(中身が無いので EOF)
    }

    @Test
    fun frameVectors() {
        val file = vector("frames.json")
        val cases = JSONArray(file.readText())
        assertTrue(cases.length() >= 8)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val name = c.getString("name")
            if (c.has("headerHex")) {
                val tooLarge = c.optBoolean("tooLarge")
                try {
                    FrameCodec.read(ByteArrayInputStream(Hex.parse(c.getString("headerHex"))))
                    fail(name)
                } catch (e: FrameTooLargeException) {
                    assertTrue(name, tooLarge)
                } catch (e: EOFException) {
                    assertFalse(name, tooLarge)
                }
                continue
            }
            val type = c.getInt("type")
            val payload = Hex.parse(c.getString("payloadHex"))
            val encoded = Hex.parse(c.getString("encodedHex"))
            assertArrayEquals(name, encoded, FrameCodec.encode(type, payload))
            val back = FrameCodec.read(ByteArrayInputStream(encoded))!!
            assertEquals(name, type, back.type)
            assertArrayEquals(name, payload, back.payload)
            checkPayload(name, type, payload)
        }
    }

    /** 中身を読めるか、端末が送る物は同じ値の JSON を作れるかを確かめる(JSON はバイトではなく値で比べる)。 */
    private fun checkPayload(name: String, type: Int, payload: ByteArray) {
        val text = String(payload, Charsets.UTF_8)
        when (type) {
            MessageType.HELLO -> {
                val o = JSONObject(text)
                val screen = o.getJSONObject("screen")
                val decoder = o.getJSONObject("decoder")
                val profiles = decoder.getJSONArray("profiles")
                val supported = decoder.getJSONArray("supported")
                val hello = HelloMsg(
                    o.getInt("protocolVersion"), o.getString("deviceId"), if (o.has("token")) o.getString("token") else null,
                    o.getString("deviceName"), o.getString("appVersion"),
                    ScreenInfo(screen.getInt("widthPx"), screen.getInt("heightPx"), screen.getInt("densityDpi")),
                    DecoderInfo(
                        List(profiles.length()) { profiles.getString(it) },
                        List(supported.length()) { supported.getJSONObject(it).let { s -> SizeFps(s.getInt("width"), s.getInt("height"), s.getInt("fps")) } },
                    ),
                )
                assertSameJson(name, o, Messages.toJson(hello))
            }
            MessageType.PAIR_REQUEST -> {
                val o = JSONObject(text)
                val m = PairRequestMsg(o.getInt("protocolVersion"), o.getString("deviceId"), o.getString("deviceName"), o.getString("clientNonce"))
                assertSameJson(name, o, Messages.toJson(m))
            }
            MessageType.STATS -> {
                val o = JSONObject(text)
                val frames = o.getJSONArray("frames")
                val m = StatsMsg(
                    o.getInt("decoded"), o.getInt("dropped"), o.getInt("decoderErrors"),
                    List(frames.length()) { frames.getJSONObject(it).let { f -> FrameTiming(f.getLong("seq"), f.getLong("arriveUs"), f.getLong("decodedUs")) } },
                )
                assertSameJson(name, o, Messages.toJson(m))
            }
            MessageType.HELLO_ACK -> Messages.parseHelloAck(payload).let { assertEquals(name, it.ok, it.reason == null) }
            MessageType.PAIR_CHALLENGE -> assertEquals(name, 16, Base64Codec.decode(Messages.parsePairChallenge(payload).serverNonce).size)
            MessageType.PAIR_ACCEPT -> assertEquals(name, 32, Base64Codec.decode(Messages.parsePairAccept(payload).token).size)
            MessageType.PAIR_REJECT, MessageType.BYE -> {
                val r = Messages.parseReason(payload)
                assertTrue(name, r.reason in Messages.REASONS)
                assertSameJson(name, JSONObject(text), Messages.toJson(r))
            }
            MessageType.STREAM_CONFIG -> Messages.parseStreamConfig(payload).let {
                assertEquals(name, 7, Base64Codec.decode(it.sps)[0].toInt() and 0x1F) // SPS の NAL(種類 7)
                Base64Codec.decode(it.pps)
            }
            MessageType.NOTICE -> Messages.parseNotice(payload)
            MessageType.VIDEO_FRAME -> {
                val v = Messages.parseVideoFrame(payload)
                assertEquals(name, payload.size - VideoFrameMsg.HEADER, v.dataLength)
            }
            MessageType.PING -> assertArrayEquals(name, payload, Messages.ping(Messages.parsePing(payload)))
            MessageType.PONG -> Messages.parsePong(payload).let { (t1, t2) -> assertArrayEquals(name, payload, Messages.pong(t1, t2)) }
        }
    }

    private fun assertSameJson(name: String, expected: JSONObject, actual: ByteArray) {
        val a = JSONObject(String(actual, Charsets.UTF_8))
        assertTrue("$name: $expected != $a", sameJson(expected, a))
    }

    private fun sameJson(a: Any?, b: Any?): Boolean = when {
        a is JSONObject && b is JSONObject ->
            keysOf(a) == keysOf(b) && keysOf(a).all { sameJson(a.get(it), b.get(it)) }
        a is JSONArray && b is JSONArray -> a.length() == b.length() && (0 until a.length()).all { sameJson(a.get(it), b.get(it)) }
        a is Number && b is Number -> a.toLong() == b.toLong() && a.toDouble() == b.toDouble()
        else -> a == b
    }

    private fun keysOf(o: JSONObject): Set<String> = o.keys().asSequence().map { it as String }.toSet()

    // ---- Messages ----

    @Test
    fun helloUsesSpecKeysAndOmitsNullToken() {
        val hello = HelloMsg(
            1, "dev", null, "d-01J", "0.1.0", ScreenInfo(2560, 1600, 400),
            DecoderInfo(listOf("baseline", "high"), listOf(SizeFps(1280, 800, 60))),
        )
        val o = JSONObject(String(Messages.toJson(hello), Charsets.UTF_8))
        assertEquals(setOf("protocolVersion", "deviceId", "deviceName", "appVersion", "screen", "decoder"), keysOf(o))
        assertEquals(2560, o.getJSONObject("screen").getInt("widthPx"))
        assertEquals(400, o.getJSONObject("screen").getInt("densityDpi"))
        val s = o.getJSONObject("decoder").getJSONArray("supported").getJSONObject(0)
        assertEquals(listOf(1280, 800, 60), listOf(s.getInt("width"), s.getInt("height"), s.getInt("fps")))
        assertEquals("high", o.getJSONObject("decoder").getJSONArray("profiles").getString(1))

        val withToken = JSONObject(String(Messages.toJson(hello.copy(token = "AAAA")), Charsets.UTF_8))
        assertEquals("AAAA", withToken.getString("token"))
    }

    @Test
    fun parsesHelloAckWithAndWithoutReason() {
        val ok = Messages.parseHelloAck("""{"ok":true,"pcInstanceId":"p","pcName":"PC","appVersion":"0.1.0","touchEnabled":false}""".toByteArray())
        assertTrue(ok.ok)
        assertNull(ok.reason)
        val ng = Messages.parseHelloAck("""{"ok":false,"reason":"busy","pcInstanceId":"p","pcName":"PC","appVersion":"0.1.0","touchEnabled":true}""".toByteArray())
        assertEquals("busy", ng.reason)
        assertTrue(ng.reason in Messages.REASONS)
    }

    @Test
    fun missingFieldOrBrokenJsonIsProtocolException() {
        for (text in listOf("""{"ok":true}""", "not json", """{"token":1}""".substring(0, 5))) {
            try {
                Messages.parseHelloAck(text.toByteArray())
                fail(text)
            } catch (_: ProtocolException) {
            }
        }
    }

    @Test
    fun parsesStreamConfigAndNotice() {
        val c = Messages.parseStreamConfig(
            """{"width":1280,"height":800,"fps":60,"profile":"high","sps":"Z2QAKA==","pps":"aO48gA==","sourceKind":"physical"}""".toByteArray(),
        )
        assertEquals(1280, c.width)
        assertArrayEquals(Hex.parse("67640028"), Base64Codec.decode(c.sps))
        val n = Messages.parseNotice("""{"kind":"paused","value":true}""".toByteArray())
        assertEquals("paused", n.kind)
        assertEquals(true, n.value)
    }

    @Test
    fun statsJson() {
        val o = JSONObject(String(Messages.toJson(StatsMsg(60, 1, 0, listOf(FrameTiming(4_000_000_000L, 10, 20)))), Charsets.UTF_8))
        assertEquals(60, o.getInt("decoded"))
        assertEquals(1, o.getInt("dropped"))
        assertEquals(0, o.getInt("decoderErrors"))
        val f = o.getJSONArray("frames").getJSONObject(0)
        assertEquals(4_000_000_000L, f.getLong("seq"))
        assertEquals(20L, f.getLong("decodedUs"))
    }

    @Test
    fun videoFrameAndPingPong() {
        val p = Hex.parse("FFFFFFFE" + "01" + "0000000000000064" + "00000000000000C8" + "00000001AABB")
        val v = Messages.parseVideoFrame(p)
        assertEquals(0xFFFF_FFFEL, v.seq)
        assertTrue(v.key)
        assertEquals(100L, v.captureUs)
        assertEquals(200L, v.sendUs)
        assertEquals("00000001AABB", Hex.of(p.copyOfRange(v.dataOffset, v.dataOffset + v.dataLength)))
        assertFalse(Messages.parseVideoFrame(Hex.parse("00000000" + "00" + "0".repeat(32))).key)
        try {
            Messages.parseVideoFrame(ByteArray(20))
            fail()
        } catch (_: ProtocolException) {
        }
        assertEquals(-5L, Messages.parsePing(Messages.ping(-5)))
        assertEquals(3L to 4L, Messages.parsePong(Messages.pong(3, 4)))
        assertEquals("0000000000000003" + "0000000000000004", Hex.of(Messages.pong(3, 4)))
    }

    @Test
    fun reasonsMatchSpec() {
        assertEquals(13, Messages.REASONS.size)
        assertEquals("user", Messages.REASONS.last())
        assertEquals("shutdown", Messages.parseReason(Messages.toJson(ReasonMsg("shutdown"))).reason)
    }

    // ---- Base64 ----

    @Test
    fun base64MatchesJdk() {
        val r = Random(1)
        for (n in 0..40) {
            val bytes = ByteArray(n).also(r::nextBytes)
            val text = java.util.Base64.getEncoder().encodeToString(bytes)
            assertEquals(text, Base64Codec.encode(bytes))
            assertArrayEquals(bytes, Base64Codec.decode(text))
        }
        for (bad in listOf("A", "AB=C", "A*==", "ああああ")) {
            try {
                Base64Codec.decode(bad)
                fail(bad)
            } catch (_: ProtocolException) {
            }
        }
    }

    // ---- PairingCode ----

    @Test
    fun pairingCodeKnownValues() {
        // 期待値は Python の hashlib で別に計算した物
        val cert = ByteArray(64) { it.toByte() }
        val c1 = ByteArray(16) { it.toByte() }
        val s1 = ByteArray(16) { (it + 16).toByte() }
        assertEquals("456957", PairingCode.compute(cert, "11111111-2222-3333-4444-555555555555", c1, s1))
        val cert2 = Hex.parse("30820100") + ByteArray(200) { 0xAB.toByte() }
        assertEquals("567297", PairingCode.compute(cert2, "dev-あ", ByteArray(16) { -1 }, ByteArray(16)))
    }

    @Test
    fun pairingVectors() {
        val cases = JSONArray(vector("pairing.json").readText())
        assertTrue(cases.length() >= 3)
        for (i in 0 until cases.length()) {
            val c = cases.getJSONObject(i)
            val code = PairingCode.compute(
                Hex.parse(c.getString("certDerHex")), c.getString("deviceId"),
                Hex.parse(c.getString("clientNonceHex")), Hex.parse(c.getString("serverNonceHex")),
            )
            assertEquals(c.toString(), c.getString("code"), code)
        }
    }

    private fun vector(name: String): File {
        val file = File("../../docs/test-vectors/$name") // テストの作業フォルダは android/link
        assumeTrue("$name はまだ無い", file.isFile)
        return file
    }
}
