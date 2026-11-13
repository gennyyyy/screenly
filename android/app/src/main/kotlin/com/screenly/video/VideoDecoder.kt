package com.screenly.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import android.view.SurfaceHolder

class VideoDecoder(private val holder: SurfaceHolder) {
    private var codec: MediaCodec? = null
    private var width = 0
    private var height = 0
    private var pendingFrame: Triple<ByteArray, Long, Boolean>? = null

    fun configure(w: Int, h: Int) {
        if (w == width && h == height && codec != null) return
        codec?.stop(); codec?.release()
        width = w; height = h
        val surface = holder.surface ?: run { pendingFrame = null; return }
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
        format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        format.setInteger(MediaFormat.KEY_PRIORITY, 0)
        codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
            configure(format, surface, null, 0)
            start()
        }
        pendingFrame?.let { (nal, ts, kf) -> decode(nal, ts, kf) }
        pendingFrame = null
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
        } catch (_: Exception) { }
    }

    fun release() { try { codec?.stop(); codec?.release() } catch (_: Exception) { }; codec = null }
}
