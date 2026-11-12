package com.screenly.input

import android.view.MotionEvent
import com.screenly.net.ClientMsg
import com.screenly.net.InputTouch
import com.screenly.net.TouchEvt
import com.screenly.net.ScreenlyClient
import kotlinx.coroutines.*

class TouchCapture(private val client: ScreenlyClient, private val streamW: () -> Int, private val streamH: () -> Int) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val queue = mutableListOf<TouchEvt>()

    fun handle(ev: MotionEvent, viewW: Int, viewH: Int) {
        val sw = streamW(); val sh = streamH()
        val events = mutableListOf<TouchEvt>()
        for (i in 0 until ev.pointerCount) {
            val type = when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> "down"
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> "up"
                MotionEvent.ACTION_MOVE -> "move"
                MotionEvent.ACTION_CANCEL -> "cancel"
                else -> "move"
            }
            val x = ev.getX(i) * sw / viewW
            val y = ev.getY(i) * sh / viewH
            events.add(TouchEvt(type, ev.getPointerId(i), x, y, ev.getPressure(i), System.nanoTime() / 1000))
        }
        scope.launch { client.send(ClientMsg.InputTouch(events)) }
    }
}
