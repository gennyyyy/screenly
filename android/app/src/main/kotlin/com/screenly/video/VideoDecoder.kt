package com.screenly.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import android.view.SurfaceHolder

class VideoDecoder(private val holder: SurfaceHolder) : SurfaceHolder.Callback {
    private var codec: MediaCodec? = null
    private var width = 0
    private var height = 0
    private var pendingFrame: Triple<ByteArray, Long, Boolean>? = null
    private var pendingConfig: Pair<Int, Int>? = null
    private var loggedFirstInput = false
    private var loggedFirstOutput = false

    init { holder.addCallback(this) }

    override fun surfaceCreated(holder: SurfaceHolder) {
        android.util.Log.e("VideoDecoder", "surfaceCreated")
        pendingConfig?.let { (w, h) -> configure(w, h) }
    }
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, w: Int, h: Int) {}
    override fun surfaceDestroyed(holder: SurfaceHolder) {
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
    }

    fun configure(w: Int, h: Int) {
        android.util.Log.e("VideoDecoder", "configure called: ${w}x${h}, surface=${holder.surface != null}")
        if (w <= 0 || h <= 0) return
        if (w == width && h == height && codec != null) return
        pendingConfig = w to h
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
        width = w; height = h
        holder.setFixedSize(w, h)
        val surface = holder.surface ?: run {
            android.util.Log.e("VideoDecoder", "surface is null, deferring")
            return
        }
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
            codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
                configure(format, surface, null, 0)
                start()
            }
            pendingFrame?.let { (nal, ts, kf) -> decode(nal, ts, kf) }
            pendingFrame = null
        } catch (e: Exception) {
            android.util.Log.e("VideoDecoder", "configure failed for ${w}x${h}", e)
            codec = null
        }
    }

    fun decode(nal: ByteArray, ptsUs: Long, isKeyframe: Boolean) {
        val c = codec
        if (c == null) { pendingFrame = Triple(nal, ptsUs, isKeyframe); return }
        try {
            if (!loggedFirstInput) {
                loggedFirstInput = true
                val prefix = nal.take(24).joinToString("") { "%02X".format(it) }
                android.util.Log.e("VideoDecoder", "first H264 input: ${nal.size} bytes, keyframe=$isKeyframe, annexB=${hasAnnexBStartCode(nal)}, nalTypes=${annexBNalTypes(nal)}, prefix=$prefix")
            }
            val inIdx = c.dequeueInputBuffer(0)
            if (inIdx >= 0) {
                val buf = c.getInputBuffer(inIdx)!!
                buf.clear()
                if (nal.size > buf.remaining()) {
                    android.util.Log.e("VideoDecoder", "H264 access unit too large for input buffer: ${nal.size} > ${buf.remaining()}")
                    c.queueInputBuffer(inIdx, 0, 0, ptsUs, 0)
                    return
                }
                buf.put(nal)
                c.queueInputBuffer(inIdx, 0, nal.size, ptsUs, if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            }
            val info = MediaCodec.BufferInfo()
            var outIdx = c.dequeueOutputBuffer(info, 0)
            while (outIdx >= 0) {
                if (!loggedFirstOutput) {
                    loggedFirstOutput = true
                    android.util.Log.e("VideoDecoder", "first H264 output: ${info.size} bytes, flags=${info.flags}, ptsUs=${info.presentationTimeUs}")
                }
                c.releaseOutputBuffer(outIdx, true)
                outIdx = c.dequeueOutputBuffer(info, 0)
            }
        } catch (e: Exception) { android.util.Log.e("VideoDecoder", "decode failed", e) }
    }

    private fun hasAnnexBStartCode(data: ByteArray): Boolean = findStartCode(data, 0) >= 0

    private fun annexBNalTypes(data: ByteArray): List<Int> {
        val types = mutableListOf<Int>()
        var start = findStartCode(data, 0)
        while (start >= 0) {
            val codeSize = if (start + 3 < data.size && data[start] == 0.toByte() && data[start + 1] == 0.toByte() && data[start + 2] == 0.toByte() && data[start + 3] == 1.toByte()) 4 else 3
            val nalHeader = start + codeSize
            if (nalHeader < data.size) types += data[nalHeader].toInt() and 0x1f
            start = findStartCode(data, nalHeader)
        }
        return types
    }

    private fun findStartCode(data: ByteArray, from: Int): Int {
        for (i in from until data.size - 2) {
            if (data[i] == 0.toByte() && data[i + 1] == 0.toByte() &&
                (data[i + 2] == 1.toByte() || (i + 3 < data.size && data[i + 2] == 0.toByte() && data[i + 3] == 1.toByte()))) return i
        }
        return -1
    }

    fun release() { try { codec?.stop(); codec?.release() } catch (_: Exception) { }; codec = null }
}
