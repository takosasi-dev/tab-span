// 映像の表示の矩形(FR-14): 縦横比を保って画面の中央に収める。scale = min(画面の幅 / 映像の幅, 画面の高さ / 映像の高さ)。
// 比べはかけ算の整数で行い、はみ出さない。余白は呼び出し側が黒で塗る。
package io.github.takosasidev.tabspan

data class FitRect(val x: Int, val y: Int, val width: Int, val height: Int) {
    companion object {
        /** 映像の大きさがまだ分からない(0 以下)ときは、画面全体を返す。 */
        fun fit(viewWidth: Int, viewHeight: Int, videoWidth: Int, videoHeight: Int): FitRect {
            if (viewWidth <= 0 || viewHeight <= 0) return FitRect(0, 0, 0, 0)
            if (videoWidth <= 0 || videoHeight <= 0) return FitRect(0, 0, viewWidth, viewHeight)
            return if (viewWidth.toLong() * videoHeight <= viewHeight.toLong() * videoWidth) {
                // 幅で決まる(上下に余白)
                val h = (viewWidth.toLong() * videoHeight / videoWidth).toInt()
                FitRect(0, (viewHeight - h) / 2, viewWidth, h)
            } else {
                // 高さで決まる(左右に余白)
                val w = (viewHeight.toLong() * videoWidth / videoHeight).toInt()
                FitRect((viewWidth - w) / 2, 0, w, viewHeight)
            }
        }
    }
}
