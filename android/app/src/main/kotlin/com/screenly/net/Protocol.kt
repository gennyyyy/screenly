package com.screenly.net

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable
sealed interface ClientMsg

@Serializable data class Hello(val protocolVersion: Int = 1, val deviceName: String, val screenWidth: Int, val screenHeight: Int, val densityDpi: Int, val deviceId: String, val pairingToken: String? = null, val pin: String? = null) : ClientMsg
@Serializable data class InputTouch(val events: List<TouchEvt>) : ClientMsg
@Serializable data class TouchEvt(val type: String, val pointerId: Int, val x: Float, val y: Float, val pressure: Float, val tUs: Long)
@Serializable data class InputKey(val events: List<KeyEvt>) : ClientMsg
@Serializable data class KeyEvt(val type: String, val keyCode: Int, val tUs: Long)
@Serializable data class Config(val width: Int, val height: Int, val fps: Int, val bitrateKbps: Int, val keyframeIntervalSec: Int = 2, val codec: String = "jpeg") : ClientMsg
@Serializable data object Ping : ClientMsg
@Serializable data object PongMsg : ClientMsg
@Serializable data class Disconnect(val reason: String, val message: String? = null) : ClientMsg

@Serializable
sealed interface ServerMsg

@Serializable data class HelloAck(val status: String, val sessionId: String? = null, val pairingToken: String? = null, val config: Config? = null) : ServerMsg
@Serializable data class ConfigMsg(val width: Int, val height: Int, val fps: Int, val bitrateKbps: Int, val keyframeIntervalSec: Int = 2, val codec: String = "jpeg") : ServerMsg
@Serializable data class ErrorMsg(val code: String, val message: String) : ServerMsg
@Serializable data object Pong : ServerMsg

object Protocol {
    const val MAX_SIZE = 4 * 1024 * 1024
    val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    // Message type codes (must match Windows side)
    const val HELLO: Byte = 0x01
    const val HELLO_ACK: Byte = 0x02
    const val VIDEO_FRAME: Byte = 0x03
    const val INPUT_TOUCH: Byte = 0x04
    const val INPUT_KEY: Byte = 0x05
    const val CONFIG: Byte = 0x06
    const val PING: Byte = 0x07
    const val PONG: Byte = 0x08
    const val DISCONNECT: Byte = 0x09
    const val ERROR: Byte = 0x0A
    const val CURSOR_POS: Byte = 0x0B
    const val CURSOR_SHAPE: Byte = 0x0C
}
