# H.264 Encoder Plan — Blocked

**Status: Blocked.** The Media Foundation H.264 Encoder MFT (`mfh264enc.dll`, CLSID `6ca50344-051a-4ded-9779-a43305165e35`) is registered but has not been exposed as a usable `IMFTransform` in this environment. More than 17 activation and enumeration approaches have been tried across sessions, including `CoCreateInstance`, `MFTEnumEx`, `IMFActivate`, managed COM casts and queries, and direct vtable calls. Failures include `E_NOINTERFACE`, zero filtered MFT results, `MF_E_INVALIDMEDIATYPE`, cast failures, and `AccessViolationException`.

The engine continues to use the working JPEG fallback at approximately 15–17 FPS. Other features are complete. Do not repeat activation experiments, guess vtable layouts, or treat MFT activation as a routine implementation task.

## Required prerequisite

Obtain access to a confirmed working C++ or C# Media Foundation H.264 encoder implementation and the Windows environment on which it activates successfully. Candidates named for this purpose are deskstream, ParsecDisplay, or a Microsoft sample. Public code references found so far are unverified against this environment and do not remove the blocker.

## Remaining work after the prerequisite is available

[ ] Reproduce the reference activation path and capture the successful CLSID/MFT selection, interface definitions, initialization order, required media types, and any D3D device manager setup.
[ ] Port the proven activation and COM interop path to `windows/Screenly.Core/Encoding/H264Encoder.cs`; do not infer undocumented vtable layouts.
[ ] Implement capture-buffer conversion, timestamped samples, output draining, Annex B framing, keyframe handling, and parameter-set delivery according to the reference.
[ ] Preserve the JPEG fallback and expose actionable initialization/runtime failures in `windows/Screenly.Engine/Program.cs`.
[ ] Verify multiple encoded frames, IDR and parameter sets, then test end-to-end decoding on Android and record measured throughput.
