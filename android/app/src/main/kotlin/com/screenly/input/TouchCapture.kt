package com.screenly.input

import android.view.MotionEvent
import com.screenly.net.ClientMsg
import com.screenly.net.InputTouch
import com.screenly.net.TouchEvt
import com.screenly.net.ScreenlyClient
import com.screenly.video.DisplayGeometry
import kotlinx.coroutines.*

class TouchCapture(private val client: ScreenlyClient, private val streamW: () -> Int, private val streamH: () -> Int) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    fun handle(ev: MotionEvent, viewW: Int, viewH: Int) {
        if (viewW <= 0 || viewH <= 0) return
        val sw = streamW(); val sh = streamH()
        if (sw <= 0 || sh <= 0) return
        val events = mutableListOf<TouchEvt>()
        for (i in 0 until ev.pointerCount) {
            val type = when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> "down"
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> "up"
                MotionEvent.ACTION_MOVE -> "move"
                MotionEvent.ACTION_CANCEL -> "cancel"
                else -> "move"
            }
            val content = DisplayGeometry.contentRect(viewW, viewH, sw, sh)
            val point = DisplayGeometry.viewToStream(ev.getX(i), ev.getY(i), content, sw, sh) ?: continue
            val (x, y) = point
            events.add(TouchEvt(type, ev.getPointerId(i), x, y, ev.getPressure(i), System.nanoTime() / 1000))
        }
        scope.launch { client.send(InputTouch(events)) }
    }

    fun release() { scope.cancel() }
}
