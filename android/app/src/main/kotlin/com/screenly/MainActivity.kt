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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.screenly.discovery.Host
import com.screenly.discovery.discoverHosts
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
    val scope = rememberCoroutineScope()
    val client = remember { ScreenlyClient() }
    var hosts by remember { mutableStateOf(listOf<Host>()) }
    var connected by remember { mutableStateOf(false) }
    var streamW by remember { mutableIntStateOf(1920) }
    var streamH by remember { mutableIntStateOf(1080) }
    val surfaceView = remember { SurfaceView(activity) }
    val decoder = remember { VideoDecoder(surfaceView.holder) }
    DisposableEffect(Unit) { onDispose { decoder.release() } }

    LaunchedEffect(Unit) {
        scope.launch { discoverHosts(activity).collect { hosts = it } }
        scope.launch {
            client.events.collect { ev ->
                when (ev) {
                    is ServerEvent.HelloAcked -> {
                        connected = true
                        ev.ack.config?.let { streamW = it.width; streamH = it.height }
                        activity.startService(Intent(activity, ConnectionService::class.java))
                    }
                    is ServerEvent.Frame -> {
                        if (ev.w != streamW || ev.h != streamH) { streamW = ev.w; streamH = ev.h }
                        decoder.configure(ev.w, ev.h)
                        decoder.decode(ev.nal, ev.tsUs, ev.keyframe)
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
            Column(Modifier.fillMaxSize().padding(16.dp)) {
                Text("Screenly", style = MaterialTheme.typography.headlineMedium)
                Spacer(Modifier.height(8.dp))
                hosts.forEach { h ->
                    Button(onClick = {
                        scope.launch {
                            if (client.connect(h.host, h.port)) {
                                val dm = activity.resources.displayMetrics
                                client.send(Hello(deviceName = android.os.Build.MODEL, screenWidth = dm.widthPixels, screenHeight = dm.heightPixels, densityDpi = dm.densityDpi))
                            }
                        }
                    }, Modifier.fillMaxWidth()) { Text("${h.name} (${h.host})") }
                }
                if (hosts.isEmpty()) Text("Searching for hosts on LAN…")
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
