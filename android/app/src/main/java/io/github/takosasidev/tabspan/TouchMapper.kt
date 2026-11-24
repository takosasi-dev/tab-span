// タッチの変換(FR-21): Android の指の番号 → 通信の番号(空いている一番小さい 0..9)、表示の矩形の座標 → 0..65535。
// 矩形の外で触り始めた指は送らない。11 本目からは無視する。矩形の中で触り始めた指は、外へ出ても端に寄せて送る。
// Android に依存しない(JVM の単体テストで確かめる)。座標はログに書かない(FR-28)。
package io.github.takosasidev.tabspan

import kotlin.math.roundToLong

class TouchMapper {
    data class Contact(val id: Int, val action: Int, val x: Int, val y: Int)

    private class Active(val id: Int, var x: Int, var y: Int)

    /** Android の指の番号 → 送っている指。矩形の外で触り始めた指・11 本目の指は入らない。 */
    private val active = HashMap<Int, Active>()

    val isEmpty get() = active.isEmpty()

    fun down(pointerId: Int, x: Float, y: Float, rect: FitRect): Contact? {
        if (pointerId in active || active.size >= MAX_CONTACTS) return null
        if (x < rect.x || y < rect.y || x >= rect.x + rect.width || y >= rect.y + rect.height) return null
        val used = active.values.map { it.id }
        val id = (0 until MAX_CONTACTS).first { it !in used }
        val a = Active(id, norm(x - rect.x, rect.width), norm(y - rect.y, rect.height))
        active[pointerId] = a
        return Contact(id, DOWN, a.x, a.y)
    }

    /** 知らない指・位置が変わっていない指は null。 */
    fun move(pointerId: Int, x: Float, y: Float, rect: FitRect): Contact? {
        val a = active[pointerId] ?: return null
        val nx = norm(x - rect.x, rect.width)
        val ny = norm(y - rect.y, rect.height)
        if (nx == a.x && ny == a.y) return null
        a.x = nx
        a.y = ny
        return Contact(a.id, MOVE, nx, ny)
    }

    fun up(pointerId: Int, x: Float, y: Float, rect: FitRect): Contact? {
        val a = active.remove(pointerId) ?: return null
        return Contact(a.id, UP, norm(x - rect.x, rect.width), norm(y - rect.y, rect.height))
    }

    /** 触れている指をすべて cancel にして(最後に送った位置で)忘れる。 */
    fun cancelAll(): List<Contact> {
        val out = active.values.sortedBy { it.id }.map { Contact(it.id, CANCEL, it.x, it.y) }
        active.clear()
        return out
    }

    companion object {
        const val DOWN = 0
        const val MOVE = 1
        const val UP = 2
        const val CANCEL = 3
        const val MAX_CONTACTS = 10

        /** 矩形の左上からの距離 → 0..65535(左上 0、右下の画素 65535)。 */
        fun norm(offset: Float, size: Int): Int {
            if (size <= 1) return 0
            return (offset * 65535.0 / (size - 1)).roundToLong().coerceIn(0, 65535).toInt()
        }

        /** Touch(0x20)の中身: [count: u8] と、指ごとに [id: u8][action: u8][x: u16 BE][y: u16 BE]。 */
        fun encode(contacts: List<Contact>): ByteArray {
            val out = ByteArray(1 + contacts.size * 6)
            out[0] = contacts.size.toByte()
            contacts.forEachIndexed { i, c ->
                val o = 1 + i * 6
                out[o] = c.id.toByte()
                out[o + 1] = c.action.toByte()
                out[o + 2] = (c.x shr 8).toByte()
                out[o + 3] = c.x.toByte()
                out[o + 4] = (c.y shr 8).toByte()
                out[o + 5] = c.y.toByte()
            }
            return out
        }
    }
}
