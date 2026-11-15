package com.screenly

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.drawIntoCanvas
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.foundation.Canvas
import com.screenly.net.Hello
import com.screenly.net.ScreenlyClient
import com.screenly.net.ServerEvent
import com.screenly.service.ConnectionService
import com.screenly.input.TouchCapture
import com.screenly.video.VideoDecoder
import com.screenly.video.DisplayGeometry
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
    var cursorBitmap by remember { mutableStateOf<android.graphics.Bitmap?>(null) }
    var cursorHotspotX by remember { mutableIntStateOf(0) }
    var cursorHotspotY by remember { mutableIntStateOf(0) }
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
                    val connected_ok = client.connect(host, 7543)
                    if (connected_ok) {
                        val dm = activity.resources.displayMetrics
                        client.send(Hello(deviceName = android.os.Build.MODEL, screenWidth = dm.widthPixels, screenHeight = dm.heightPixels, densityDpi = dm.densityDpi))
                        kotlinx.coroutines.withTimeoutOrNull(5000) {
                            while (!connected) kotlinx.coroutines.delay(100)
                        }
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
                        cursorX = ev.x
                        cursorY = ev.y
                        cursorVisible = ev.visible
                    }
                    is ServerEvent.Disconnected -> {
                        connected = false
                        client.close()
                        activity.stopService(Intent(activity, ConnectionService::class.java))
                    }
                    is ServerEvent.CursorShape -> {
                        val bmp = android.graphics.BitmapFactory.decodeByteArray(ev.pngData, 0, ev.pngData.size)
                        if (bmp != null) {
                            cursorBitmap = bmp
                            cursorHotspotX = ev.hotspotX
                            cursorHotspotY = ev.hotspotY
                        }
                    }
                    else -> {}
                }
            }
        }
    }

    MaterialTheme {
        BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black)) {
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
            Box(Modifier.size(videoWidth, videoHeight).align(Alignment.Center)) {
                AndroidView(factory = { _ ->
                    val tc = TouchCapture(client, { streamW }, { streamH })
                    surfaceView.setOnTouchListener { _, ev -> tc.handle(ev, surfaceView.width, surfaceView.height); true }
                    surfaceView
                }, modifier = Modifier.fillMaxSize())
                Canvas(modifier = Modifier.fillMaxSize()) {
                    if (!cursorVisible) return@Canvas
                    val viewW = size.width.toInt().coerceAtLeast(1)
                    val viewH = size.height.toInt().coerceAtLeast(1)
                    val content = DisplayGeometry.contentRect(viewW, viewH, streamW, streamH)
                    val (vx, vy) = DisplayGeometry.streamToView(cursorX, cursorY, content, streamW, streamH)
                    val bmp = cursorBitmap
                    drawIntoCanvas { canvas ->
                        val native = canvas.nativeCanvas
                        if (bmp != null) {
                            val scale = if (streamW > 0) content.width() / streamW else 1f
                            val left = vx - cursorHotspotX * scale
                            val top = vy - cursorHotspotY * scale
                            val dst = android.graphics.RectF(left, top, left + bmp.width * scale, top + bmp.height * scale)
                            native.drawBitmap(bmp, null, dst, null)
                        } else {
                            val paint = android.graphics.Paint().apply {
                                color = android.graphics.Color.YELLOW
                                style = android.graphics.Paint.Style.FILL
                            }
                            native.drawCircle(vx, vy, 12f, paint)
                            val crossPaint = android.graphics.Paint().apply {
                                color = android.graphics.Color.RED
                                strokeWidth = 3f
                            }
                            native.drawLine(vx - 15f, vy, vx + 15f, vy, crossPaint)
                            native.drawLine(vx, vy - 15f, vx, vy + 15f, crossPaint)
                        }
                    }
                }
            }
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
