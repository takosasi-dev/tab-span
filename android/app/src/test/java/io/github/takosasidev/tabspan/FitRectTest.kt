// FitRect(FR-14)の単体テスト。dtab の画面 2560x1600 を主に使う。
package io.github.takosasidev.tabspan

import org.junit.Assert.assertEquals
import org.junit.Test

class FitRectTest {
    @Test
    fun sameAspectFillsScreen() {
        assertEquals(FitRect(0, 0, 2560, 1600), FitRect.fit(2560, 1600, 1280, 800))
        assertEquals(FitRect(0, 0, 2560, 1600), FitRect.fit(2560, 1600, 1920, 1200))
    }

    @Test
    fun widerVideoGetsBarsTopAndBottom() {
        assertEquals(FitRect(0, 80, 2560, 1440), FitRect.fit(2560, 1600, 1920, 1080))
    }

    @Test
    fun narrowerVideoGetsBarsLeftAndRight() {
        // 4:3 → 1600 * 4 / 3 = 2133、余白は (2560 - 2133) / 2 = 213
        assertEquals(FitRect(213, 0, 2133, 1600), FitRect.fit(2560, 1600, 1024, 768))
    }

    @Test
    fun neverExceedsView() {
        for ((vw, vh) in listOf(1919 to 1081, 1 to 1000, 1000 to 1, 2559 to 1599, 3840 to 2160)) {
            val r = FitRect.fit(2560, 1600, vw, vh)
            assert(r.x >= 0 && r.y >= 0 && r.x + r.width <= 2560 && r.y + r.height <= 1600) { "$vw x $vh -> $r" }
            assert(r.width == 2560 || r.height == 1600) { "$vw x $vh -> $r" }
        }
    }

    @Test
    fun unknownSizes() {
        assertEquals(FitRect(0, 0, 2560, 1600), FitRect.fit(2560, 1600, 0, 0))
        assertEquals(FitRect(0, 0, 0, 0), FitRect.fit(0, 0, 1280, 800))
    }
}
