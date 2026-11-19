package com.screenly

import android.app.Activity
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.os.Bundle
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
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
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.concurrent.Executors

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
            insetsController.hide(WindowInsetsCompat.Type.systemBars())
            insetsController.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        insetsController.hide(WindowInsetsCompat.Type.systemBars())
        insetsController.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
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
    var firstFrameReceived by remember { mutableStateOf(false) }
    var lastAppliedOrientation by remember { mutableStateOf<Int?>(null) }
    var streamW by remember { mutableIntStateOf(1920) }
    var streamH by remember { mutableIntStateOf(1080) }
    var cursorX by remember { mutableIntStateOf(0) }
    var cursorY by remember { mutableIntStateOf(0) }
    var cursorVisible by remember { mutableStateOf(false) }
    var cursorBitmap by remember { mutableStateOf<android.graphics.Bitmap?>(null) }
    var cursorHotspotX by remember { mutableIntStateOf(0) }
    var cursorHotspotY by remember { mutableIntStateOf(0) }
    var hasCursorPos by remember { mutableStateOf(false) }
    val surfaceView = remember { SurfaceView(activity) }
    val scope = rememberCoroutineScope()
    val decoder = remember {
        VideoDecoder(surfaceView.holder) { scope.launch { client.sendRequestKeyframe() } }
    }
    // Decoding runs on one dedicated thread. A slow decoder must never stall the socket reader or
    // the control path, and MediaCodec plus its surface are only touched from this thread.
    val videoDispatcher = remember {
        Executors.newSingleThreadExecutor { r -> Thread(r, "screenly-video") }.asCoroutineDispatcher()
    }
    DisposableEffect(Unit) {
        onDispose {
            client.close() // stop the socket + receive loop with the composition
            decoder.release()
            videoDispatcher.close()
        }
    }

    // Loading screen state
    var retryToken by remember { mutableIntStateOf(0) }
    var showRetry by remember { mutableStateOf(false) }
    var statusText by remember { mutableStateOf("Connecting to PC...") }

    LaunchedEffect(retryToken) {
        var firstAttempt = true
        // Connection loop
        launch {
            while (true) {
                if (!connected) {
                    if (!firstAttempt) kotlinx.coroutines.delay(2000)
                    firstAttempt = false
                    val host = "127.0.0.1"
                    try {
                        if (client.connect(host, 7543)) {
                            val size = android.graphics.Point()
                            activity.windowManager.defaultDisplay.getRealSize(size)
                            val dm = activity.resources.displayMetrics
                            val deviceId = android.provider.Settings.Secure.getString(activity.contentResolver, android.provider.Settings.Secure.ANDROID_ID) ?: ""
                            client.send(Hello(deviceName = android.os.Build.MODEL, screenWidth = size.x, screenHeight = size.y, densityDpi = dm.densityDpi, deviceId = deviceId))
                        } else {
                            android.util.Log.e("MainActivity", "connect returned false")
                        }
                    } catch (e: Exception) {
                        android.util.Log.e("MainActivity", "connect/send failed: ${e.message}")
                    }
                } else {
                    kotlinx.coroutines.delay(1000)
                }
            }
        }
        // Timeout: show retry button after 10s if no first frame
        launch {
            kotlinx.coroutines.delay(10000)
            if (!firstFrameReceived) {
                statusText = "Still connecting..."
                showRetry = true
            }
        }
        // Control path: never dropped, always on Main so UI state and cursor updates are safe.
        launch {
            client.controlEvents.collect { ev ->
                when (ev) {
                    is ServerEvent.HelloAcked -> {
                        connected = true
                        ev.ack.config?.let { streamW = it.width; streamH = it.height }
                        activity.startService(Intent(activity, ConnectionService::class.java))
                        statusText = "Waiting for first frame..."
                    }
                    is ServerEvent.Configured -> {
                        streamW = ev.c.width
                        streamH = ev.c.height
                        // The H.264 codec is configured by the video worker from the frame header,
                        // so it is only ever driven from its own thread.
                    }
                    is ServerEvent.CursorPos -> {
                        cursorX = ev.x
                        cursorY = ev.y
                        cursorVisible = ev.visible
                        hasCursorPos = true
                    }
                    is ServerEvent.CursorShape -> {
                        cursorBitmap?.recycle()
                        cursorBitmap = android.graphics.BitmapFactory.decodeByteArray(ev.pngData, 0, ev.pngData.size)
                        cursorHotspotX = ev.hotspotX
                        cursorHotspotY = ev.hotspotY
                    }
                    is ServerEvent.Disconnected -> {
                        connected = false
                        firstFrameReceived = false
                        cursorVisible = false
                        hasCursorPos = false
                        cursorBitmap?.recycle()
                        cursorBitmap = null
                        client.close()
                        activity.stopService(Intent(activity, ConnectionService::class.java))
                    }
                    else -> {}
                }
            }
        }
        // Video path: latest-frame only, off Main. A slow decode costs frames, never control.
        launch(videoDispatcher) {
            var lastSeq: Int? = null
            client.frames.collect { ev ->
                if (ev.w != streamW || ev.h != streamH) {
                    withContext(Dispatchers.Main) { streamW = ev.w; streamH = ev.h }
                }
                if (ev.isJpeg) {
                    lastSeq = null // JPEG frames do not advance the H.264 sequence
                    val bmp = android.graphics.BitmapFactory.decodeByteArray(ev.nal, 0, ev.nal.size)
                    if (bmp != null) {
                        withContext(Dispatchers.Main) {
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
                            if (!firstFrameReceived) {
                                firstFrameReceived = true
                                showRetry = false
                            }
                        }
                    }
                } else {
                    // A gap means frames were dropped here or lost upstream. Only an IDR repairs
                    // the reference chain, so ask for one and gate P-frames until it arrives.
                    val prev = lastSeq
                    if (prev != null && ev.seq != prev + 1) decoder.onSequenceGap()
                    lastSeq = ev.seq
                    decoder.configure(ev.w, ev.h)
                    decoder.decode(ev.nal, ev.tsUs, ev.keyframe)
                    if (!firstFrameReceived) {
                        withContext(Dispatchers.Main) {
                            firstFrameReceived = true
                            showRetry = false
                        }
                    }
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
            AndroidView(factory = { _ ->
                val tc = TouchCapture(client, { streamW }, { streamH })
                surfaceView.setOnTouchListener { _, ev -> tc.handle(ev, surfaceView.width, surfaceView.height); true }
                surfaceView
            }, modifier = Modifier.size(videoWidth, videoHeight).align(Alignment.Center))
            // Cursor overlay
            Canvas(modifier = Modifier.size(videoWidth, videoHeight).align(Alignment.Center)) {
                if (!hasCursorPos || !cursorVisible) return@Canvas
                val scale = size.width / streamW.coerceAtLeast(1).toFloat()
                val bmp = cursorBitmap
                if (bmp != null) {
                    drawImage(
                        bmp.asImageBitmap(),
                        topLeft = Offset(cursorX * scale - cursorHotspotX * scale, cursorY * scale - cursorHotspotY * scale)
                    )
                } else {
                    drawCircle(Color.Yellow, radius = 8f, center = Offset(cursorX * scale, cursorY * scale))
                }
            }
            // Loading overlay: shown until first frame received
            if (!firstFrameReceived) {
                Column(
                    Modifier.fillMaxSize().padding(16.dp).windowInsetsPadding(WindowInsets.systemBars),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Text("Screenly", style = MaterialTheme.typography.headlineMedium)
                    Spacer(Modifier.height(16.dp))
                    CircularProgressIndicator()
                    Spacer(Modifier.height(16.dp))
                    Text(statusText)
                    if (showRetry) {
                        Spacer(Modifier.height(16.dp))
                        Button(onClick = {
                            client.close()
                            firstFrameReceived = false
                            connected = false
                            showRetry = false
                            statusText = "Connecting to PC..."
                            retryToken++
                        }) { Text("Retry") }
                    }
                }
            }
        }
    }
}