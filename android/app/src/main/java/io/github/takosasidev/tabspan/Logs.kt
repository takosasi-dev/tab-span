// 端末のログ(FR-28): アプリの専用領域の logs/tabspan-<yyyyMMdd>.log に 1 行ずつ足す。5 MB を超えるなら今のファイルを
// tabspan-<yyyyMMdd>-<n>.log に名前を変えて新しく始め、起動時に 14 日より古い日付のファイルを消す。
// 映像・タッチの座標・トークン・秘密鍵を書かない(呼ぶ側の決まり)。FileLog は java.io だけ(JVM の単体テスト用)。
package io.github.takosasidev.tabspan

import android.content.Context
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.TimeZone

class FileLog(
    val dir: File,
    private val now: () -> Long = System::currentTimeMillis,
    private val zone: TimeZone = TimeZone.getDefault(),
) {
    private val day = SimpleDateFormat("yyyyMMdd", Locale.US).apply { timeZone = zone }
    private val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US).apply { timeZone = zone }

    /** 書けなくても(容量が無いなど)例外を出さない。 */
    @Synchronized
    fun write(level: Char, message: String) {
        try {
            dir.mkdirs()
            val t = Date(now())
            val line = "${stamp.format(t)} $level $message\n".toByteArray(Charsets.UTF_8)
            val file = File(dir, "tabspan-${day.format(t)}.log")
            if (file.length() > 0 && file.length() + line.size > MAX_BYTES) rotate(file)
            FileOutputStream(file, true).use { it.write(line) }
        } catch (_: IOException) {
        }
    }

    private fun rotate(file: File) {
        val base = file.name.removeSuffix(".log")
        var n = 1
        while (File(dir, "$base-$n.log").exists()) n++
        file.renameTo(File(dir, "$base-$n.log"))
    }

    /** 起動時: 日付が今日の 14 日前より前のファイルを消す(14 日前の日付の物は残す)。 */
    @Synchronized
    fun deleteOld() {
        val limit = Calendar.getInstance(zone).apply {
            timeInMillis = now()
            add(Calendar.DAY_OF_YEAR, -KEEP_DAYS)
        }.let { day.format(it.time) }
        // yyyyMMdd は文字の並びで日付の前後と同じ
        dir.listFiles()?.forEach { f -> NAME.matchEntire(f.name)?.let { if (it.groupValues[1] < limit) f.delete() } }
    }

    /** ログのファイル(古い順)。 */
    @Synchronized
    fun files(): List<File> = dir.listFiles()?.filter { NAME.matches(it.name) }?.sortedBy { it.name }.orEmpty()

    companion object {
        const val MAX_BYTES = 5L * 1024 * 1024
        const val KEEP_DAYS = 14
        private val NAME = Regex("tabspan-(\\d{8})(-\\d+)?\\.log")
    }
}

/** Logcat とファイルの両方へ書く口。 */
object AppLog {
    private const val TAG = "TabSpan"
    @Volatile private var file: FileLog? = null

    /** アプリの起動時に 1 回。古いログを消す。 */
    @Synchronized
    fun init(context: Context) {
        if (file != null) return
        file = FileLog(File(context.filesDir, "logs")).also { it.deleteOld() }
    }

    fun i(message: String) {
        Log.i(TAG, message)
        file?.write('I', message)
    }

    fun w(message: String) {
        Log.w(TAG, message)
        file?.write('W', message)
    }

    fun files(): List<File> = file?.files().orEmpty()
}
