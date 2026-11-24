// DecodeText(復号のテストの Android に依らない部分)の単体テスト。JVM で走る。
package io.github.takosasidev.tabspan.probe

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DecodeTextTest {
    private fun bytes(vararg v: Int) = ByteArray(v.size) { v[it].toByte() }

    // AUD(4 バイトの開始コード) SPS PPS(3 バイト) IDR | AUD(3 バイト) スライス(4 バイト)
    private val stream = bytes(
        0, 0, 0, 1, 0x09, 0x10,
        0, 0, 0, 1, 0x67, 0x4D, 0x40,
        0, 0, 1, 0x68, 0xEE,
        0, 0, 0, 1, 0x65, 0x88, 0x84,
        0, 0, 1, 0x09, 0x30,
        0, 0, 0, 1, 0x41, 0x9A,
    )

    @Test
    fun parsesFileNames() {
        assertEquals(Clip(Size(1280, 800), 30), DecodeText.parseFileName("tabspan_1280x800_30.h264"))
        assertEquals(Clip(Size(2560, 1600), 60), DecodeText.parseFileName("tabspan_2560x1600_60.h264"))
        assertNull(DecodeText.parseFileName("tabspan_1280x800_30.mp4"))
        assertNull(DecodeText.parseFileName("x_tabspan_1280x800_30.h264"))
        assertNull(DecodeText.parseFileName("tabspan_1280x800_0.h264"))
        assertNull(DecodeText.parseFileName("tabspan_99999999999x800_30.h264"))
        assertEquals("tabspan_1280x800_30.h264", DecodeText.fileName(Clip(Size(1280, 800), 30)))
    }

    @Test
    fun findsNalsWithThreeAndFourByteStartCodes() {
        val nals = DecodeText.nalUnits(stream)
        assertEquals(listOf(9, 7, 8, 5, 9, 1), nals.map { it.type })
        assertEquals(0, nals[0].start)
        assertEquals(13, nals[2].start) // 3 バイトの開始コード
        assertEquals(18, nals[3].start) // 4 バイトの開始コード(前の 0 を含める)
        assertEquals(stream.size, nals.last().end)
    }

    @Test
    fun splitsBeforeEachAud() {
        val units = DecodeText.splitAccessUnits(stream)
        assertEquals(2, units.size)
        assertEquals(listOf(9, 7, 8, 5), DecodeText.nalTypes(units[0]))
        assertEquals(listOf(9, 1), DecodeText.nalTypes(units[1]))
        assertArrayEquals(stream, units[0] + units[1]) // 1 バイトも落とさない
    }

    @Test
    fun bytesBeforeTheFirstAudGoToTheFirstUnit() {
        val withPrefix = bytes(0, 0, 1, 0x06, 0x05) + stream
        val units = DecodeText.splitAccessUnits(withPrefix)
        assertEquals(2, units.size)
        assertEquals(listOf(6, 9, 7, 8, 5), DecodeText.nalTypes(units[0]))
    }

    @Test
    fun noAudMeansNoUnits() {
        assertTrue(DecodeText.splitAccessUnits(bytes(0, 0, 0, 1, 0x65, 0x88)).isEmpty())
        assertTrue(DecodeText.splitAccessUnits(ByteArray(0)).isEmpty())
    }

    @Test
    fun extractsSpsAndPpsWithStartCode() {
        val first = DecodeText.splitAccessUnits(stream)[0]
        assertArrayEquals(bytes(0, 0, 0, 1, 0x67, 0x4D, 0x40), DecodeText.firstNal(first, DecodeText.SPS))
        assertArrayEquals(bytes(0, 0, 0, 1, 0x68, 0xEE), DecodeText.firstNal(first, DecodeText.PPS))
        assertNull(DecodeText.firstNal(first, 6))
    }

    @Test
    fun ptsRoundTripsToIndex() {
        for (fps in listOf(30, 60)) {
            for (k in 0 until 300) assertEquals(k, DecodeText.indexOf(DecodeText.ptsOf(k, fps), fps, 300))
        }
        assertNull(DecodeText.indexOf(DecodeText.ptsOf(300, 30), 30, 300))
        assertNull(DecodeText.indexOf(-1_000_000, 30, 300))
    }

    @Test
    fun medianOfOddEvenAndEmpty() {
        assertEquals(2.0, DecodeText.median(listOf(3.0, 1.0, 2.0))!!, 0.0)
        assertEquals(2.5, DecodeText.median(listOf(4.0, 1.0, 3.0, 2.0))!!, 0.0)
        assertNull(DecodeText.median(emptyList()))
    }

    private val r4 = DecodeText.R4_CLIP
    private fun cell(decoder: String, frames: Int, fps: Double?, failure: String? = null, clip: Clip = r4, surface: Boolean = true) =
        DecodeCell(clip, decoder, surface, frames, 150, fps, 12.0, failure)

    @Test
    fun r4SaysCanWhenOneDecoderKeepsUp() {
        val line = DecodeText.r4Line(listOf(cell("hw", 150, 64.9), cell("sw", 150, 20.0)))
        assertTrue(line, line.contains("できた(hw 64.9 fps Surface)"))
    }

    @Test
    fun r4SaysCannotWhenTooSlowOrFramesMissing() {
        assertTrue(DecodeText.r4Line(listOf(cell("hw", 150, 25.0))).contains("できない("))
        assertTrue(DecodeText.r4Line(listOf(cell("hw", 140, 60.0))).contains("できない("))
        // 途中まで出て時間切れも「できない」
        assertTrue(DecodeText.r4Line(listOf(cell("hw", 40, 2.0, "時間切れ 40/150枚"))).contains("できない("))
    }

    @Test
    fun r4IsUndecidedWithoutFileOrMeasurement() {
        assertTrue(DecodeText.r4Line(emptyList()).contains("判定できない(tabspan_1280x800_30.h264 が無い)"))
        assertTrue(DecodeText.r4Line(listOf(cell("hw", 150, 60.0, clip = Clip(Size(1280, 720), 30)))).contains("判定できない"))
        assertTrue(DecodeText.r4Line(listOf(cell("hw", 0, null, "CodecException 0/150枚"))).contains("判定できない(全部"))
    }

    @Test
    fun tableHasOneRowPerClipAndOneColumnPerDecoder() {
        val other = Clip(Size(1920, 1080), 60)
        val rows = DecodeText.table(
            listOf(other, r4), listOf("hw", "sw"),
            listOf(cell("hw", 150, 64.94), cell("sw", 150, 20.0, surface = false), cell("hw", 0, null, "時間切れ 0/150枚", clip = other)),
        )
        assertEquals(3, rows.size)
        assertTrue(rows[0], rows[0].startsWith("大きさ@fps"))
        assertTrue(rows[1], rows[1].contains("時間切れ 0/150枚") && rows[1].endsWith("-"))
        assertTrue(rows[2], rows[2].contains("64.9fps/12.0ms") && rows[2].contains("20.0fps/12.0ms BB"))
    }
}
