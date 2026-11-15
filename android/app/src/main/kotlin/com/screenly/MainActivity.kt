package com.screenly

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
// USB-only: no mDNS discovery
import com.screenly.net.ClientMsg
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
    // USB-only: always connect to localhost (adb reverse)
    var connected by remember { mutableStateOf(false) }
    var streamW by remember { mutableIntStateOf(1920) }
    var streamH by remember { mutableIntStateOf(1080) }
    val surfaceView = remember { SurfaceView(activity) }
    val decoder = remember { VideoDecoder(surfaceView.holder) }
    DisposableEffect(Unit) { onDispose { decoder.release() } }

    LaunchedEffect(Unit) {
        // USB-only: auto-connect to localhost (adb reverse tcp:7543 tcp:7543)
        launch {
            kotlinx.coroutines.delay(500)
            val host = "127.0.0.1"
            if (client.connect(host, 7543)) {
                val dm = activity.resources.displayMetrics
                client.send(Hello(deviceName = android.os.Build.MODEL, screenWidth = dm.widthPixels, screenHeight = dm.heightPixels, densityDpi = dm.densityDpi))
            }
        }
        launch {
            client.events.collect { ev ->
                when (ev) {
                    is ServerEvent.HelloAcked -> {
                        connected = true
                        // Persist the connected host
                        // (host IP is not in ack; store from the connect call is handled separately)
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
                                    canvas.drawBitmap(bmp, null, android.graphics.Rect(0, 0, surfaceView.width.coerceAtLeast(1), surfaceView.height.coerceAtLeast(1)), null)
                                    surfaceView.holder.unlockCanvasAndPost(canvas)
                                }
                                bmp.recycle()
                            }
                        } else {
                            decoder.configure(ev.w, ev.h)
                            decoder.decode(ev.nal, ev.tsUs, ev.keyframe)
                        }
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
        if (!connected) {
            Column(Modifier.fillMaxSize().padding(16.dp).verticalScroll(rememberScrollState()).imePadding()) {
                Text("Screenly", style = MaterialTheme.typography.headlineMedium)
                Spacer(Modifier.height(8.dp))
                Text("USB mode: connect phone to PC via USB.")
                Text("The app connects automatically.")
                Spacer(Modifier.height(12.dp))
                Text("Connecting to 127.0.0.1…")
            }
        } else {
            AndroidView(factory = { _ ->
                val tc = TouchCapture(client, { streamW }, { streamH })
                surfaceView.setOnTouchListener { _, ev -> tc.handle(ev, surfaceView.width, surfaceView.height); true }
                surfaceView
            }, modifier = Modifier.fillMaxSize())
        }
    }
}
