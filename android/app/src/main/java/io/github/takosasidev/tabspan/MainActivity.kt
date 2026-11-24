// 端末側の画面: ホーム・ペアリング・映像(下から出るメニュー)と設定(SettingsScreen.kt)。Compose、固定の暗い配色。
// 接続の状態は Connector が持つ。映像の画面の間だけ、画面を点けたままにし、ステータスバーとナビゲーションバーを隠す(FR-20)。
package io.github.takosasidev.tabspan

import android.os.Build
import android.os.Bundle
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.annotation.StringRes
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.pointerInteropFilter
import androidx.compose.ui.layout.layout
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

class MainActivity : ComponentActivity() {
    private lateinit var connector: Connector

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        AppLog.init(this)
        AppLog.i("app ${BuildConfig.VERSION_NAME} on ${Build.MODEL} API ${Build.VERSION.SDK_INT}")
        connector = Connector.get(this)
        setContent {
            MaterialTheme(colorScheme = COLORS) {
                val ui = connector.ui
                SystemBars(ui.screen == Screen.VIDEO)
                // 映像の画面では戻る操作でメニューを出し入れする(ナビゲーションバーは引き出して使う)
                BackHandler(enabled = ui.screen != Screen.HOME) {
                    when (ui.screen) {
                        Screen.VIDEO -> connector.setMenu(!ui.menuOpen)
                        Screen.SETTINGS -> connector.closeSettings()
                        else -> connector.cancel()
                    }
                }
                when (ui.screen) {
                    Screen.HOME -> HomeScreen(ui, connector)
                    Screen.PAIRING -> PairingScreen(ui, connector)
                    Screen.VIDEO -> VideoScreen(ui, connector)
                    Screen.SETTINGS -> SettingsScreen(ui, connector)
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        connector.onForeground()
    }

    override fun onStop() {
        super.onStop()
        connector.onBackground()
    }

    @Composable
    private fun SystemBars(video: Boolean) {
        DisposableEffect(video) {
            val controller = WindowCompat.getInsetsController(window, window.decorView)
            if (video) {
                window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                controller.hide(WindowInsetsCompat.Type.systemBars())
            } else {
                window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                controller.show(WindowInsetsCompat.Type.systemBars())
            }
            onDispose { }
        }
    }

    private companion object {
        val COLORS = darkColorScheme(
            primary = Color(0xFF8AB4F8),
            onPrimary = Color(0xFF0B1A2E),
            background = Color(0xFF121212),
            surface = Color(0xFF121212),
            onBackground = Color(0xFFE6E6E6),
            onSurface = Color(0xFFE6E6E6),
        )
    }
}

@Composable
private fun text(t: UiText): String = stringResource(t.id, *t.args)

@Composable
private fun HomeScreen(ui: UiState, connector: Connector) {
    Column(
        Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).safeDrawingPadding().padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                stringResource(R.string.app_name), style = MaterialTheme.typography.headlineMedium,
                color = MaterialTheme.colorScheme.onBackground, modifier = Modifier.weight(1f),
            )
            TextButton(onClick = { connector.openSettings() }) { Text(stringResource(R.string.btn_settings)) }
        }
        if (ui.pcs.isEmpty()) {
            Text(stringResource(R.string.home_no_pc), modifier = Modifier.widthIn(max = 640.dp), color = MaterialTheme.colorScheme.onBackground)
        } else {
            ui.pcs.forEach { Text(it.pcName, style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.onBackground) }
        }
        ui.message?.let { Text(text(it), modifier = Modifier.widthIn(max = 640.dp), color = MaterialTheme.colorScheme.onBackground) }
        Spacer(Modifier.height(8.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            if (ui.pcs.isNotEmpty()) {
                Button(onClick = { if (ui.busy) connector.cancel() else connector.connect() }) {
                    Text(stringResource(if (ui.busy) R.string.btn_cancel else R.string.btn_connect))
                }
            }
            OutlinedButton(onClick = { connector.startPairing() }) { Text(stringResource(R.string.btn_add_pc)) }
        }
    }
}

@Composable
private fun PairingScreen(ui: UiState, connector: Connector) {
    Column(
        Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).safeDrawingPadding().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(24.dp, Alignment.CenterVertically),
    ) {
        Text(stringResource(R.string.pair_title), style = MaterialTheme.typography.headlineMedium, color = MaterialTheme.colorScheme.onBackground)
        ui.pairCode?.let { code ->
            Text(stringResource(R.string.pair_compare), color = MaterialTheme.colorScheme.onBackground)
            Text(
                code.toList().joinToString(" "),
                fontSize = 72.sp,
                fontFamily = FontFamily.Monospace,
                color = MaterialTheme.colorScheme.primary,
            )
            Text(stringResource(R.string.pair_hint), color = MaterialTheme.colorScheme.onBackground)
        }
        ui.message?.let { Text(text(it), textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 640.dp), color = MaterialTheme.colorScheme.onBackground) }
        OutlinedButton(onClick = { connector.cancelPairing() }) { Text(stringResource(R.string.btn_cancel)) }
    }
}

@OptIn(ExperimentalComposeUiApi::class)
@Composable
private fun VideoScreen(ui: UiState, connector: Connector) {
    BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black)) {
        val rect = FitRect.fit(constraints.maxWidth, constraints.maxHeight, ui.videoWidth, ui.videoHeight)
        // SurfaceView そのものを表示の矩形の大きさにする(余白は黒い箱)
        AndroidView(
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = connector.surfaceChanged(holder.surface)
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                        override fun surfaceDestroyed(holder: SurfaceHolder) = connector.surfaceChanged(null)
                    })
                }
            },
            modifier = Modifier
                .offset { IntOffset(rect.x, rect.y) }
                .layout { measurable, _ ->
                    val placeable = measurable.measure(Constraints.fixed(rect.width, rect.height))
                    layout(placeable.width, placeable.height) { placeable.place(0, 0) }
                },
        )
        val waiting = when {
            ui.paused -> R.string.video_paused
            !ui.firstFrame -> R.string.video_waiting
            else -> null
        }
        waiting?.let {
            Text(stringResource(it), color = Color.White, modifier = Modifier.align(Alignment.Center))
        }
        if (ui.showOverlay) {
            Text(
                stringResource(R.string.overlay_format, ui.fps, ui.latencyMs, ui.dropped, ui.videoWidth, ui.videoHeight),
                color = Color.White,
                fontSize = 12.sp,
                fontFamily = FontFamily.Monospace,
                modifier = Modifier.padding(8.dp).background(Color(0x99000000)).padding(horizontal = 6.dp, vertical = 2.dp),
            )
        }
        // タッチは PC へ送る(SurfaceView の上に重ねた透明な面で受ける。座標は FitRect と同じ面)
        val currentRect by rememberUpdatedState(rect)
        if (ui.menuOpen) {
            // シートの外を押したら閉じる(シートが出ている間はタッチを PC に送らない)
            Box(Modifier.fillMaxSize().background(Color(0x66000000)).pointerInput(Unit) { detectTapGestures { connector.setMenu(false) } })
        } else {
            Box(Modifier.fillMaxSize().pointerInteropFilter { connector.onTouch(it, currentRect); true })
        }
        // 下から出るシート(A4)。別の窓を作らない(映像の間に隠したバーを出さない)
        AnimatedVisibility(
            visible = ui.menuOpen,
            enter = slideInVertically { it },
            exit = slideOutVertically { it },
            modifier = Modifier.align(Alignment.BottomCenter),
        ) {
            VideoMenu(ui, connector)
        }
    }
}

@Composable
private fun VideoMenu(ui: UiState, connector: Connector) {
    Column(
        Modifier
            .widthIn(min = 320.dp, max = 560.dp)
            .background(Color(0xFF1E1E1E), RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp))
            .pointerInput(Unit) { detectTapGestures { } } // シートの上の押しは下へ通さない
            .padding(horizontal = 24.dp, vertical = 20.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        SettingSwitch(R.string.menu_touch, ui.deviceTouch, enabled = ui.pcTouch) { connector.setDeviceTouch(it) }
        if (!ui.pcTouch) Text(stringResource(R.string.menu_pc_touch_off), fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurface)
        SettingSwitch(R.string.menu_overlay, ui.showOverlay) { connector.setOverlay(it) }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedButton(onClick = { connector.cancel() }) { Text(stringResource(R.string.menu_disconnect)) }
            Button(onClick = { connector.setMenu(false) }) { Text(stringResource(R.string.menu_close)) }
        }
    }
}

/** 文言とスイッチの 1 行(A4 と A5)。 */
@Composable
fun SettingSwitch(@StringRes label: Int, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(
            stringResource(label), modifier = Modifier.weight(1f),
            color = MaterialTheme.colorScheme.onSurface.copy(alpha = if (enabled) 1f else 0.5f),
        )
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}
