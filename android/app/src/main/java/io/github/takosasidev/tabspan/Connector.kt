// 接続の流れ(Activity の外に 1 つだけ持つ): ペアリング、Hello から映すまで、Ping への Pong、1 秒ごとの Stats、
// RequestKeyframe(1 秒に 1 回まで)、5 秒の無通信で切って 2 秒おきに USB → 前回 → 手入力 → 探索の順でつなぎ直す(5 分で諦める)。
// 端末の設定(自動でつなぐ・重ね表示・タッチ)もここで保存する。画面の状態は [ui]。
package io.github.takosasidev.tabspan

import android.content.Context
import android.net.ConnectivityManager
import android.hardware.display.DisplayManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.DisplayMetrics
import android.view.MotionEvent
import android.view.Display
import android.view.Surface
import androidx.annotation.StringRes
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.takosasidev.tabspan.link.Attempt
import io.github.takosasidev.tabspan.link.Base64Codec
import io.github.takosasidev.tabspan.link.ConnectOrder
import io.github.takosasidev.tabspan.link.DecoderInfo
import io.github.takosasidev.tabspan.link.Discovery
import io.github.takosasidev.tabspan.link.FingerprintMismatchException
import io.github.takosasidev.tabspan.link.FrameTiming
import io.github.takosasidev.tabspan.link.HelloMsg
import io.github.takosasidev.tabspan.link.Ipv4
import io.github.takosasidev.tabspan.link.LinkSocket
import io.github.takosasidev.tabspan.link.MessageType
import io.github.takosasidev.tabspan.link.Messages
import io.github.takosasidev.tabspan.link.PROTOCOL_VERSION
import io.github.takosasidev.tabspan.link.PairChallengeMsg
import io.github.takosasidev.tabspan.link.PairRequestMsg
import io.github.takosasidev.tabspan.link.PairStore
import io.github.takosasidev.tabspan.link.PairingCode
import io.github.takosasidev.tabspan.link.PcEntry
import io.github.takosasidev.tabspan.link.ReasonMsg
import io.github.takosasidev.tabspan.link.Route
import io.github.takosasidev.tabspan.link.ScreenInfo
import io.github.takosasidev.tabspan.link.SizeFps
import io.github.takosasidev.tabspan.link.StatsMsg
import io.github.takosasidev.tabspan.video.AvcCaps
import io.github.takosasidev.tabspan.video.VideoDecoder
import java.io.EOFException
import java.net.InetAddress
import java.net.ProtocolException
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

enum class Screen { HOME, PAIRING, VIDEO, SETTINGS }

/** 文言はリソースの番号と引数で持ち、画面で文字にする。 */
class UiText(@StringRes val id: Int, vararg val args: Any)

data class UiState(
    val screen: Screen = Screen.HOME,
    val pcs: List<PcEntry> = emptyList(),
    /** つないでいる途中(ホームのボタンを「やめる」にする)。 */
    val busy: Boolean = false,
    val message: UiText? = null,
    val pairCode: String? = null,
    val videoWidth: Int = 0,
    val videoHeight: Int = 0,
    val firstFrame: Boolean = false,
    val paused: Boolean = false,
    /** 診断の重ね表示(A4 と A5 で同じ値。保存する)。 */
    val showOverlay: Boolean = false,
    /** 起動した時と表に戻った時に自動でつなぐ(A5。保存する)。 */
    val autoConnect: Boolean = true,
    /** 映像の画面のメニュー(戻る操作で開く)。開いている間はタッチを送らない。 */
    val menuOpen: Boolean = false,
    /** 端末側のタッチのオン・オフ(メニュー。保存する)。 */
    val deviceTouch: Boolean = true,
    /** PC 側のタッチのオン・オフ(HelloAck.touchEnabled と Notice "touchEnabled")。 */
    val pcTouch: Boolean = true,
    val fps: Int = 0,
    val latencyMs: Int = 0,
    val dropped: Int = 0,
)

class Connector private constructor(context: Context) {
    private val store = PairStore(context)
    private val prefs = context.getSharedPreferences("tabspan_app", Context.MODE_PRIVATE)
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val screenInfo = landscapeScreen(context)
    private val main = Handler(Looper.getMainLooper())
    /** 送り(Stats・RequestKeyframe・Bye)と 1 秒の刻みを受け持つ。 */
    private val worker = Executors.newSingleThreadScheduledExecutor()
    private val decoder = VideoDecoder { requestKeyframe() }
    private val decoderInfo by lazy { readDecoderInfo() }

    var ui by mutableStateOf(
        UiState(
            pcs = store.list(),
            showOverlay = prefs.getBoolean(KEY_OVERLAY, false),
            autoConnect = prefs.getBoolean(KEY_AUTO_CONNECT, true),
            deviceTouch = prefs.getBoolean(KEY_DEVICE_TOUCH, true),
        ),
    )
        private set

    /** 増やすと、走っている接続・ペアリングの流れは画面を触らずに抜ける。 */
    @Volatile private var gen = 0
    @Volatile private var socket: LinkSocket? = null
    /** HelloAck(ok) を受けた後だけ真(認証の前に RequestKeyframe や Stats を送らない)。 */
    @Volatile private var streaming = false
    // ここから 3 つは worker だけが触る
    private var lastKeyRequestMs = -10_000L
    private var keyPending = false
    private var droppedTotal = 0
    /** メインスレッドだけが触る。 */
    private val touch = TouchMapper()

    // ---- 画面から呼ぶ(メインスレッド) ----

    /** 起動した時と表に戻った時。登録済みの PC があれば自動でつなぎ始める(FR-6)。 */
    fun onForeground() {
        if (ui.screen == Screen.HOME && !ui.busy && ui.autoConnect && store.list().isNotEmpty()) connect()
    }

    /** 裏に回った: デコーダーを止め、Bye(user) を送って閉じる(§10)。設定の画面(共有の画面を開いた時など)はそのまま。 */
    fun onBackground() {
        if (ui.screen == Screen.SETTINGS) return
        if (ui.screen != Screen.HOME || ui.busy) {
            if (ui.screen == Screen.PAIRING) cancelPairing() else cancel()
        }
    }

    fun connect() {
        val my = stopCurrent()
        ui = ui.copy(screen = Screen.HOME, busy = true, message = UiText(R.string.status_connecting), pcs = store.list())
        thread(name = "TabSpanLink") { sessionLoop(my) }
    }

    /** 利用者が切った(つなぎ直さない)。 */
    fun cancel() {
        stopCurrent()
        ui = ui.copy(screen = Screen.HOME, busy = false, message = null, pcs = store.list())
    }

    fun startPairing() {
        val my = stopCurrent()
        ui = ui.copy(screen = Screen.PAIRING, busy = true, pairCode = null, message = UiText(R.string.pair_searching))
        thread(name = "TabSpanPair") { pairLoop(my) }
    }

    fun cancelPairing() = cancel()

    fun setOverlay(on: Boolean) {
        prefs.edit().putBoolean(KEY_OVERLAY, on).apply()
        ui = ui.copy(showOverlay = on)
    }

    // ---- 設定の画面(A5) ----

    /** 設定を開く間は、つなぐ途中の流れを止める(裏でつながって画面が切り替わらないように)。 */
    fun openSettings() {
        stopCurrent()
        ui = ui.copy(screen = Screen.SETTINGS, busy = false, message = null, pcs = store.list())
    }

    fun closeSettings() {
        ui = ui.copy(screen = Screen.HOME, busy = false, message = null, pcs = store.list())
    }

    fun setAutoConnect(on: Boolean) {
        prefs.edit().putBoolean(KEY_AUTO_CONNECT, on).apply()
        ui = ui.copy(autoConnect = on)
    }

    /** 手で入れたアドレス。空なら消す。IPv4 の形でなければ偽を返し、保存しない。 */
    fun setManualHost(pcInstanceId: String, text: String): Boolean {
        val host = if (text.isBlank()) null else Ipv4.normalize(text) ?: return false
        store.update(pcInstanceId) { it.copy(manualHost = host) }
        ui = ui.copy(pcs = store.list())
        return true
    }

    /** 端末側の行(指紋とトークン)を消す(FR-4)。 */
    fun unpair(pcInstanceId: String) {
        store.remove(pcInstanceId)
        AppLog.i("unpaired a PC")
        ui = ui.copy(pcs = store.list())
    }

    fun setMenu(open: Boolean) {
        if (open) releaseTouches()
        ui = ui.copy(menuOpen = open)
    }

    fun setDeviceTouch(on: Boolean) {
        if (!on) releaseTouches()
        prefs.edit().putBoolean(KEY_DEVICE_TOUCH, on).apply()
        ui = ui.copy(deviceTouch = on)
    }

    /** 映像の画面の MotionEvent。座標は [rect] と同じ面(映像の画面の左上が 0)。変わった指を 1 つの枠にまとめて送る。 */
    fun onTouch(e: MotionEvent, rect: FitRect) {
        // 映像の大きさ(StreamConfig)が来るまでは矩形が決まらないので送らない
        if (!(ui.screen == Screen.VIDEO && streaming && ui.videoWidth > 0 && ui.pcTouch && ui.deviceTouch && !ui.menuOpen)) {
            releaseTouches()
            return
        }
        if (e.actionMasked == MotionEvent.ACTION_CANCEL) {
            releaseTouches()
            return
        }
        val index = e.actionIndex
        val changed = ArrayList<TouchMapper.Contact>()
        for (i in 0 until e.pointerCount) {
            val id = e.getPointerId(i)
            val x = e.getX(i)
            val y = e.getY(i)
            val c = when {
                i != index -> touch.move(id, x, y, rect)
                e.actionMasked == MotionEvent.ACTION_DOWN || e.actionMasked == MotionEvent.ACTION_POINTER_DOWN -> touch.down(id, x, y, rect)
                e.actionMasked == MotionEvent.ACTION_UP || e.actionMasked == MotionEvent.ACTION_POINTER_UP -> touch.up(id, x, y, rect)
                else -> touch.move(id, x, y, rect)
            }
            c?.let(changed::add)
        }
        sendTouch(changed)
    }

    fun surfaceChanged(surface: Surface?) = decoder.setSurface(surface)

    // ---- 接続 ----

    private fun sessionLoop(my: Int) {
        var since = SystemClock.elapsedRealtime()
        var lost = false
        while (alive(my)) {
            val waited = SystemClock.elapsedRealtime() - since
            if (waited > GIVE_UP_MS) {
                toHome(my, UiText(R.string.status_not_found))
                return
            }
            val pcs = store.list()
            if (pcs.isEmpty()) {
                toHome(my, null)
                return
            }
            if (lost) update(my) { it.copy(screen = Screen.HOME, busy = true, message = UiText(R.string.status_reconnecting, (waited / 1000).toInt())) }
            val result = runRound(my, pcs, lost)
            if (result.stop) {
                toHome(my, result.message)
                return
            }
            if (result.streamed) {
                lost = true
                since = SystemClock.elapsedRealtime()
            }
            // 切れたらすぐホームへ戻す(一時的な断りなら、その理由を出す)
            val shown = result.message ?: if (result.streamed) UiText(R.string.status_reconnecting, 0) else null
            shown?.let { m -> update(my) { it.copy(screen = Screen.HOME, busy = true, message = m) } }
            sleep(RETRY_MS)
        }
    }

    private class Outcome(val stop: Boolean, val message: UiText?, val streamed: Boolean)

    /** つなぎ方の順(FR-6)に 1 巡試す。どれかでつながったら、そのセッションの終わりまで戻らない。 */
    private fun runRound(my: Int, pcs: List<PcEntry>, lost: Boolean): Outcome {
        var mismatch = false
        for (a in ConnectOrder.plan(pcs, lanAvailable())) {
            if (!alive(my)) break
            if (!lost) update(my) { it.copy(message = UiText(routeText(a.route))) }
            val opened = try {
                open(my, a, pcs)
            } catch (e: FingerprintMismatchException) {
                // USB は相手が adb の先の PC と決まっているので、鍵が変わった(止める)。LAN は古いアドレスや認証の無い
                // UDP の返事の先かもしれないので、文だけ出してつなぎ直しは続ける
                AppLog.w("server certificate does not match the paired PC (${a.route})")
                if (a.route == Route.USB) return Outcome(true, UiText(R.string.reason_key_changed), false)
                mismatch = true
                null
            } ?: continue
            return runSession(my, opened, pcs)
        }
        return Outcome(false, if (mismatch) UiText(R.string.reason_key_changed) else null, false)
    }

    /** [lanHost] は LAN でつないだ時の相手の IPv4(USB は null)。 */
    private class Opened(val sock: LinkSocket, val lanHost: String?, val port: Int)

    private fun open(my: Int, a: Attempt, pcs: List<PcEntry>): Opened? = try {
        when (a.route) {
            Route.USB -> Opened(LinkSocket.connect(a.port, pcs.map { it.fingerprint }.toSet(), SILENCE_MS), null, a.port)
            Route.LAST, Route.MANUAL -> {
                val pc = pcs.first { it.pcInstanceId == a.pcInstanceId }
                val host = InetAddress.getByAddress(Ipv4.parse(a.host!!))
                Opened(LinkSocket.connect(a.port, setOf(pc.fingerprint), SILENCE_MS, host), a.host, a.port)
            }
            Route.DISCOVER -> Discovery.search(a.port, store.deviceId, pcs.map { it.pcInstanceId }.toSet()) { alive(my) }?.let { f ->
                val pc = pcs.first { it.pcInstanceId == f.pcInstanceId }
                AppLog.i("found PC at ${f.host.hostAddress}:${f.tcpPort}")
                Opened(LinkSocket.connect(f.tcpPort, setOf(pc.fingerprint), SILENCE_MS, f.host), f.host.hostAddress, f.tcpPort)
            }
        }
    } catch (e: FingerprintMismatchException) {
        throw e
    } catch (e: Exception) {
        AppLog.i("connect failed (${a.route}): ${e.javaClass.simpleName}")
        null
    }

    private fun runSession(my: Int, opened: Opened, pcs: List<PcEntry>): Outcome {
        val sock = opened.sock
        if (!alive(my)) {
            sock.close()
            return Outcome(false, null, false)
        }
        socket = sock
        val pc = pcs.first { it.fingerprint == sock.fingerprint }
        var streamed = false
        var tick: ScheduledFuture<*>? = null
        try {
            sock.send(MessageType.HELLO, Messages.toJson(hello(pc.token)))
            while (true) {
                val frame = sock.read() ?: return Outcome(false, null, streamed)
                val arriveUs = nowUs()
                when (frame.type) {
                    MessageType.HELLO_ACK -> {
                        val ack = Messages.parseHelloAck(frame.payload)
                        if (!ack.ok) return refused(ack.reason, ack.appVersion, pc, streamed)
                        streamed = true
                        streaming = true
                        // 名前と、LAN でつながったアドレス(前回のアドレスとして PC ごとに覚える)とポート
                        if (ack.pcName != pc.pcName || opened.port != pc.port || (opened.lanHost != null && opened.lanHost != pc.lastHost)) {
                            store.update(pc.pcInstanceId) { it.copy(pcName = ack.pcName, port = opened.port, lastHost = opened.lanHost ?: it.lastHost) }
                        }
                        AppLog.i("session started (${if (opened.lanHost == null) "USB" else "LAN ${opened.lanHost}"})")
                        tick = startTick(my, sock)
                        main.post { if (alive(my)) touch.cancelAll() } // 前の接続で触れていた指を忘れる
                        update(my) {
                            it.copy(
                                screen = Screen.VIDEO, busy = false, message = null, videoWidth = 0, videoHeight = 0,
                                firstFrame = false, paused = false, fps = 0, latencyMs = 0, dropped = 0,
                                menuOpen = false, pcTouch = ack.touchEnabled,
                            )
                        }
                    }
                    MessageType.STREAM_CONFIG -> {
                        val c = Messages.parseStreamConfig(frame.payload)
                        AppLog.i("stream ${c.width}x${c.height}@${c.fps} ${c.profile} ${c.sourceKind}")
                        decoder.configure(c.width, c.height, Base64Codec.decode(c.sps), Base64Codec.decode(c.pps))
                        update(my) { it.copy(videoWidth = c.width, videoHeight = c.height, firstFrame = false, paused = false) }
                    }
                    MessageType.VIDEO_FRAME -> {
                        val v = Messages.parseVideoFrame(frame.payload)
                        decoder.queue(v.seq, v.key, v.payload, v.dataOffset, v.dataLength, arriveUs)
                    }
                    MessageType.PING -> sock.send(MessageType.PONG, Messages.pong(Messages.parsePing(frame.payload), nowUs()))
                    MessageType.BYE -> return refused(Messages.parseReason(frame.payload).reason, null, pc, streamed)
                    MessageType.NOTICE -> {
                        val n = Messages.parseNotice(frame.payload)
                        when (n.kind) {
                            "paused" -> update(my) { it.copy(paused = n.value == true) }
                            "touchEnabled" -> {
                                val on = n.value == true
                                main.post {
                                    if (alive(my)) {
                                        if (!on) releaseTouches()
                                        ui = ui.copy(pcTouch = on)
                                    }
                                }
                            }
                        }
                    }
                    else -> AppLog.i("skipped frame type ${frame.type}")
                }
            }
        } catch (e: SocketTimeoutException) {
            AppLog.i("no frame for ${SILENCE_MS / 1000} s")
            return Outcome(false, null, streamed)
        } catch (e: Exception) {
            // 枠が大きすぎる・JSON が壊れている・切れた: どれも閉じてつなぎ直す
            if (alive(my)) AppLog.i("session ended: ${e.javaClass.simpleName}")
            return Outcome(false, null, streamed)
        } finally {
            tick?.cancel(false)
            sock.close()
            if (socket === sock) socket = null
            // 止められた古い流れは、次の流れの物(streaming・デコーダー)に触らない
            if (alive(my)) {
                streaming = false
                decoder.stop()
            }
        }
    }

    private fun refused(reason: String?, pcVersion: String?, pc: PcEntry, streamed: Boolean): Outcome {
        AppLog.i("refused: $reason")
        val text = when (reason) {
            // 一時的な断り: 理由を出したまま、つなぎ直しを続ける
            "busy" -> return Outcome(false, UiText(R.string.reason_busy), streamed)
            "no_source" -> return Outcome(false, UiText(R.string.reason_no_source), streamed)
            "shutdown" -> return Outcome(false, null, streamed)
            "unpaired" -> {
                store.remove(pc.pcInstanceId)
                UiText(R.string.reason_auth)
            }
            "auth" -> UiText(R.string.reason_auth)
            "version" -> UiText(R.string.reason_version, pcVersion ?: "?", BuildConfig.VERSION_NAME)
            "blocked" -> UiText(R.string.reason_blocked)
            "decoder" -> UiText(R.string.reason_decoder)
            "user" -> null
            else -> UiText(R.string.reason_other, reason ?: "?")
        }
        return Outcome(true, text, streamed)
    }

    private fun hello(token: String) = HelloMsg(
        PROTOCOL_VERSION, store.deviceId, token, Build.MODEL, BuildConfig.VERSION_NAME, screenInfo, decoderInfo,
    )

    // ---- 1 秒ごと: Stats・重ね表示・取りこぼした RequestKeyframe ----

    private fun startTick(my: Int, sock: LinkSocket): ScheduledFuture<*> {
        worker.execute {
            droppedTotal = 0
            keyPending = false
        }
        return worker.scheduleWithFixedDelay({
            val s = decoder.takeStats()
            try {
                sock.send(
                    MessageType.STATS,
                    Messages.toJson(StatsMsg(s.decoded, s.dropped, s.decoderErrors, s.frames.map { FrameTiming(it[0], it[1], it[2]) })),
                )
                if (keyPending) sendKeyRequest()
            } catch (e: Exception) {
                // 切れたら読みの側が気づいて片付ける
            }
            droppedTotal += s.dropped
            val delays = s.frames.map { it[2] - it[1] }.sorted()
            val p50 = if (delays.isEmpty()) 0 else (delays[delays.size / 2] / 1000).toInt()
            val total = droppedTotal
            update(my) { it.copy(fps = s.decoded, latencyMs = p50, dropped = total, firstFrame = it.firstFrame || s.decoded > 0) }
        }, 1, 1, TimeUnit.SECONDS)
    }

    /** デコーダーのスレッドから呼ばれる。送りは worker で、1 秒に 1 回まで(多い分は次の刻みに回す)。 */
    private fun requestKeyframe() {
        worker.execute { if (streaming) sendKeyRequest() }
    }

    private fun sendKeyRequest() {
        val now = SystemClock.elapsedRealtime()
        if (now - lastKeyRequestMs < 1000) {
            keyPending = true
            return
        }
        keyPending = false
        lastKeyRequestMs = now
        try {
            socket?.send(MessageType.REQUEST_KEYFRAME)
        } catch (e: Exception) {
            // 切れたら読みの側が気づく
        }
    }

    // ---- タッチ(メインスレッドから) ----

    /** 触れている指を cancel にして送り(つながっていれば)、忘れる。 */
    private fun releaseTouches() = sendTouch(touch.cancelAll())

    private fun sendTouch(contacts: List<TouchMapper.Contact>) {
        if (contacts.isEmpty()) return
        val payload = TouchMapper.encode(contacts)
        worker.execute {
            if (streaming) {
                try {
                    socket?.send(MessageType.TOUCH, payload)
                } catch (e: Exception) {
                    // 切れたら読みの側が気づく
                }
            }
        }
    }

    // ---- ペアリング ----

    private fun pairLoop(my: Int) {
        val start = SystemClock.elapsedRealtime()
        var sock: LinkSocket? = null
        while (alive(my) && sock == null) {
            try {
                sock = LinkSocket.connect(LinkSocket.PORT, null, PAIR_TIMEOUT_MS)
            } catch (e: Exception) {
                if (SystemClock.elapsedRealtime() - start > PAIR_CONNECT_MS) {
                    update(my) { it.copy(busy = false, message = UiText(R.string.pair_no_connection)) }
                    return
                }
                sleep(1000)
            }
        }
        if (sock == null) return
        if (!alive(my)) {
            sock.close()
            return
        }
        socket = sock
        try {
            val deviceId = store.deviceId
            val nonce = ByteArray(16).also { SecureRandom().nextBytes(it) }
            sock.send(MessageType.PAIR_REQUEST, Messages.toJson(PairRequestMsg(PROTOCOL_VERSION, deviceId, Build.MODEL, Base64Codec.encode(nonce))))
            var challenge: PairChallengeMsg? = null
            while (alive(my)) {
                val frame = sock.read() ?: throw EOFException()
                when (frame.type) {
                    MessageType.PAIR_CHALLENGE -> {
                        val ch = Messages.parsePairChallenge(frame.payload)
                        challenge = ch
                        val code = PairingCode.compute(sock.certDer, deviceId, nonce, Base64Codec.decode(ch.serverNonce))
                        update(my) { it.copy(pairCode = code, message = null) }
                    }
                    MessageType.PAIR_ACCEPT -> {
                        val ch = challenge ?: throw ProtocolException("PairAccept before PairChallenge")
                        val token = Messages.parsePairAccept(frame.payload).token
                        store.save(PcEntry(ch.pcInstanceId, ch.pcName, sock.fingerprint, token, pairedAt = System.currentTimeMillis()))
                        AppLog.i("paired")
                        val pcs = store.list()
                        update(my) { it.copy(busy = false, pairCode = null, message = UiText(R.string.pair_done, ch.pcName), pcs = pcs) }
                        sock.close()
                        sleep(1000)
                        main.post { if (alive(my)) connect() }
                        return
                    }
                    MessageType.PAIR_REJECT, MessageType.BYE -> {
                        val reason = Messages.parseReason(frame.payload).reason
                        AppLog.i("pairing refused: $reason")
                        update(my) { it.copy(busy = false, pairCode = null, message = pairReasonText(reason)) }
                        return
                    }
                    else -> AppLog.i("skipped frame type ${frame.type}")
                }
            }
        } catch (e: Exception) {
            if (alive(my)) {
                AppLog.i("pairing failed: ${e.javaClass.simpleName}")
                update(my) { it.copy(busy = false, pairCode = null, message = UiText(R.string.pair_failed)) }
            }
        } finally {
            sock.close()
            if (socket === sock) socket = null
        }
    }

    private fun pairReasonText(reason: String) = UiText(
        when (reason) {
            "usb_only" -> R.string.pair_usb_only
            "not_accepting" -> R.string.pair_not_accepting
            "full" -> R.string.pair_full
            "busy" -> R.string.pair_busy
            else -> R.string.pair_denied
        },
    )

    // ---- 下回り ----

    /** 今の流れを止め、新しい世代の番号を返す。映していたら Bye(user) を送ってから閉じる。 */
    private fun stopCurrent(): Int {
        val my = ++gen
        val cancels = touch.cancelAll()
        decoder.stop()
        val s = socket
        val wasStreaming = streaming
        socket = null
        streaming = false
        if (s != null) {
            worker.execute {
                if (wasStreaming) {
                    try {
                        if (cancels.isNotEmpty()) s.send(MessageType.TOUCH, TouchMapper.encode(cancels))
                        s.send(MessageType.BYE, Messages.toJson(ReasonMsg("user")))
                    } catch (e: Exception) {
                        // もう切れている
                    }
                }
                s.close()
            }
        }
        return my
    }

    private fun alive(my: Int) = my == gen

    private fun update(my: Int, change: (UiState) -> UiState) {
        main.post { if (alive(my)) ui = change(ui) }
    }

    private fun toHome(my: Int, message: UiText?) {
        val pcs = store.list()
        update(my) { it.copy(screen = Screen.HOME, busy = false, message = message, pcs = pcs) }
    }

    private fun routeText(route: Route) = when (route) {
        Route.USB -> R.string.status_route_usb
        Route.LAST -> R.string.status_route_last
        Route.MANUAL -> R.string.status_route_manual
        Route.DISCOVER -> R.string.status_route_search
    }

    /** つながっているネットワークがあるか(無ければ LAN の候補を飛ばす。USB だけのときに 3 秒の待ちを重ねない)。 */
    @Suppress("DEPRECATION")
    private fun lanAvailable(): Boolean = connectivity?.activeNetworkInfo?.isConnected == true

    private fun sleep(ms: Long) {
        try {
            Thread.sleep(ms)
        } catch (_: InterruptedException) {
        }
    }

    private fun readDecoderInfo(): DecoderInfo {
        val info = AvcCaps.pick() ?: return DecoderInfo(emptyList(), emptyList())
        AppLog.i("decoder ${info.name}")
        return DecoderInfo(AvcCaps.profiles(info), AvcCaps.supported(info).map { SizeFps(it.width, it.height, it.fps) })
    }

    companion object {
        private const val KEY_OVERLAY = "showOverlay"
        private const val KEY_AUTO_CONNECT = "autoConnect"
        private const val KEY_DEVICE_TOUCH = "deviceTouch"
        private const val SILENCE_MS = 5_000
        private const val RETRY_MS = 2_000L
        private const val GIVE_UP_MS = 5 * 60_000L
        private const val PAIR_TIMEOUT_MS = 130_000 // PC の受け付け 120 秒より少し長く待つ
        private const val PAIR_CONNECT_MS = 10_000L

        @Volatile private var instance: Connector? = null

        fun get(context: Context): Connector = instance ?: synchronized(this) {
            instance ?: Connector(context.applicationContext).also { instance = it }
        }

        /** 端末と同じ時計(PC へ返す t2・到着の時刻・デコーダーの時刻はすべてこれ)。 */
        fun nowUs() = System.nanoTime() / 1000

        private fun landscapeScreen(context: Context): ScreenInfo {
            val metrics = DisplayMetrics()
            context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY).getRealMetrics(metrics)
            return ScreenInfo(
                maxOf(metrics.widthPixels, metrics.heightPixels), minOf(metrics.widthPixels, metrics.heightPixels), metrics.densityDpi,
            )
        }
    }
}
