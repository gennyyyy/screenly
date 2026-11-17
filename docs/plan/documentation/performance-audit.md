# Performance Audit — Screenly (FPS)

## Current measured FPS
- Engine capture loop: ~15 fps (hard-capped by `Task.Delay(66)`)
- Android SurfaceView render: ~9 fps
- Gap = ~40% of frames lost between engine and display

## Bottlenecks found

### Engine (Windows)
1. **GDI+ JPEG encode is the dominant cost.** `GdiCapture.TryGetFrame` calls `bmp.Save(ms, jpegCodec, ...)` at 1920×1080 q70. GDI+ JPEG encode takes ~25-40 ms/frame on this hardware. This alone limits throughput to ~25 fps theoretical, ~15 fps actual after BitBlt + overhead.
2. **Hard 15 fps cap.** `Task.Delay(66)` in Program.cs caps the loop regardless of encode speed.
3. **No frame dropping for slow clients.** Every frame is sent to every session via `s.SendAsync`. If a client is slow, the send blocks (write lock) and stalls the loop for all clients.
4. **BitBlt from display DC** reads the full 1920×1080 framebuffer each frame (~8 MB). Fast but contributes ~2-3 ms.
5. **`Image.FromHbitmap`** allocates a new Bitmap each frame (GC pressure).

### Android
6. **`BitmapFactory.decodeByteArray`** decodes JPEG on the main receive thread. ~10-15 ms at 1080p. Blocks receiveLoop, so frames arriving during decode are buffered in TCP (not dropped — they pile up, adding latency).
7. **Synchronous SurfaceView draw** (`lockCanvas` → `drawBitmap` → `unlockCanvasAndPost`) on the receive thread. ~5-8 ms. Blocks decode of next frame.
8. **No frame-skip.** receiveLoop processes every frame sequentially. If decode+draw takes 25 ms, effective FPS is 40 fps theoretical but TCP backpressure means stale frames queue up, adding latency.
9. **`surfaceView.width.coerceAtLeast(1)`** called every frame (minor).

### Network
10. TCP over adb reverse (localhost) — not a bottleneck. ~1 ms RTT.
11. No compression beyond JPEG. Frames are ~150-300 KB each at q70. At 15 fps = ~3-4 MB/s. USB 2.0 can handle this, but it's wasteful.

## Recommended fixes (lazy order)

### Quick wins (do first)
- **A. Lower capture resolution.** 1280×720 instead of 1920×1080. JPEG encode ~3x faster, decode ~2x faster. Biggest single improvement. Config-driven (already in AppConfig).
- **B. Lower JPEG quality.** q70 → q50. ~20% smaller, slightly faster encode. Marginal.
- **C. Increase engine loop rate.** `Task.Delay(66)` → `Task.Delay(33)` for 30 fps cap. Only helps if encode is fast enough (after A).
- **D. Skip frames on Android.** In receiveLoop, if a new frame arrives while still processing, skip (read and discard) to always show the latest. Reduces latency.

### Medium effort
- **E. Move Android decode+draw off the receive thread.** Decode on a background thread, post bitmap to SurfaceView via Handler. Prevents receiveLoop from blocking.
- **F. Use `BitmapFactory.Options.inBitmap`** to reuse a bitmap buffer. Reduces GC.
- **G. Engine: cache the JPEG codec.** `ImageCodecInfo.GetImageEncoders()` is called every frame. Cache it.

### Larger (future)
- **H. DXGI Desktop Duplication + hardware H.264** (already planned in ADR-008). Would give 60 fps. Major rewrite.

## Suggested priority
1. A (resolution) + C (loop rate) + G (codec cache) → engine side, ~2x FPS
2. D (frame skip) + F (inBitmap) → Android side, reduces latency
3. E (background decode) → Android side, smoother rendering
4. H (DXGI+H.264) → future, 60 fps target