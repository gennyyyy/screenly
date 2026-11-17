# Plan — Media Foundation H.264 Encoder Implementation

**Goal:** Implement the `H264Encoder` (currently a stub returning null) so the engine can stream H.264 at 30-60 fps instead of falling back to JPEG (~15 fps).

**Scope:** `windows/Screenly.Core/Encoding/H264Encoder.cs` only. No Android changes (already supports H.264 via MediaCodec).

## Current state
- `Initialize()` calls `MFStartup` and `CoCreateInstance(CLSID_CMSH264EncoderMFT)` as `IUnknown`. Sets `_ready = true` if hr >= 0.
- `Encode()` returns `null` (stub).
- `RequestKeyframe()` sets a flag (never consumed).

## Design
1. **QueryInterface** for `IMFTransform` from the MFT pointer.
2. **Set input type**: MFVideoFormat (BGRA or NV12). Since capture gives BGRA, either convert to NV12 before feeding, or set input type as BGRA if the MFT supports it. The Microsoft H.264 MFT accepts NV12 input. So convert BGRA→NV12.
3. **Set output type**: H.264 (MFVideoFormat_H264) with CBR, target bitrate, fps.
4. **Configure**: set CODECAPI_AVLowLatencyMode=TRUE, MaxBFrames=0, force keyframe via CODECAPI_AVEncVideoForceKeyFrame.
5. **Feed input**: create IMFSample with IMFMediaBuffer containing NV12 data; call `ProcessInput`.
6. **Drain output**: call `ProcessOutput` until MF_E_TRANSFORM_NEED_MORE_INPUT; extract H.264 NAL units from output buffer.
7. **Keyframe**: when `_forceKeyframe` is set, set CODECAPI_AVEncVideoForceKeyFrame before processing.

## COM interfaces needed
- `IMFTransform` (ProcessInput, ProcessOutput, SetInputType, GetOutputAvailableType, etc.)
- `IMFMediaType`
- `IMFSample`
- `IMFMediaBuffer` (Lock/Unlock)
- `IMFAttributes` (for codec API)

## Tasks
- E1: Add IMFTransform, IMFMediaType, IMFSample, IMFMediaBuffer COM interface definitions (P/Invoke/COM interop).
- E2: Implement BGRA→NV12 conversion.
- E3: Implement SetInputType/SetOutputType in Initialize.
- E4: Implement Encode: convert, feed, drain, return EncodedFrame.
- E5: Implement RequestKeyframe via CODECAPI.
- E6: Handle disposal.
- E7: Test: encode 10 frames, verify H.264 NAL output with at least one IDR.

## Risks
- COM interop complexity; may need `Marshal.QueryInterface`.
- NV12 conversion performance (CPU-bound; could be slow).
- MFT may not be available on all systems (software fallback).
- Thread safety: MFT is single-threaded; engine capture loop is single-threaded, so OK.

## Verification
- `dotnet build` 0 errors.
- Manual: run engine with `codec: "h264"` in config; verify frames display on Android.
- Log encode time per frame.