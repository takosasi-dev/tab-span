// A5 設定: PC の一覧(名前・ペアリングした日。行からアドレスの手入力と解除)、自動でつなぐ、診断の表示、ログの共有、この端末の版。
// ログの共有は確認の後に Android の共有の画面へ渡す(FileProvider。渡せるのは logs のフォルダだけ)。
package io.github.takosasidev.tabspan

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import io.github.takosasidev.tabspan.link.PROTOCOL_VERSION
import io.github.takosasidev.tabspan.link.PcEntry
import java.text.DateFormat
import java.util.Date

@Composable
fun SettingsScreen(ui: UiState, connector: Connector) {
    var openRow by remember { mutableStateOf<String?>(null) }
    var unpairing by remember { mutableStateOf<PcEntry?>(null) }
    var sharing by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val onBg = MaterialTheme.colorScheme.onBackground

    Column(
        Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).safeDrawingPadding()
            .verticalScroll(rememberScrollState()).padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.settings_title), style = MaterialTheme.typography.headlineMedium, color = onBg, modifier = Modifier.weight(1f))
            TextButton(onClick = { connector.closeSettings() }) { Text(stringResource(R.string.btn_back)) }
        }
        Column(Modifier.widthIn(max = 720.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Text(stringResource(R.string.settings_pcs), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            if (ui.pcs.isEmpty()) Text(stringResource(R.string.settings_no_pc), color = onBg)
            ui.pcs.forEach { pc ->
                PcRow(pc, open = openRow == pc.pcInstanceId, connector, onToggle = {
                    openRow = if (openRow == pc.pcInstanceId) null else pc.pcInstanceId
                }, onUnpair = { unpairing = pc })
            }
            HorizontalDivider()
            SettingSwitch(R.string.settings_auto_connect, ui.autoConnect) { connector.setAutoConnect(it) }
            SettingSwitch(R.string.menu_overlay, ui.showOverlay) { connector.setOverlay(it) }
            OutlinedButton(onClick = { sharing = true }) { Text(stringResource(R.string.settings_share_log)) }
            HorizontalDivider()
            Text(stringResource(R.string.settings_version_title), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            Text(stringResource(R.string.settings_version, BuildConfig.VERSION_NAME, PROTOCOL_VERSION), color = onBg)
        }
    }

    unpairing?.let { pc ->
        AlertDialog(
            onDismissRequest = { unpairing = null },
            text = { Text(stringResource(R.string.settings_unpair_confirm, pc.pcName)) },
            confirmButton = {
                TextButton(onClick = {
                    connector.unpair(pc.pcInstanceId)
                    unpairing = null
                    openRow = null
                }) { Text(stringResource(R.string.settings_unpair)) }
            },
            dismissButton = { TextButton(onClick = { unpairing = null }) { Text(stringResource(R.string.btn_cancel)) } },
        )
    }
    if (sharing) {
        AlertDialog(
            onDismissRequest = { sharing = false },
            text = { Text(stringResource(R.string.settings_share_confirm)) },
            confirmButton = {
                TextButton(onClick = {
                    sharing = false
                    shareLogs(context)
                }) { Text(stringResource(R.string.settings_share)) }
            },
            dismissButton = { TextButton(onClick = { sharing = false }) { Text(stringResource(R.string.btn_cancel)) } },
        )
    }
}

@Composable
private fun PcRow(pc: PcEntry, open: Boolean, connector: Connector, onToggle: () -> Unit, onUnpair: () -> Unit) {
    val onSurface = MaterialTheme.colorScheme.onSurface
    Column(
        Modifier.fillMaxWidth().background(Color(0xFF1E1E1E), RoundedCornerShape(8.dp)).clickable(onClick = onToggle).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(pc.pcName, style = MaterialTheme.typography.titleLarge, color = onSurface)
        if (pc.pairedAt > 0) {
            Text(stringResource(R.string.settings_paired_on, DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(pc.pairedAt))), color = onSurface)
        }
        if (open) {
            var text by remember(pc.pcInstanceId) { mutableStateOf(pc.manualHost.orEmpty()) }
            var error by remember(pc.pcInstanceId) { mutableStateOf(false) }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                OutlinedTextField(
                    value = text,
                    onValueChange = {
                        text = it
                        error = false
                    },
                    label = { Text(stringResource(R.string.settings_manual_address)) },
                    placeholder = { Text("192.168.1.10") },
                    isError = error,
                    supportingText = if (error) ({ Text(stringResource(R.string.settings_address_invalid)) }) else null,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.weight(1f),
                )
                Button(onClick = { error = !connector.setManualHost(pc.pcInstanceId, text) }) { Text(stringResource(R.string.btn_save)) }
            }
            OutlinedButton(onClick = onUnpair) { Text(stringResource(R.string.settings_unpair)) }
        }
    }
}

/** ログのファイルを共有の画面へ渡す(読みの許可を付けて)。渡す先が無ければ何もしない。 */
private fun shareLogs(context: Context) {
    val files = AppLog.files()
    if (files.isEmpty()) return
    val uris = ArrayList<Uri>(files.map { FileProvider.getUriForFile(context, "${context.packageName}.logs", it) })
    val send = Intent(Intent.ACTION_SEND_MULTIPLE).apply {
        type = "text/plain"
        putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
        clipData = ClipData.newRawUri(null, uris[0]).also { clip -> uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) } }
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }
    try {
        context.startActivity(Intent.createChooser(send, context.getString(R.string.settings_share_log)))
    } catch (e: ActivityNotFoundException) {
        AppLog.i("no app to share logs")
    }
}
