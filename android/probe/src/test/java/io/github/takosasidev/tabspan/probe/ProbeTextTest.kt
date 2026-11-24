// ProbeText(Android に依らない計算)の単体テスト。JVM で走る。
package io.github.takosasidev.tabspan.probe

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ProbeTextTest {
    @Test
    fun avcProfileNames() {
        assertEquals("Baseline", ProbeText.avcProfileName(1))
        assertEquals("Main", ProbeText.avcProfileName(2))
        assertEquals("High", ProbeText.avcProfileName(8))
        assertEquals("ConstrainedBaseline", ProbeText.avcProfileName(65536))
        assertEquals("ConstrainedHigh", ProbeText.avcProfileName(524288))
        assertEquals("0x3", ProbeText.avcProfileName(3))
    }

    @Test
    fun avcLevelNames() {
        assertEquals("1b", ProbeText.avcLevelName(2))
        assertEquals("3.1", ProbeText.avcLevelName(512))
        assertEquals("4.1", ProbeText.avcLevelName(4096))
        assertEquals("5.1", ProbeText.avcLevelName(32768))
        assertEquals("6.2", ProbeText.avcLevelName(524288))
        assertEquals("0x100000", ProbeText.avcLevelName(0x100000))
    }

    @Test
    fun colorFormatNames() {
        assertEquals("YUV420SemiPlanar", ProbeText.colorFormatName(21))
        assertEquals("YUV420Flexible", ProbeText.colorFormatName(0x7F420888))
        assertEquals("Surface", ProbeText.colorFormatName(0x7F000789))
        assertEquals("0x7FA30C04", ProbeText.colorFormatName(0x7FA30C04))
    }

    @Test
    fun landscapeMakesWidthTheLongEdge() {
        assertEquals(Size(2560, 1600), ProbeText.landscape(1600, 2560))
        assertEquals(Size(2560, 1600), ProbeText.landscape(2560, 1600))
        assertEquals(Size(1000, 1000), ProbeText.landscape(1000, 1000))
    }

    @Test
    fun candidatesFollowSpec97() {
        assertEquals(12, ProbeText.CANDIDATES.size)
        assertEquals(Size(2560, 1600) to 60, ProbeText.CANDIDATES.first())
        assertEquals(Size(1280, 720) to 30, ProbeText.CANDIDATES.last())
        // 候補はどれも横向き
        assertTrue(ProbeText.CANDIDATE_SIZES.all { it.width >= it.height })
        assertEquals(Size(1600, 2560), Size(2560, 1600).swapped)
        assertEquals("2560x1600", Size(2560, 1600).toString())
    }

    @Test
    fun parsePortChecksRange() {
        assertEquals(47815, ProbeText.parsePort(" 47815 "))
        assertEquals(1, ProbeText.parsePort("1"))
        assertEquals(65535, ProbeText.parsePort("65535"))
        assertNull(ProbeText.parsePort("0"))
        assertNull(ProbeText.parsePort("65536"))
        assertNull(ProbeText.parsePort("abc"))
        assertNull(ProbeText.parsePort(""))
        assertNull(ProbeText.parsePort(null))
        assertFalse(ProbeText.isValidPort(-1))
    }

    @Test
    fun fingerprintMatchesPcFormat() {
        // GetCertHashString と同じ: 大文字の 16 進、区切りなし、負のバイトも 2 桁
        assertEquals("00FF7F80AB", ProbeText.fingerprint(byteArrayOf(0, -1, 127, -128, 0xAB.toByte())))
    }
}
