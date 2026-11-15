package com.screenly.video

import android.graphics.RectF

/** Geometry shared by video, cursor, and touch so all three use the same letterboxed image area. */
object DisplayGeometry {
    fun contentRect(viewWidth: Int, viewHeight: Int, streamWidth: Int, streamHeight: Int): RectF {
        if (viewWidth <= 0 || viewHeight <= 0 || streamWidth <= 0 || streamHeight <= 0) return RectF()
        val scale = minOf(viewWidth.toFloat() / streamWidth, viewHeight.toFloat() / streamHeight)
        val width = streamWidth * scale
        val height = streamHeight * scale
        val left = (viewWidth - width) / 2f
        val top = (viewHeight - height) / 2f
        return RectF(left, top, left + width, top + height)
    }

    /** Returns null for touches in letterbox bars; otherwise maps to stream pixel coordinates. */
    fun viewToStream(x: Float, y: Float, content: RectF, streamWidth: Int, streamHeight: Int): Pair<Float, Float>? {
        if (content.isEmpty || streamWidth <= 0 || streamHeight <= 0 || !content.contains(x, y)) return null
        val streamX = ((x - content.left) / content.width() * streamWidth).coerceIn(0f, streamWidth.toFloat())
        val streamY = ((y - content.top) / content.height() * streamHeight).coerceIn(0f, streamHeight.toFloat())
        return streamX to streamY
    }

    fun streamToView(x: Int, y: Int, content: RectF, streamWidth: Int, streamHeight: Int): Pair<Float, Float> {
        if (streamWidth <= 0 || streamHeight <= 0 || content.isEmpty) return content.left to content.top
        return (content.left + x * content.width() / streamWidth) to
            (content.top + y * content.height() / streamHeight)
    }
}
