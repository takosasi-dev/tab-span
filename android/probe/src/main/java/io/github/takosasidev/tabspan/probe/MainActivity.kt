// probe の画面と入口。起動で調べ物を走らせ、「TLS のテスト」ボタンと adb の intent(--es action tls --ei port N)で TLS を試す。
// 仕事は 1 本の裏のスレッドに順に並べ、出た行は画面と logcat(TabSpanProbe)の両方に出す。並んだ仕事が全部はけたら「--- 終わり ---」。
package io.github.takosasidev.tabspan.probe

import android.app.Activity
import android.content.Intent
import android.graphics.Typeface
import android.os.Bundle
import android.text.InputType
import android.util.Log
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

class MainActivity : Activity() {
    companion object {
        const val TAG = "TabSpanProbe"
    }

    private val worker: ExecutorService = Executors.newSingleThreadExecutor()
    private val pending = AtomicInteger()
    private lateinit var output: TextView
    private lateinit var portInput: EditText

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pad = (12 * resources.displayMetrics.density).toInt()
        portInput = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            setText(ProbeText.DEFAULT_PORT.toString())
            hint = "ポート"
        }
        val button = Button(this).apply {
            text = "TLS のテスト"
            setOnClickListener { onTlsButton() }
        }
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            addView(portInput, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
            addView(button, LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT))
        }
        output = TextView(this).apply {
            setTextIsSelectable(true)
            typeface = Typeface.MONOSPACE
            textSize = 12f
        }
        val scroll = ScrollView(this).apply { addView(output) }
        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            fitsSystemWindows = true
            setPadding(pad, pad, pad, pad)
            addView(row, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
            addView(scroll, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
        })

        // 作り直し(savedInstanceState あり)では、残った intent から TLS を出し直さない。
        // PC の待ち受けは 60 秒だけなので、TLS を頼まれていれば調べ物より先に並べる
        if (savedInstanceState == null) handleIntent(intent)
        @Suppress("DEPRECATION") // getDisplay() は API 30。API 24 では defaultDisplay を使う
        val display = windowManager.defaultDisplay
        enqueue { DeviceSurvey.run(display, it) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIntent(intent)
    }

    override fun onDestroy() {
        worker.shutdown()
        super.onDestroy()
    }

    private fun handleIntent(intent: Intent?) {
        if (intent?.getStringExtra("action") != "tls") return
        val port = intent.getIntExtra("port", ProbeText.DEFAULT_PORT)
        if (!ProbeText.isValidPort(port)) {
            enqueue { it("intent の port が範囲外です: $port (1〜65535)") } // 終わりの行も出すため並べる
            return
        }
        portInput.setText(port.toString())
        enqueue { TlsTest.run(port, it) }
    }

    private fun onTlsButton() {
        val port = ProbeText.parsePort(portInput.text.toString())
        if (port == null) {
            val text = portInput.text.toString()
            enqueue { it("ポートは 1〜65535 の整数にしてください: $text") }
            return
        }
        enqueue { TlsTest.run(port, it) }
    }

    private fun enqueue(job: ((String) -> Unit) -> Unit) {
        pending.incrementAndGet()
        worker.execute {
            try {
                job(::emit)
            } catch (e: Exception) {
                emit("途中で例外: ${e.javaClass.name}: ${e.message}")
            } finally {
                if (pending.decrementAndGet() == 0) emit("--- 終わり ---")
            }
        }
    }

    private fun emit(line: String) {
        Log.i(TAG, line)
        runOnUiThread { output.append(line + "\n") }
    }
}
