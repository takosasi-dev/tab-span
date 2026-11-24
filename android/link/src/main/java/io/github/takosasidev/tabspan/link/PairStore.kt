// ペアリングの保存(SharedPreferences、アプリの専用領域): 端末の deviceId(初回に作る UUID)と、PC ごとの
// pcInstanceId・pcName・証明書の指紋・token。端末の固有の番号は使わない。
package io.github.takosasidev.tabspan.link

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** 登録済みの PC 1 台。[fingerprint] は証明書の DER の SHA-256 の 16 進、[token] は Base64。 */
data class PcEntry(val pcInstanceId: String, val pcName: String, val fingerprint: String, val token: String)

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
            List(array.length()) {
                val o = array.getJSONObject(it)
                PcEntry(o.getString("pcInstanceId"), o.getString("pcName"), o.getString("fingerprint"), o.getString("token"))
            }
        } catch (_: org.json.JSONException) {
            emptyList() // 壊れていたら未登録として扱う(ペアリングし直せば上書きされる)
        }
    }

    /** 同じ pcInstanceId の行は置き換える。 */
    @Synchronized
    fun save(entry: PcEntry) = write(list().filter { it.pcInstanceId != entry.pcInstanceId } + entry)

    @Synchronized
    fun remove(pcInstanceId: String) = write(list().filter { it.pcInstanceId != pcInstanceId })

    private fun write(entries: List<PcEntry>) {
        val array = JSONArray(entries.map {
            JSONObject().put("pcInstanceId", it.pcInstanceId).put("pcName", it.pcName)
                .put("fingerprint", it.fingerprint).put("token", it.token)
        })
        prefs.edit().putString(KEY_PCS, array.toString()).apply()
    }

    private companion object {
        const val KEY_DEVICE_ID = "deviceId"
        const val KEY_PCS = "pcs"
    }
}
