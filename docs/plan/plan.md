# Plan

**Status: Blocked.** The H.264 Encoder MFT is registered, but exhaustive activation attempts have not produced an `IMFTransform`: `CoCreateInstance` with `IID_IMFTransform` returns `E_NOINTERFACE`; `IUnknown` activation succeeds but cannot be queried or marshalled as `IMFTransform`; `MFTEnumEx` returns zero transforms for the tested category/media-type combinations; and direct vtable calls fail with `AccessViolationException`. Public code found so far is not a confirmed working reference for this environment. Do not repeat activation experiments or infer vtable layout. Completion requires access to a known-good working Media Foundation H.264 implementation (deskstream, ParsecDisplay, or Microsoft sample) and adapting it to C# COM interop. The engine continues to use JPEG at approximately 15–17 FPS; other features are complete.

## Scope
- In: adapt a provided, working H.264 Media Foundation reference to C# COM interop, then complete the encoder and validate the existing Windows-to-Android stream path.
- Out: further blind MFT activation experiments; completed per-client VDD, ADB, cursor, orientation, reconnect, and null-safety work; general FPS redesign; unrelated Android rendering changes absent a demonstrated decoder blocker.

## Action items
[ ] Obtain access to a known-good, working Media Foundation H.264 encoder reference and the Windows environment on which it successfully activates; this is the blocking dependency.
[ ] Port the reference activation and COM interop to `windows/Screenly.Core/Encoding/H264Encoder.cs`, preserving the reference's activation mechanism, interface definitions/vtable layout, media type negotiation, and any required D3D device manager setup.
[ ] Confirm the capture buffer contract (pixel format, stride, orientation, timestamp), then implement input conversion, sample timestamping, output draining, Annex B framing, keyframe handling, and parameter-set delivery based on the reference.
[ ] Update `windows/Screenly.Engine/Program.cs` to log actionable errors, recover cleanly from runtime failures, advertise the active codec, and retain JPEG fallback when H.264 is unavailable.
[ ] Add focused checks and a Windows integration check for initialization, multiple encoded frames, an IDR and parameter sets; verify end to end with the Android decoder across reconnects and dimension changes, and measure throughput.
[ ] Update README and encoder documentation with the reference source, supported configurations, fallback behavior, and measured validation results.

## Open questions
- Which working reference implementation and corresponding source/build can be provided? This is the blocking dependency.

## Investigation record
- Tried `CoCreateInstance` with the original and corrected CLSIDs, `IUnknown` and `IMFTransform` IIDs, `Marshal.GetObjectForIUnknown`, `GetTypedObjectForIUnknown`, and `QueryInterface`; direct interface casts and queries fail with `E_NOINTERFACE` or cast errors. Direct guessed vtable calls caused `AccessViolationException`.
- `MFTEnumEx` with the standard category finds none. The correct category (`f79eac7d-e545-4387-bdee-d647d7bde42a`) finds 12 without media-type filters; NV12 input, H.264 output, either filter alone, both filters, and all flags (`0x3F`) return none. `MFTEnum` is not exported. Windows SDK headers/WinMD are unavailable in the current environment.
- `Type.GetTypeFromCLSID` plus `Activator.CreateInstance` creates a `__ComObject` that cannot be cast to `IMFTransform`. `IMFActivate.ActivateObject` returns `MF_E_INVALIDMEDIATYPE` (`0xC00D36E6`).
- Public references located earlier ([hardware Media Foundation MFT sample](https://gist.github.com/flyingdove/79b1c16c5bba9ff273ff29714ee60d57), [C++ H.264 encoder](https://github.com/mchudik/mfh264encoder/blob/master/MFEncoderH264.cpp), and [deskstream](https://github.com/erdo-enes/deskstream)) are unverified leads only; they do not establish a working activation path for this environment and do not change the blocked status.
- Related completed/mitigated work: ADB reverse is set up with device-specific serial selection, retries, and a longer setup timeout; auto-reconnect mitigates tunnel drops. Testing indicates the engine remains connected to a PC client, while Android ADB reverse still drops intermittently. VDD interface discovery found the `0000` device path; the ADD IOCTL succeeds with an all-zero payload and creates a display at default resolution. VDD remains environmentally unreliable; primary-screen capture is the fallback. Do not reopen these completed mitigations as part of H.264 work absent new evidence.
