# Plan

Implement the Windows H.264 encoder as a working Media Foundation transform pipeline, then verify that its Annex B output can be streamed and decoded by the existing Android client. Keep JPEG as an explicit fallback when encoder setup or runtime processing fails, and make the selected codec and failure reason observable.

## Scope
- In: `H264Encoder`, Windows engine codec selection and recovery, H.264 frame format/timestamp/keyframe handling, focused encoder and protocol-path validation, and documentation of supported behavior.
- Out: General FPS pipeline redesign, unrelated capture/cursor changes, new codecs, and broad Android rendering refactors unless validation shows a decoder incompatibility that blocks H.264.

## Action items
[ ] Confirm the actual capture buffer contract (`CapturedFrame` pixel format, stride, orientation, and timestamp) and reconcile the engine's `rawFrame.JpegData` use with the H.264 encoder input; document the chosen input format and validate even width/height requirements.
[ ] Implement a Media Foundation transform lifecycle in `windows/Screenly.Core/Encoding/H264Encoder.cs`: check every HRESULT, balance `MFStartup`/`MFShutdown`, activate or instantiate the encoder correctly, configure NV12 input and H.264 output types with frame size/rate/bitrate, and release all COM objects on every failure path.
[ ] Add BGRA-to-NV12 conversion (or use a supported direct input format), allocate/reuse buffers, set sample timestamps and durations, submit samples, and drain all available output including delayed frames; handle transform output requirements and distinguish “no output yet” from encode failure.
[ ] Produce the exact bitstream format expected by `VideoDecoder` (Annex B start codes), identify keyframes from encoded output, ensure SPS/PPS are available at stream start and after reconfiguration, and implement forced keyframes through the encoder's supported codec API or documented equivalent.
[ ] Update `windows/Screenly.Engine/Program.cs` so encoder errors are logged with HRESULT/context, failed frame submission or output triggers controlled reinitialization/fallback, and stream configuration advertises the codec actually in use; preserve JPEG operation when H.264 is unavailable.
[ ] Add focused automated checks for pixel conversion, frame header/keyframe flags, output parsing, and encoder error handling; add a Windows integration check that encodes multiple frames and confirms valid H.264 including an IDR and parameter sets.
[ ] Verify end to end on Windows and Android at the configured dimensions: connect with H.264 selected, decode/render initial and subsequent frames, force a keyframe, change display dimensions, reconnect, and confirm JPEG fallback when encoder creation is unavailable; record encoder implementation/device and measured encode time.
[ ] Update the README and encoder plan with actual support limits, configuration, fallback behavior, and validation results; retain the plan until implementation and verification are complete.

## Open questions
- Which Windows hardware and Android device are available for the end-to-end check? If unspecified, use available devices and record their models and encoder names.
- Should the first implementation require a hardware MFT, or may it select a Windows software MFT when hardware encoding is unavailable? Prefer hardware when present and permit software only if it meets the target throughput.
