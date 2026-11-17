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

    init { holder.addCallback(this) }

    override fun surfaceCreated(holder: SurfaceHolder) {
        pendingConfig?.let { (w, h) -> configure(w, h) }
    }
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, w: Int, h: Int) {}
    override fun surfaceDestroyed(holder: SurfaceHolder) {
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
    }

    fun configure(w: Int, h: Int) {
        if (w <= 0 || h <= 0) return
        if (w == width && h == height && codec != null) return
        pendingConfig = w to h
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
        width = w; height = h
        holder.setFixedSize(w, h)
        val surface = holder.surface ?: return
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            format.setInteger(MediaFormat.KEY_PRIORITY, 0)
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
            val inIdx = c.dequeueInputBuffer(0)
            if (inIdx >= 0) {
                val buf = c.getInputBuffer(inIdx)!!
                buf.clear()
                buf.put(nal)
                c.queueInputBuffer(inIdx, 0, nal.size, ptsUs, if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            }
            val info = MediaCodec.BufferInfo()
            var outIdx = c.dequeueOutputBuffer(info, 0)
            while (outIdx >= 0) {
                c.releaseOutputBuffer(outIdx, true)
                outIdx = c.dequeueOutputBuffer(info, 0)
            }
        } catch (e: Exception) { android.util.Log.e("VideoDecoder", "decode failed", e) }
    }

    fun release() { try { codec?.stop(); codec?.release() } catch (_: Exception) { }; codec = null }
}
