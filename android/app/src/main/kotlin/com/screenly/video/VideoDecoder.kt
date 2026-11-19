package com.screenly.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import android.view.SurfaceHolder
import java.util.concurrent.atomic.AtomicLong

class VideoDecoder(
    private val holder: SurfaceHolder,
    /**
     * Asks the host for an IDR; wired to ScreenlyClient.sendRequestKeyframe(). Called with the
     * decoder lock held, so it must not block or call back into the decoder.
     */
    private val onKeyframeRequest: () -> Unit = {},
) : SurfaceHolder.Callback {
    /**
     * Decode runs on a dedicated worker thread while surface callbacks arrive on the main thread.
     * Every entry point holds this lock, so the codec, its configuration and the keyframe gate
     * are touched by one thread at a time.
     */
    private val lock = Any()
    private var codec: MediaCodec? = null
    private var width = 0
    private var height = 0
    private var pendingFrame: Triple<ByteArray, Long, Boolean>? = null
    private var pendingConfig: Pair<Int, Int>? = null
    private var loggedFirstInput = false
    private var loggedFirstOutput = false
    /** Set after configure() and after any dropped frame: P-frames are unusable until an IDR. */
    private var waitingForKeyframe = true
    private var lastKeyframeRequestMs = 0L

    init { holder.addCallback(this) }

    override fun surfaceCreated(holder: SurfaceHolder) {
        android.util.Log.e("VideoDecoder", "surfaceCreated")
        synchronized(lock) { pendingConfig?.let { (w, h) -> configure(w, h) } }
    }
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, w: Int, h: Int) {}
    override fun surfaceDestroyed(holder: SurfaceHolder) = synchronized(lock) {
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
    }

    fun configure(w: Int, h: Int) {
        synchronized(lock) { configureLocked(w, h) }
    }

    private fun configureLocked(w: Int, h: Int) {
        if (w <= 0 || h <= 0) return
        if (w == width && h == height && codec != null) return
        android.util.Log.e("VideoDecoder", "configure called: ${w}x${h}, surface=${holder.surface?.isValid}")
        pendingConfig = w to h
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
        width = w; height = h
        // The SurfaceView is sized and laid out by Compose; resizing its surface from here
        // recreates the native surface and invalidates the one handed to configure().
        val surface = holder.surface?.takeIf { it.isValid } ?: run {
            android.util.Log.e("VideoDecoder", "surface not ready, deferring")
            return
        }
        try {
            codec = openCodec(surface, w, h, minimal = false)
        } catch (e: Exception) {
            logConfigureFailure(w, h, "configure", e)
            codec = try {
                openCodec(surface, w, h, minimal = true)
            } catch (retry: Exception) {
                logConfigureFailure(w, h, "configure retry", retry)
                null
            }
        }
        // A fresh codec has no parameter sets, so nothing decodes until an IDR with SPS/PPS.
        if (codec != null) {
            waitingForKeyframe = true
            pendingFrame?.let { (nal, ts, kf) -> decode(nal, ts, kf) }
            pendingFrame = null
        }
    }

    /** Full format first; the minimal one drops the optional hints some codecs reject outright. */
    private fun openCodec(surface: Surface, w: Int, h: Int, minimal: Boolean): MediaCodec {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
        if (!minimal) {
            format.setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
        }
        val created = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        try {
            created.configure(format, surface, null, 0)
            created.start()
            return created
        } catch (e: Exception) {
            try { created.release() } catch (_: Exception) { }
            throw e
        }
    }

    private fun logConfigureFailure(w: Int, h: Int, stage: String, e: Exception) {
        android.util.Log.e("VideoDecoder", "$stage failed for ${w}x${h}: ${e.javaClass.name}: ${e.message}", e)
        if (e is MediaCodec.CodecException) VideoStats.onCodecException("$stage ${w}x${h}", e)
    }

    fun decode(nal: ByteArray, ptsUs: Long, isKeyframe: Boolean) {
        synchronized(lock) { decodeLocked(nal, ptsUs, isKeyframe) }
    }

    private fun decodeLocked(nal: ByteArray, ptsUs: Long, isKeyframe: Boolean) {
        val c = codec
        if (c == null) {
            if (pendingFrame != null) {
                VideoStats.onDroppedOther("pending")
                enterWaitingForKeyframe()
            }
            pendingFrame = Triple(nal, ptsUs, isKeyframe)
            return
        }
        try {
            if (!loggedFirstInput) {
                loggedFirstInput = true
                val prefix = nal.take(24).joinToString("") { "%02X".format(it) }
                android.util.Log.e("VideoDecoder", "first H264 input: ${nal.size} bytes, keyframe=$isKeyframe, annexB=${hasAnnexBStartCode(nal)}, nalTypes=${annexBNalTypes(nal)}, prefix=$prefix")
            }
            if (waitingForKeyframe) {
                if (!isIdrWithParamSets(nal, isKeyframe)) {
                    VideoStats.onDroppedOther("waitingKeyframe")
                    enterWaitingForKeyframe()
                    return
                }
                waitingForKeyframe = false
            }
            val inIdx = c.dequeueInputBuffer(INPUT_TIMEOUT_US)
            if (inIdx >= 0) {
                val buf = c.getInputBuffer(inIdx)!!
                buf.clear()
                if (nal.size > buf.remaining()) {
                    android.util.Log.e("VideoDecoder", "H264 access unit too large for input buffer: ${nal.size} > ${buf.remaining()}")
                    c.queueInputBuffer(inIdx, 0, 0, ptsUs, 0)
                    VideoStats.onDroppedOther("tooLarge")
                    enterWaitingForKeyframe()
                    return
                }
                buf.put(nal)
                c.queueInputBuffer(inIdx, 0, nal.size, ptsUs, if (isKeyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
                VideoStats.onFrameQueued()
            } else {
                VideoStats.onDroppedNoBuffer()
                enterWaitingForKeyframe()
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
        } catch (e: MediaCodec.CodecException) {
            VideoStats.onCodecException("decode", e)
        } catch (e: Exception) { android.util.Log.e("VideoDecoder", "decode failed", e) }
    }

    /**
     * A decodable restart point. The protocol requires every keyframe access unit to carry SPS+PPS
     * in-band, so an IDR without them is not enough to start a fresh codec. Non-Annex-B input
     * cannot be inspected, so the keyframe flag decides there.
     */
    private fun isIdrWithParamSets(nal: ByteArray, isKeyframe: Boolean): Boolean {
        if (!isKeyframe) return false
        val types = annexBNalTypes(nal)
        return types.isEmpty() || (types.contains(7) && types.contains(8))
    }

    /**
     * A frame went missing before it reached the decoder (dropped here, or upstream): P-frames no
     * longer reference the right picture, so gate everything but an IDR and ask for one.
     */
    fun onSequenceGap() = synchronized(lock) { enterWaitingForKeyframe() }

    /** Drops nothing by itself: marks the stream unrecoverable until an IDR, and asks for one (throttled). */
    private fun enterWaitingForKeyframe() {
        waitingForKeyframe = true
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastKeyframeRequestMs < KEYFRAME_REQUEST_INTERVAL_MS) return
        lastKeyframeRequestMs = now
        onKeyframeRequest()
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

    fun release() {
        synchronized(lock) {
            try { codec?.stop(); codec?.release() } catch (_: Exception) { }
            codec = null
        }
    }

    private companion object {
        /** Short block so a briefly-full input queue does not cost a keyframe round trip. */
        const val INPUT_TIMEOUT_US = 10_000L
        /** The engine accepts at most one request per 500 ms; match it locally. */
        const val KEYFRAME_REQUEST_INTERVAL_MS = 500L
    }
}

/**
 * Video-path telemetry. Instrumentation only: counters never change decode behavior.
 * One line per second while frames are flowing, tagged ScreenlyStats.
 */
object VideoStats {
    private const val TAG = "ScreenlyStats"
    private const val INTERVAL_MS = 1000L

    private val received = AtomicLong()
    private val queued = AtomicLong()
    private val droppedBacklog = AtomicLong()
    private val droppedNoBuffer = AtomicLong()
    private val droppedOther = AtomicLong()
    private val codecExceptions = AtomicLong()

    @Volatile private var streamW = 0
    @Volatile private var streamH = 0
    @Volatile private var codecLabel = "none"
    @Volatile private var lastDropReason: String? = null
    @Volatile private var lastCodecError: String? = null

    private val lock = Any()
    @Volatile private var nextLogAtMs = 0L
    private var lastLogAtMs = 0L
    private var seenReceived = 0L
    private var seenQueued = 0L
    private var seenBacklog = 0L
    private var seenNoBuffer = 0L
    private var seenOther = 0L
    private var seenCodecExceptions = 0L

    fun onFrameReceived(w: Int, h: Int, isJpeg: Boolean) {
        streamW = w
        streamH = h
        codecLabel = if (isJpeg) "jpeg" else "h264"
        received.incrementAndGet()
    }

    /** Frame read off the socket but superseded by a newer one before the decoder took it. */
    fun onBacklogSkip() {
        droppedBacklog.incrementAndGet()
    }

    fun onFrameQueued() { queued.incrementAndGet() }

    fun onDroppedNoBuffer() { droppedNoBuffer.incrementAndGet() }

    fun onDroppedOther(reason: String) {
        lastDropReason = reason
        droppedOther.incrementAndGet()
    }

    fun onCodecException(stage: String, e: MediaCodec.CodecException) {
        codecExceptions.incrementAndGet()
        lastCodecError = "$stage: ${e.message}"
        android.util.Log.e(TAG, "MediaCodec.CodecException in $stage " +
                "(errorCode=${e.errorCode}, diagnostic=${e.diagnosticInfo})", e)
    }

    /** Cheap when not due; at most one line per second across all callers. */
    fun logIfDue() {
        val now = android.os.SystemClock.elapsedRealtime()
        if (now < nextLogAtMs) return
        synchronized(lock) {
            if (now < nextLogAtMs) return
            nextLogAtMs = now + INTERVAL_MS
            val interval = if (lastLogAtMs == 0L) "first" else "${now - lastLogAtMs}ms"
            lastLogAtMs = now

            val r = received.get()
            val q = queued.get()
            val b = droppedBacklog.get()
            val n = droppedNoBuffer.get()
            val o = droppedOther.get()
            val x = codecExceptions.get()
            val dReceived = r - seenReceived
            val dQueued = q - seenQueued
            val dBacklog = b - seenBacklog
            val dNoBuffer = n - seenNoBuffer
            val dOther = o - seenOther
            val dCodecEx = x - seenCodecExceptions
            seenReceived = r
            seenQueued = q
            seenBacklog = b
            seenNoBuffer = n
            seenOther = o
            seenCodecExceptions = x

            val extra = buildString {
                lastDropReason?.let { append(", lastDrop=$it") }
                lastCodecError?.let { append(", lastCodecError=${it.take(200)}") }
            }
            android.util.Log.i(TAG, "$codecLabel ${streamW}x$streamH interval=$interval " +
                    "recv=$dReceived queued=$dQueued dropped=${dBacklog + dNoBuffer + dOther} " +
                    "(backlog=$dBacklog noBuffer=$dNoBuffer other=$dOther) codecEx=$dCodecEx " +
                    "| total recv=$r queued=$q dropped=${b + n + o} codecEx=$x$extra")
        }
    }
}
