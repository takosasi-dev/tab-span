// FileLog(FR-28)の単体テスト。偽の時計(UTC)と一時フォルダで、5 MB の切り替えと 14 日の消し方を見る。
// あわせて manifest の権限が 2 つだけか(INV-13)を見る。
package io.github.takosasidev.tabspan

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.RandomAccessFile
import java.util.TimeZone

class FileLogTest {
    @get:Rule val tmp = TemporaryFolder()

    private val utc = TimeZone.getTimeZone("UTC")
    private val day = 24L * 60 * 60 * 1000
    /** 2026-11-24 12:00 UTC */
    private val noon = 1_795_521_600_000L

    @Test
    fun writesDatedFileWithLevelAndMessage() {
        val log = FileLog(tmp.root, { noon }, utc)
        log.write('I', "session started")
        val f = File(tmp.root, "tabspan-20261124.log")
        assertEquals("2026-11-24 12:00:00.000 I session started\n", f.readText())
    }

    @Test
    fun AC45_over5MbSwitchesToNewFile() {
        val log = FileLog(tmp.root, { noon }, utc)
        val f = File(tmp.root, "tabspan-20261124.log")
        RandomAccessFile(f, "rw").use { it.setLength(FileLog.MAX_BYTES - 10) }
        log.write('I', "short") // 34 バイトで 5 MB を超える
        assertEquals(FileLog.MAX_BYTES - 10, File(tmp.root, "tabspan-20261124-1.log").length())
        assertTrue(f.readText().endsWith("I short\n"))
        assertTrue(f.length() < 100)
        // もう一度超えたら -2
        RandomAccessFile(f, "rw").use { it.setLength(FileLog.MAX_BYTES) }
        log.write('I', "again")
        assertTrue(File(tmp.root, "tabspan-20261124-2.log").exists())
        assertEquals(listOf("tabspan-20261124-1.log", "tabspan-20261124-2.log", "tabspan-20261124.log"), log.files().map { it.name })
    }

    @Test
    fun underLimitStaysInOneFile() {
        val log = FileLog(tmp.root, { noon }, utc)
        val f = File(tmp.root, "tabspan-20261124.log")
        RandomAccessFile(f, "rw").use { it.setLength(FileLog.MAX_BYTES - 100) }
        log.write('I', "fits")
        assertEquals(listOf("tabspan-20261124.log"), log.files().map { it.name })
    }

    @Test
    fun AC45_startupDeletes15DaysOldAndKeeps14() {
        listOf("tabspan-20261109.log", "tabspan-20261109-1.log", "tabspan-20261110.log", "tabspan-20261124.log", "other.txt")
            .forEach { File(tmp.root, it).writeText("x") }
        val log = FileLog(tmp.root, { noon }, utc)
        log.deleteOld()
        assertFalse(File(tmp.root, "tabspan-20261109.log").exists()) // 15 日前
        assertFalse(File(tmp.root, "tabspan-20261109-1.log").exists())
        assertTrue(File(tmp.root, "tabspan-20261110.log").exists()) // 14 日前
        assertTrue(File(tmp.root, "tabspan-20261124.log").exists())
        assertTrue(File(tmp.root, "other.txt").exists()) // ログでない物は触らない
    }

    @Test
    fun dayChangeStartsNewFile() {
        var t = noon
        val log = FileLog(tmp.root, { t }, utc)
        log.write('I', "a")
        t += day
        log.write('W', "b")
        assertEquals(listOf("tabspan-20261124.log", "tabspan-20261125.log"), log.files().map { it.name })
    }

    @Test
    fun AC39_manifestAsksOnlyTwoPermissions() {
        val manifest = File("src/main/AndroidManifest.xml").readText() // 単体テストは app のフォルダで動く
        val permissions = Regex("<uses-permission[^>]*android:name=\"([^\"]+)\"").findAll(manifest).map { it.groupValues[1] }.toList()
        assertEquals(listOf("android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE"), permissions)
    }
}
