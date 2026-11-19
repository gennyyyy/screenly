package com.screenly.net

import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.*
import com.screenly.video.VideoStats
import java.io.DataInputStream
import java.io.IOException
import java.io.DataOutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

sealed interface ServerEvent {
    data class HelloAcked(val ack: HelloAck) : ServerEvent
    data class Frame(val seq: Int, val tsUs: Long, val w: Int, val h: Int, val keyframe: Boolean, val isJpeg: Boolean, val nal: ByteArray) : ServerEvent
    data class Configured(val c: ConfigMsg) : ServerEvent
    data class Errored(val e: ErrorMsg) : ServerEvent
    data object Ponged : ServerEvent
    data class CursorPos(val x: Int, val y: Int, val visible: Boolean, val tsUs: Long) : ServerEvent
    data class CursorShape(val width: Int, val height: Int, val hotspotX: Int, val hotspotY: Int, val pngData: ByteArray) : ServerEvent
    data object Disconnected : ServerEvent
}

class ScreenlyClient {
    @Volatile private var socket: Socket? = null
    @Volatile private var input: DataInputStream? = null
    @Volatile private var output: DataOutputStream? = null
    private var receiveJob: Job? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    // Control messages must never be dropped. A slow UI may lag behind, but never loses a
    // CONFIG, DISCONNECT or cursor update.
    private val _control = Channel<ServerEvent>(Channel.UNLIMITED)
    val controlEvents: Flow<ServerEvent> = _control.receiveAsFlow()

    // One-slot latest-frame handoff. A frame that is still undelivered when a newer one arrives
    // is discarded, so a slow decoder costs frames and never control messages.
    private val _frames = Channel<ServerEvent.Frame>(capacity = 1)
    val frames: Flow<ServerEvent.Frame> = _frames.receiveAsFlow()

    suspend fun connect(host: String, port: Int): Boolean = withContext(Dispatchers.IO) {
        close() // close previous connection
        // Control is lossless now, so anything still queued belongs to the dead connection;
        // a stale DISCONNECT must not tear down the session we are about to open.
        while (_control.tryReceive().isSuccess) { }
        try {
            val s = Socket()
            s.tcpNoDelay = true
            s.connect(java.net.InetSocketAddress(host, port), 15000)
            socket = s
            input = DataInputStream(s.getInputStream())
            output = DataOutputStream(s.getOutputStream())
            // Track receive job per connection
            receiveJob = scope.launch { receiveLoop(s) }
            true
        } catch (e: Exception) {
            android.util.Log.e("ScreenlyClient", "connect failed to $host:$port", e)
            false
        }
    }

    private suspend fun receiveLoop(connSocket: Socket) {
        try {
            val inp = input ?: return
            while (true) {
                val lenBuf = ByteArray(4)
                inp.readFully(lenBuf)
                val len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).int
                if (len < 1 || len > Protocol.MAX_SIZE) throw IOException("bad len")
                val type = inp.readByte()
                val payload = ByteArray(len - 1)
                if (payload.isNotEmpty()) inp.readFully(payload)
                if (!dispatch(connSocket, type, payload)) return
                if (type == Protocol.VIDEO_FRAME) VideoStats.logIfDue()
            }
        } catch (e: Exception) {
            android.util.Log.e("ScreenlyClient", "receive loop error", e)
            // Only close this connection's socket, not a newer one
            if (closeConnection(connSocket)) _control.trySend(ServerEvent.Disconnected)
        }
    }

    // Close this connection's socket and clear stream fields. Returns true if it was the current
    // connection. Never touches receiveJob, because callers include the receive loop itself.
    private fun closeConnection(connSocket: Socket): Boolean {
        if (socket !== connSocket) return false
        try { connSocket.close() } catch (_: Exception) { }
        socket = null
        input = null
        output = null
        return true
    }

    // Returns false for terminal messages, after the connection has been closed.
    private fun dispatch(connSocket: Socket, type: Byte, payload: ByteArray): Boolean {
        when (type) {
            Protocol.HELLO_ACK -> {
                val ack = Protocol.json.decodeFromString<HelloAck>(String(payload))
                _control.trySend(ServerEvent.HelloAcked(ack))
            }
            Protocol.VIDEO_FRAME -> {
                val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
                val seq = bb.int
                val ts = bb.long
                val w = bb.short.toInt() and 0xFFFF
                val h = bb.short.toInt() and 0xFFFF
                val flags = bb.get().toInt()
                val isJpeg = (flags and 2) != 0
                val nal = ByteArray(bb.remaining())
                bb.get(nal)
                VideoStats.onFrameReceived(w, h, isJpeg)
                offerFrame(ServerEvent.Frame(seq, ts, w, h, (flags and 1) == 1, isJpeg, nal))
            }
            Protocol.CONFIG -> {
                val c = Protocol.json.decodeFromString<ConfigMsg>(String(payload))
                _control.trySend(ServerEvent.Configured(c))
            }
            Protocol.ERROR -> {
                val e = Protocol.json.decodeFromString<ErrorMsg>(String(payload))
                _control.trySend(ServerEvent.Errored(e))
            }
            Protocol.PING -> {
                // Answer Windows PING with PONG
                scope.launch { send(PongMsg) }
            }
            Protocol.PONG -> _control.trySend(ServerEvent.Ponged)
            Protocol.CURSOR_POS -> {
                if (payload.size >= 17) {
                    val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
                    val x = bb.int
                    val y = bb.int
                    val visible = bb.get().toInt() == 1
                    val ts = bb.long
                    _control.trySend(ServerEvent.CursorPos(x, y, visible, ts))
                }
            }
            Protocol.CURSOR_SHAPE -> {
                if (payload.size >= 16) {
                    val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
                    val w = bb.int
                    val h = bb.int
                    val hx = bb.int
                    val hy = bb.int
                    val png = ByteArray(bb.remaining())
                    bb.get(png)
                    _control.trySend(ServerEvent.CursorShape(w, h, hx, hy, png))
                }
            }
            Protocol.DISCONNECT -> {
                // Terminal by contract: peer writes this then closes. Emit Disconnected once and
                // close our side so MainActivity reconnects. Do not cancel receiveJob from here.
                if (closeConnection(connSocket)) _control.trySend(ServerEvent.Disconnected)
                return false
            }
        }
        return true
    }

    /**
     * Latest-frame handoff. Only the receive loop calls this, and only a suspended consumer can
     * empty the slot, so after discarding one superseded frame the retry always succeeds.
     */
    private fun offerFrame(frame: ServerEvent.Frame) {
        if (_frames.trySend(frame).isSuccess) return
        _frames.tryReceive() // superseded frame loses
        if (_frames.trySend(frame).isSuccess) VideoStats.onBacklogSkip()
    }

    suspend fun send(msg: ClientMsg) = withContext(Dispatchers.IO) {
        val (type, bytes) = when (msg) {
            is Hello -> Protocol.HELLO to Protocol.json.encodeToString(Hello.serializer(), msg).toByteArray()
            is InputTouch -> Protocol.INPUT_TOUCH to Protocol.json.encodeToString(InputTouch.serializer(), msg).toByteArray()
            is InputKey -> Protocol.INPUT_KEY to Protocol.json.encodeToString(InputKey.serializer(), msg).toByteArray()
            is Config -> Protocol.CONFIG to Protocol.json.encodeToString(Config.serializer(), msg).toByteArray()
            is Ping -> Protocol.PING to ByteArray(0)
            is PongMsg -> Protocol.PONG to ByteArray(0)
            is RequestKeyframe -> Protocol.REQUEST_KEYFRAME to ByteArray(0)
            is Disconnect -> Protocol.DISCONNECT to Protocol.json.encodeToString(Disconnect.serializer(), msg).toByteArray()
        }
        try {
            val out = output
            if (out == null) return@withContext
            synchronized(out) {
                val lenBuf = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN)
                lenBuf.putInt(1 + bytes.size)
                out.write(lenBuf.array())
                out.writeByte(type.toInt())
                out.write(bytes)
                out.flush()
            }
        } catch (e: Exception) { android.util.Log.e("ScreenlyClient", "send failed", e) }
    }

    suspend fun sendRequestKeyframe() = send(RequestKeyframe)

    fun close() {
        try { receiveJob?.cancel() } catch (_: Exception) { }
        receiveJob = null
        try { input?.close() } catch (_: Exception) { }
        try { output?.close() } catch (_: Exception) { }
        try { socket?.close() } catch (_: Exception) { }
        socket = null
        input = null
        output = null
    }
}
