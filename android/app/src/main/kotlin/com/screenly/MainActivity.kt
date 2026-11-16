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
import androidx.compose.ui.Alignment
import androidx.compose.foundation.background
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
// USB-only: no mDNS discovery
import com.screenly.net.Hello
import com.screenly.net.ScreenlyClient
import com.screenly.net.ServerEvent
import com.screenly.service.ConnectionService
import com.screenly.input.TouchCapture
import com.screenly.video.VideoDecoder
import com.screenly.video.DisplayGeometry
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private lateinit var insetsController: WindowInsetsControllerCompat

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // Edge-to-edge: content extends under system bars
        WindowCompat.setDecorFitsSystemWindows(window, false)
        insetsController = WindowInsetsControllerCompat(window, window.decorView)
        // Immersive sticky: hide system bars, reappear on swipe then auto-hide
        insetsController.hide(WindowInsetsCompat.Type.systemBars())
        insetsController.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        setContent { ScreenlyApp(this) }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            // Re-apply immersive on focus change (bars can reappear on rotation/app switch)
            insetsController.hide(WindowInsetsCompat.Type.systemBars())
            insetsController.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        try { insetsController.show(WindowInsetsCompat.Type.systemBars()) } catch (_: Exception) { }
    }
}

@Composable
fun ScreenlyApp(activity: Activity) {
    val client = remember { ScreenlyClient() }
    var connected by remember { mutableStateOf(false) }
    var streamW by remember { mutableIntStateOf(1920) }
    var streamH by remember { mutableIntStateOf(1080) }
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
                                    val content = DisplayGeometry.contentRect(vw, vh, bmp.width, bmp.height)
                                    canvas.drawColor(android.graphics.Color.BLACK)
                                    canvas.drawBitmap(bmp, null, android.graphics.RectF(content), null)
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
                        // Cursor is composited into frames; ignore position updates
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
        BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black)) {
            // Video fills the entire window (edge-to-edge)
            val sourceAspect = streamW.toFloat() / streamH.coerceAtLeast(1)
            val containerAspect = maxWidth.value / maxHeight.value.coerceAtLeast(1f)
            val videoWidth: androidx.compose.ui.unit.Dp
            val videoHeight: androidx.compose.ui.unit.Dp
            if (containerAspect > sourceAspect) {
                videoHeight = maxHeight
                videoWidth = maxHeight * sourceAspect
            } else {
                videoWidth = maxWidth
                videoHeight = maxWidth / sourceAspect
            }
            AndroidView(factory = { _ ->
                val tc = TouchCapture(client, { streamW }, { streamH })
                surfaceView.setOnTouchListener { _, ev -> tc.handle(ev, surfaceView.width, surfaceView.height); true }
                surfaceView
            }, modifier = Modifier.size(videoWidth, videoHeight).align(Alignment.Center))
            // Connecting overlay with inset padding (not covering video)
            if (!connected) {
                Column(Modifier.fillMaxSize().padding(16.dp).windowInsetsPadding(WindowInsets.systemBars)) {
                    Text("Screenly", style = MaterialTheme.typography.headlineMedium)
                    Spacer(Modifier.height(8.dp))
                    Text("USB mode: connect phone to PC via USB.")
                    Text("Connecting...")
                }
            }
        }
    }
}