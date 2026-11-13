package com.screenly.net

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import java.io.DataInputStream
import java.io.IOException
import java.io.DataOutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

sealed interface ServerEvent {
    data class HelloAcked(val ack: HelloAck) : ServerEvent
    data class Frame(val seq: Int, val tsUs: Long, val w: Int, val h: Int, val keyframe: Boolean, val nal: ByteArray) : ServerEvent
    data class Configured(val c: ConfigMsg) : ServerEvent
    data class Errored(val e: ErrorMsg) : ServerEvent
    data object Ponged : ServerEvent
    data object Disconnected : ServerEvent
}

class ScreenlyClient {
    private var socket: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _events = MutableSharedFlow<ServerEvent>(extraBufferCapacity = 64)
    val events: SharedFlow<ServerEvent> = _events

    suspend fun connect(host: String, port: Int): Boolean = withContext(Dispatchers.IO) {
        try {
            val s = Socket()
            s.tcpNoDelay = true
            s.connect(java.net.InetSocketAddress(host, port), 5000)
            socket = s
            input = DataInputStream(s.getInputStream())
            output = DataOutputStream(s.getOutputStream())
            // receive loop
            scope.launch { receiveLoop() }
            true
        } catch (e: Exception) {
            false
        }
    }

    private suspend fun receiveLoop() {
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
                dispatch(type, payload)
            }
        } catch (e: Exception) {
            try { socket?.close() } catch (_: Exception) { }
            _events.tryEmit(ServerEvent.Disconnected)
        }
    }

    private fun dispatch(type: Byte, payload: ByteArray) {
        when (type) {
            Protocol.HELLO_ACK -> {
                val ack = Protocol.json.decodeFromString<HelloAck>(String(payload))
                _events.tryEmit(ServerEvent.HelloAcked(ack))
            }
            Protocol.VIDEO_FRAME -> {
                val bb = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
                val seq = bb.int
                val ts = bb.long
                val w = bb.short.toInt() and 0xFFFF
                val h = bb.short.toInt() and 0xFFFF
                val flags = bb.get().toInt()
                val nal = ByteArray(bb.remaining())
                bb.get(nal)
                _events.tryEmit(ServerEvent.Frame(seq, ts, w, h, (flags and 1) == 1, nal))
            }
            Protocol.CONFIG -> {
                val c = Protocol.json.decodeFromString<ConfigMsg>(String(payload))
                _events.tryEmit(ServerEvent.Configured(c))
            }
            Protocol.ERROR -> {
                val e = Protocol.json.decodeFromString<ErrorMsg>(String(payload))
                _events.tryEmit(ServerEvent.Errored(e))
            }
            Protocol.PONG -> _events.tryEmit(ServerEvent.Ponged)
        }
    }

    fun send(msg: ClientMsg) {
        val (type, bytes) = when (msg) {
            is Hello -> Protocol.HELLO to Protocol.json.encodeToString(Hello.serializer(), msg).toByteArray()
            is InputTouch -> Protocol.INPUT_TOUCH to Protocol.json.encodeToString(InputTouch.serializer(), msg).toByteArray()
            is InputKey -> Protocol.INPUT_KEY to Protocol.json.encodeToString(InputKey.serializer(), msg).toByteArray()
            is Config -> Protocol.CONFIG to Protocol.json.encodeToString(Config.serializer(), msg).toByteArray()
            is Ping -> Protocol.PING to ByteArray(0)
            is Disconnect -> Protocol.DISCONNECT to Protocol.json.encodeToString(Disconnect.serializer(), msg).toByteArray()
        }
        try {
            val out = output ?: return
            synchronized(out) {
                val lenBuf = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN)
                lenBuf.putInt(1 + bytes.size)
                out.write(lenBuf.array())
                out.writeByte(type.toInt())
                out.write(bytes)
                out.flush()
            }
        } catch (_: Exception) { }
    }

    fun close() {
        scope.cancel()
        try { input?.close() } catch (_: Exception) { }
        try { output?.close() } catch (_: Exception) { }
        try { socket?.close() } catch (_: Exception) { }
        socket = null
        input = null
        output = null
    }
}
