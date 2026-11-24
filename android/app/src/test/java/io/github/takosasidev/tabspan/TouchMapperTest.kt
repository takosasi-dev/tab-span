// TouchMapper(FR-21)の単体テスト。矩形は dtab の 2560x1600 に 16:9 を収めた (0, 80, 2560, 1440)。
package io.github.takosasidev.tabspan

import io.github.takosasidev.tabspan.TouchMapper.Companion.CANCEL
import io.github.takosasidev.tabspan.TouchMapper.Companion.DOWN
import io.github.takosasidev.tabspan.TouchMapper.Companion.MOVE
import io.github.takosasidev.tabspan.TouchMapper.Companion.UP
import io.github.takosasidev.tabspan.TouchMapper.Contact
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TouchMapperTest {
    private val rect = FitRect(0, 80, 2560, 1440)

    @Test
    fun cornersMapToEnds() {
        val m = TouchMapper()
        assertEquals(Contact(0, DOWN, 0, 0), m.down(10, 0f, 80f, rect))
        assertEquals(Contact(1, DOWN, 65535, 65535), m.down(11, 2559f, 1519f, rect))
        assertEquals(Contact(1, MOVE, 32768, 32768), m.move(11, 1279.5f, 799.5f, rect))
    }

    @Test
    fun startOutsideIsNotSent() {
        val m = TouchMapper()
        assertNull(m.down(1, 100f, 79f, rect)) // 上の余白
        assertNull(m.down(2, 100f, 1520f, rect)) // 下の余白
        assertNull(m.move(1, 100f, 500f, rect))
        assertNull(m.up(1, 100f, 500f, rect))
        assertTrue(m.isEmpty)
    }

    @Test
    fun movedOutsideIsClamped() {
        val m = TouchMapper()
        m.down(1, 100f, 100f, rect)
        assertEquals(Contact(0, MOVE, 0, 65535), m.move(1, -50f, 1600f, rect))
        assertNull(m.move(1, -80f, 1700f, rect)) // 端に寄せた値が同じなら送らない
        assertEquals(Contact(0, UP, 0, 65535), m.up(1, -80f, 1700f, rect))
    }

    @Test
    fun eleventhIgnored() {
        val m = TouchMapper()
        for (p in 0 until 10) assertEquals(p, m.down(100 + p, 10f, 100f, rect)!!.id)
        assertNull(m.down(200, 10f, 100f, rect))
        assertNull(m.move(200, 20f, 100f, rect))
        assertNull(m.up(200, 20f, 100f, rect))
        assertEquals(10, m.cancelAll().size)
    }

    @Test
    fun idsReuseSmallestFree() {
        val m = TouchMapper()
        m.down(5, 10f, 100f, rect)
        m.down(6, 10f, 100f, rect)
        m.down(7, 10f, 100f, rect)
        assertEquals(Contact(1, UP, 0, 0), m.up(6, 0f, 80f, rect))
        assertNull(m.move(6, 10f, 100f, rect)) // 離した指は忘れる
        assertEquals(1, m.down(8, 10f, 100f, rect)!!.id)
        assertEquals(3, m.down(9, 10f, 100f, rect)!!.id)
    }

    @Test
    fun unchangedMoveIsNull() {
        val m = TouchMapper()
        m.down(1, 100f, 100f, rect)
        assertNull(m.move(1, 100.01f, 100f, rect))
    }

    @Test
    fun cancelAllReturnsActiveAndForgets() {
        val m = TouchMapper()
        m.down(3, 0f, 80f, rect)
        m.down(4, 2559f, 1519f, rect)
        m.up(3, 0f, 80f, rect)
        assertEquals(listOf(Contact(1, CANCEL, 65535, 65535)), m.cancelAll())
        assertTrue(m.isEmpty)
        assertEquals(emptyList<Contact>(), m.cancelAll())
        assertEquals(0, m.down(4, 0f, 80f, rect)!!.id)
    }

    @Test
    fun tinyRectDoesNotDivideByZero() {
        val m = TouchMapper()
        assertEquals(Contact(0, DOWN, 0, 0), m.down(1, 5.5f, 7.2f, FitRect(5, 7, 1, 1)))
        assertNull(m.down(2, 0f, 0f, FitRect(0, 0, 0, 0)))
    }

    @Test
    fun encodeMatchesVector() {
        // docs/test-vectors/frames.json の touch_two_contacts の payloadHex
        val bytes = TouchMapper.encode(listOf(Contact(0, DOWN, 0, 65535), Contact(1, MOVE, 0x8000, 0x1234)))
        assertArrayEquals(hex("0200000000ffff010180001234"), bytes)
        assertArrayEquals(hex("0103000000ffff"), TouchMapper.encode(listOf(Contact(3, DOWN, 0, 65535))))
    }

    private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}
