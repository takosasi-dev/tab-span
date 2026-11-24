// ペアリングの保存(SharedPreferences、アプリの専用領域): 端末の deviceId(初回に作る UUID)と、PC ごとの
// pcInstanceId・pcName・証明書の指紋・token、ペアリングした日、TCP のポート、前回つながったアドレスと手で入れたアドレス。
// 端末の固有の番号は使わない。v0.2.0 の行(後から足した項目が無い)もそのまま読める。
package io.github.takosasidev.tabspan.link

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/**
 * 登録済みの PC 1 台。[fingerprint] は証明書の DER の SHA-256 の 16 進、[token] は Base64。
 * [pairedAt] はペアリングした時刻(ミリ秒、不明なら 0)。[lastHost] は前回 LAN でつながった IPv4、[manualHost] は手で入れた IPv4。
 */
data class PcEntry(
    val pcInstanceId: String,
    val pcName: String,
    val fingerprint: String,
    val token: String,
    val pairedAt: Long = 0,
    val port: Int = LinkSocket.PORT,
    val lastHost: String? = null,
    val manualHost: String? = null,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("pcInstanceId", pcInstanceId).put("pcName", pcName).put("fingerprint", fingerprint).put("token", token)
        .put("pairedAt", pairedAt).put("port", port)
        .put("lastHost", lastHost).put("manualHost", manualHost) // null なら書かない

    companion object {
        /** 後から足した項目は無くてもよい(v0.2.0 の行)。 */
        fun fromJson(o: JSONObject) = PcEntry(
            o.getString("pcInstanceId"), o.getString("pcName"), o.getString("fingerprint"), o.getString("token"),
            o.optLong("pairedAt", 0), o.optInt("port", LinkSocket.PORT),
            if (o.isNull("lastHost")) null else o.getString("lastHost"),
            if (o.isNull("manualHost")) null else o.getString("manualHost"),
        )
    }
}

class PairStore(context: Context) {
    private val prefs = context.getSharedPreferences("tabspan_link", Context.MODE_PRIVATE)

    /** 初回に作って以後は同じ値。 */
    @get:Synchronized
    val deviceId: String
        get() = prefs.getString(KEY_DEVICE_ID, null) ?: UUID.randomUUID().toString().also {
            prefs.edit().putString(KEY_DEVICE_ID, it).apply()
        }

    @Synchronized
    fun list(): List<PcEntry> {
        val text = prefs.getString(KEY_PCS, null) ?: return emptyList()
        return try {
            val array = JSONArray(text)
            List(array.length()) { PcEntry.fromJson(array.getJSONObject(it)) }
        } catch (_: org.json.JSONException) {
            emptyList() // 壊れていたら未登録として扱う(ペアリングし直せば上書きされる)
        }
    }

    /** 同じ pcInstanceId の行は、並びの位置を保って置き換える。無ければ末尾に足す。 */
    @Synchronized
    fun save(entry: PcEntry) {
        val old = list()
        write(if (old.any { it.pcInstanceId == entry.pcInstanceId }) old.map { if (it.pcInstanceId == entry.pcInstanceId) entry else it } else old + entry)
    }

    /** 今保存されている行を読み直して一部を変える(別のスレッドの書き込みを消さない)。行が無ければ何もしない。 */
    @Synchronized
    fun update(pcInstanceId: String, change: (PcEntry) -> PcEntry) {
        list().firstOrNull { it.pcInstanceId == pcInstanceId }?.let { save(change(it)) }
    }

    @Synchronized
    fun remove(pcInstanceId: String) = write(list().filter { it.pcInstanceId != pcInstanceId })

    private fun write(entries: List<PcEntry>) {
        prefs.edit().putString(KEY_PCS, JSONArray(entries.map { it.toJson() }).toString()).apply()
    }

    private companion object {
        const val KEY_DEVICE_ID = "deviceId"
        const val KEY_PCS = "pcs"
    }
}
