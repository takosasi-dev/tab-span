// 復号のテストの Android に依らない部分: ファイル名の読み取り、Annex B の NAL の切り出しと AUD での区切り、中央値、表と R-4 の判定。
// android.* を import しない(JVM の単体テストで確かめるため)。
package io.github.takosasidev.tabspan.probe

/** 入力のファイル 1 本の大きさと fps(ファイル名から読む)。 */
data class Clip(val size: Size, val fps: Int) {
    override fun toString() = "$size@$fps"
}

/** NAL 1 つ。[start] は開始コード(3 か 4 バイト)の先頭、[payload] は NAL の頭のバイト、[end] は次の開始コードの先頭(含まない)。 */
data class Nal(val start: Int, val payload: Int, val end: Int, val type: Int)

/** ファイル 1 本 × デコーダー 1 つの結果(表と R-4 の判定に使う分だけ)。 */
data class DecodeCell(
    val clip: Clip,
    val decoder: String,
    val surface: Boolean,
    /** 速さの測定で出たフレームの数と、入れたユニットの数。 */
    val frames: Int,
    val expected: Int,
    val speedFps: Double?,
    val medianMs: Double?,
    /** 測れなかったときの短い理由(「例外」「時間切れ」など)。測れたら null。 */
    val failure: String?,
) {
    /** 全部のユニットが絵になり、ファイルの fps 以上の速さで出た。 */
    val decodedInRealTime: Boolean get() = failure == null && frames >= expected && (speedFps ?: 0.0) >= clip.fps
}

object DecodeText {
    const val AUD = 9
    const val SPS = 7
    const val PPS = 8
    val R4_CLIP = Clip(Size(1280, 800), 30)

    private val FILE_NAME = Regex("""tabspan_(\d+)x(\d+)_(\d+)\.h264""")

    /** `tabspan_<幅>x<高さ>_<fps>.h264` を読む。形が違えば null。 */
    fun parseFileName(name: String): Clip? {
        val m = FILE_NAME.matchEntire(name) ?: return null
        val (w, h, fps) = m.destructured.toList().map { it.toIntOrNull() ?: return null }
        if (w <= 0 || h <= 0 || fps <= 0) return null
        return Clip(Size(w, h), fps)
    }

    fun fileName(clip: Clip) = "tabspan_${clip.size.width}x${clip.size.height}_${clip.fps}.h264"

    /** Annex B の NAL を並べる。開始コードの前の 0 が 1 つあれば 4 バイトの開始コードとして含める。 */
    fun nalUnits(data: ByteArray): List<Nal> {
        val starts = ArrayList<IntArray>() // [開始コードの先頭, NAL の頭]
        var i = 0
        while (i + 3 < data.size) {
            if (data[i].toInt() == 0 && data[i + 1].toInt() == 0 && data[i + 2].toInt() == 1) {
                val start = if (i > 0 && data[i - 1].toInt() == 0) i - 1 else i
                starts.add(intArrayOf(start, i + 3))
                i += 3
            } else {
                i++
            }
        }
        return starts.mapIndexed { k, (start, payload) ->
            val end = if (k + 1 < starts.size) starts[k + 1][0] else data.size
            Nal(start, payload, end, data[payload].toInt() and 0x1F)
        }
    }

    /**
     * AUD(種類 9)の前で区切ったアクセス ユニット。最初の AUD より前のバイトは最初のユニットに含める。
     * AUD が 1 つも無ければ空(呼ぶ側で「AUD が無い」と出す)。
     */
    fun splitAccessUnits(data: ByteArray): List<ByteArray> {
        val cuts = nalUnits(data).filter { it.type == AUD }.map { it.start }.toMutableList()
        if (cuts.isEmpty()) return emptyList()
        cuts[0] = 0
        cuts.add(data.size)
        return cuts.zipWithNext { a, b -> data.copyOfRange(a, b) }
    }

    /** ユニットの中の最初の NAL [type] を、4 バイトの開始コード付きで返す(csd-0 / csd-1 にそのまま使う形)。 */
    fun firstNal(unit: ByteArray, type: Int): ByteArray? {
        val nal = nalUnits(unit).firstOrNull { it.type == type } ?: return null
        return byteArrayOf(0, 0, 0, 1) + unit.copyOfRange(nal.payload, nal.end)
    }

    /** ユニットに入っている NAL の種類(並び順)。 */
    fun nalTypes(unit: ByteArray) = nalUnits(unit).map { it.type }

    /** presentationTimeUs から入れた番号に戻す(入れたときは 番号 × 1e6 / fps)。範囲外なら null。 */
    fun indexOf(ptsUs: Long, fps: Int, count: Int): Int? {
        val k = Math.round(ptsUs * fps / 1_000_000.0).toInt()
        return k.takeIf { it in 0 until count }
    }

    /** 番号 k のユニットの presentationTimeUs。 */
    fun ptsOf(k: Int, fps: Int) = k * 1_000_000L / fps

    fun median(values: List<Double>): Double? {
        if (values.isEmpty()) return null
        val s = values.sorted()
        val mid = s.size / 2
        return if (s.size % 2 == 1) s[mid] else (s[mid - 1] + s[mid]) / 2
    }

    fun f1(value: Double?) = if (value == null) "-" else "%.1f".format(value)

    /** 表の 1 マス。 */
    fun cellText(cell: DecodeCell?): String = when {
        cell == null -> "-"
        cell.failure != null -> cell.failure
        else -> "${f1(cell.speedFps)}fps/${f1(cell.medianMs)}ms" +
            (if (cell.frames < cell.expected) " (${cell.frames}/${cell.expected}枚)" else "") +
            (if (cell.surface) "" else " BB")
    }

    /** 大きさ@fps × デコーダー の表(1 行目は見出し)。列は空白で揃える。 */
    fun table(clips: List<Clip>, decoders: List<String>, cells: List<DecodeCell>): List<String> {
        val rows = listOf(listOf("大きさ@fps") + decoders) + clips.map { clip ->
            listOf(clip.toString()) + decoders.map { d -> cellText(cells.firstOrNull { it.clip == clip && it.decoder == d }) }
        }
        val widths = rows[0].indices.map { c -> rows.maxOf { it[c].length } }
        return rows.map { row -> row.mapIndexed { c, s -> s.padEnd(widths[c]) }.joinToString(" | ").trimEnd() }
    }

    /** R-4(1280x800@30 を復号できるか)の 1 行。測れていなければ「判定できない」(「できない」とは出さない)。 */
    fun r4Line(cells: List<DecodeCell>): String {
        val head = "R-4(${R4_CLIP} を復号できるか): "
        val rule = "[条件: 速さの測定で全ユニットが絵になり、実測 fps ≥ ${R4_CLIP.fps}]"
        val mine = cells.filter { it.clip == R4_CLIP }
        if (mine.isEmpty()) return head + "判定できない(${fileName(R4_CLIP)} が無い) $rule"
        val ok = mine.filter { it.decodedInRealTime }
        if (ok.isNotEmpty()) {
            val who = ok.joinToString(", ") { "${it.decoder} ${f1(it.speedFps)} fps ${if (it.surface) "Surface" else "ByteBuffer"}" }
            return head + "できた($who) $rule"
        }
        // 全部が例外・時間切れなら、復号できないのか測り方が悪いのか分からない
        if (mine.all { it.failure != null && it.frames == 0 }) {
            return head + "判定できない(全部のデコーダーで測れなかった: ${mine.joinToString(", ") { "${it.decoder} ${it.failure}" }}) $rule"
        }
        return head + "できない(${mine.joinToString(", ") { "${it.decoder} ${cellText(it)}" }}) $rule"
    }
}
