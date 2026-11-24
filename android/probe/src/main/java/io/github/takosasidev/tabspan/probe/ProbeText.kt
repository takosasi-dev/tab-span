// probe の Android に依らない計算(定数の名前への直し、横向きへの直し、§9.7 の候補の表、ポートの検査、指紋の形)。
// android.* を import しない(JVM の単体テストで確かめるため)。数値は android-35 の android.jar を javap で見た値。
package io.github.takosasidev.tabspan.probe

data class Size(val width: Int, val height: Int) {
    val swapped: Size get() = Size(height, width)
    override fun toString() = "${width}x$height"
}

object ProbeText {
    const val DEFAULT_PORT = 47815

    /** §9.7 の候補の大きさ(横向き)とフレームレート。 */
    val CANDIDATE_SIZES = listOf(
        Size(2560, 1600), Size(2560, 1440), Size(1920, 1200),
        Size(1920, 1080), Size(1280, 800), Size(1280, 720),
    )
    val CANDIDATE_FPS = listOf(60, 30)

    /** 候補の大きさ × fps を、表の順(大きさごとに 60 → 30)に並べた物。 */
    val CANDIDATES: List<Pair<Size, Int>> = CANDIDATE_SIZES.flatMap { s -> CANDIDATE_FPS.map { s to it } }

    // MediaCodecInfo.CodecProfileLevel の AVCProfile* / AVCLevel*
    private val AVC_PROFILES = mapOf(
        1 to "Baseline", 2 to "Main", 4 to "Extended", 8 to "High",
        16 to "High10", 32 to "High422", 64 to "High444",
        65536 to "ConstrainedBaseline", 524288 to "ConstrainedHigh",
    )
    private val AVC_LEVELS = mapOf(
        1 to "1", 2 to "1b", 4 to "1.1", 8 to "1.2", 16 to "1.3",
        32 to "2", 64 to "2.1", 128 to "2.2",
        256 to "3", 512 to "3.1", 1024 to "3.2",
        2048 to "4", 4096 to "4.1", 8192 to "4.2",
        16384 to "5", 32768 to "5.1", 65536 to "5.2",
        131072 to "6", 262144 to "6.1", 524288 to "6.2",
    )

    // MediaCodecInfo.CodecCapabilities の COLOR_* のうち、デコーダーの出力で見かける物だけ
    private val COLOR_FORMATS = mapOf(
        19 to "YUV420Planar", 20 to "YUV420PackedPlanar", 21 to "YUV420SemiPlanar",
        39 to "YUV420PackedSemiPlanar",
        2130708361 to "Surface", 2135033992 to "YUV420Flexible",
        2141391872 to "QCOM_YUV420SemiPlanar", 2130706688 to "TI_YUV420PackedSemiPlanar",
    )

    fun avcProfileName(value: Int) = AVC_PROFILES[value] ?: hex(value)
    fun avcLevelName(value: Int) = AVC_LEVELS[value] ?: hex(value)
    fun colorFormatName(value: Int) = COLOR_FORMATS[value] ?: hex(value)

    /** 幅 ≥ 高さ に直した大きさ。 */
    fun landscape(width: Int, height: Int) = Size(maxOf(width, height), minOf(width, height))

    fun isValidPort(port: Int) = port in 1..65535

    /** 入力欄の文字をポートに直す。範囲外や数でなければ null。 */
    fun parsePort(text: String?): Int? = text?.trim()?.toIntOrNull()?.takeIf(::isValidPort)

    /** PC の X509Certificate2.GetCertHashString(SHA256) と同じ形(大文字の 16 進、区切りなし)。 */
    fun fingerprint(digest: ByteArray) = digest.joinToString("") { "%02X".format(it) }

    fun yesNo(value: Boolean) = if (value) "可" else "不可"

    fun hex(value: Int) = "0x" + Integer.toHexString(value).uppercase()
}
