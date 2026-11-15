package com.screenly

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
// USB-only: no mDNS discovery
import com.screenly.net.Hello
import com.screenly.net.ScreenlyClient
import com.screenly.net.ServerEvent
import com.screenly.service.ConnectionService
import com.screenly.input.TouchCapture
import com.screenly.video.VideoDecoder
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContent { ScreenlyApp(this) }
    }
}

@Composable
fun ScreenlyApp(activity: Activity) {
    val client = remember { ScreenlyClient() }
    var connected by remember { mutableStateOf(false) }
    var streamW by remember { mutableIntStateOf(1920) }
    var streamH by remember { mutableIntStateOf(1080) }
    var cursorX by remember { mutableIntStateOf(0) }
    var cursorY by remember { mutableIntStateOf(0) }
    var cursorVisible by remember { mutableStateOf(false) }
    val surfaceView = remember { SurfaceView(activity) }
    val decoder = remember { VideoDecoder(surfaceView.holder) }
    DisposableEffect(Unit) { onDispose { decoder.release() } }

    LaunchedEffect(Unit) {
        var firstAttempt = true
        launch {
            while (true) {
                if (!connected) {
                    if (!firstAttempt) kotlinx.coroutines.delay(2000)
                    firstAttempt = false
                    val host = "127.0.0.1"
                    if (client.connect(host, 7543)) {
                        val dm = activity.resources.displayMetrics
                        client.send(Hello(deviceName = android.os.Build.MODEL, screenWidth = dm.widthPixels, screenHeight = dm.heightPixels, densityDpi = dm.densityDpi))
                    }
                } else {
                    kotlinx.coroutines.delay(1000)
                }
            }
        }
        launch {
            client.events.collect { ev ->
                when (ev) {
                    is ServerEvent.HelloAcked -> {
                        connected = true
                        ev.ack.config?.let { streamW = it.width; streamH = it.height }
                        activity.startService(Intent(activity, ConnectionService::class.java))
                    }
                    is ServerEvent.Frame -> {
                        if (ev.w != streamW || ev.h != streamH) { streamW = ev.w; streamH = ev.h }
                        if (ev.isJpeg) {
                            val bmp = android.graphics.BitmapFactory.decodeByteArray(ev.nal, 0, ev.nal.size)
                            if (bmp != null) {
                                val canvas = try { surfaceView.holder.lockCanvas() } catch (_: Exception) { null }
                                if (canvas != null) {
                                    val vw = surfaceView.width.coerceAtLeast(1)
                                    val vh = surfaceView.height.coerceAtLeast(1)
                                    val bw = bmp.width
                                    val bh = bmp.height
                                    val scale = minOf(vw.toFloat() / bw, vh.toFloat() / bh)
                                    val dw = (bw * scale).toInt()
                                    val dh = (bh * scale).toInt()
                                    val dx = (vw - dw) / 2
                                    val dy = (vh - dh) / 2
                                    canvas.drawColor(android.graphics.Color.BLACK)
                                    canvas.drawBitmap(bmp, null, android.graphics.Rect(dx, dy, dx + dw, dy + dh), null)
                                    // Draw cursor overlay if visible
                                    if (cursorVisible) {
                                        val cx = dx + (cursorX * scale).toInt()
                                        val cy = dy + (cursorY * scale).toInt()
                                        val paint = android.graphics.Paint().apply { color = android.graphics.Color.YELLOW; style = android.graphics.Paint.Style.FILL }
                                        canvas.drawCircle(cx.toFloat(), cy.toFloat(), 12f, paint)
                                        val crossPaint = android.graphics.Paint().apply { color = android.graphics.Color.RED; strokeWidth = 3f }
                                        canvas.drawLine((cx - 15).toFloat(), cy.toFloat(), (cx + 15).toFloat(), cy.toFloat(), crossPaint)
                                        canvas.drawLine(cx.toFloat(), (cy - 15).toFloat(), cx.toFloat(), (cy + 15).toFloat(), crossPaint)
                                    }
                                    surfaceView.holder.unlockCanvasAndPost(canvas)
                                }
                                bmp.recycle()
                            }
                        } else {
                            decoder.configure(ev.w, ev.h)
                            decoder.decode(ev.nal, ev.tsUs, ev.keyframe)
                        }
                    }
                    is ServerEvent.CursorPos -> {
                        cursorX = ev.x
                        cursorY = ev.y
                        cursorVisible = ev.visible
                    }
                    is ServerEvent.Disconnected -> {
                        connected = false
                        client.close()
                        activity.stopService(Intent(activity, ConnectionService::class.java))
                    }
                    else -> {}
                }
            }
        }
    }

    MaterialTheme {
        Box(Modifier.fillMaxSize()) {
            AndroidView(factory = { _ ->
                val tc = TouchCapture(client, { streamW }, { streamH })
                surfaceView.setOnTouchListener { _, ev -> tc.handle(ev, surfaceView.width, surfaceView.height); true }
                surfaceView
            }, modifier = Modifier.fillMaxSize())
            if (!connected) {
                Column(Modifier.fillMaxSize().padding(16.dp)) {
                    Text("Screenly", style = MaterialTheme.typography.headlineMedium)
                    Spacer(Modifier.height(8.dp))
                    Text("USB mode: connect phone to PC via USB.")
                    Text("Connecting...")
                }
            }
        }
    }
}