# Plan: Fix Screenly engine flooding the local network

## 1. Analysis

### Symptom
Running `Screenly.Engine.exe` makes the local Wi-Fi unusable: other devices cannot even connect to the router. The machine's Wi-Fi adapter / router is being saturated.

### Root cause
**mDNS self-response amplification loop** in `windows/Screenly.Core/Discovery/MdnsBroadcaster.cs`.

- `AnnounceLoop` multicasts a `BuildResponse()` packet to `224.0.0.251:5353` every 30s (every 2s for the first 5).
- `BuildResponse()` writes `QDCOUNT = 1` with the question `_screenly._tcp.local.` (it echoes a question).
- The same socket is bound to `0.0.0.0:5353`, joined the multicast group, and `ReuseAddress = true`, so it **receives its own multicast**.
- `ListenLoop` calls `ContainsServiceQuery`, which returns `true` whenever a question name contains `_screenly`. Our own announcement satisfies that → it sends another `BuildResponse()` to multicast → which is received again → responds again → **infinite multicast storm**.
- Every response is ~a few hundred bytes; at loop speed this saturates the Wi-Fi radio and the router's multicast handling, breaking connectivity for every other device on the LAN.

Secondary issues:
- The listener responds to **response** packets too (QR bit not checked), so two Screenly instances (or our own echo) amplify each other.
- Binding to port 5353 collides with the Windows DNS Client service (svchost, PID observed on 0.0.0.0:5353). `ReuseAddress` lets us share, but our traffic competes with system mDNS/DNS.
- `GetLocalIp()` opens a UDP socket to `8.8.8.8:65530` on every construction — minor, but an external dependency for a LAN-only feature; if offline it falls back to loopback (wrong IP advertised).

### Why other devices can't connect to the router
The storm is continuous multicast on 224.0.0.251 at maximum socket throughput. Wi-Fi multicast is sent at the lowest basic rate and is not ACKed, so the AP buffers/retries it, starving unicast association/auth traffic. Hence "other devices can't connect."

## 2. Fix (minimal)

Edit `MdnsBroadcaster.cs`:

1. **Break the self-loop.** In `ListenLoop`, only respond to packets whose QR bit is 0 (queries), never to responses. Add an `IsQuery(data)` check before `ContainsServiceQuery`.
2. **Stop echoing a question in our own announcements.** Set `QDCOUNT = 0` in `BuildResponse` when used as an announcement, OR better: build announcements with QDCOUNT=0. Simplest: make `BuildResponse` write QDCOUNT=0 (responses without a question are valid mDNS and still answerable). Then our own announcements won't even look like queries to a naive parser. Keep QR-bit check as the real guard.
3. **Ignore our own source.** Track the socket's local endpoint; drop packets whose source IP equals `_localIp` (belt-and-braces against any remaining self-echo).
4. **Rate-limit responses.** Cap responses to one per query source per 1s (simple timestamp dict) to prevent amplification from a misbehaving peer.
5. **Don't bind the shared system port.** Use a dedicated socket for *sending* announcements (ephemeral source port) and only bind 5353 for *receiving* if needed; or better, keep receive on 5353 but don't set ReuseAddress competition. Minimal: leave receive binding but add the guards above. (Avoid changing port — Android browses 5353.)

No changes to capture, streaming, TCP, or the Android client. The capture/streaming path is not implicated.

## 3. Verification
- Run engine; confirm `netstat`/Wireshark shows no multicast storm (packets only every ~30s).
- Other devices can connect to router while engine runs.
- Android discovery still finds the host.
- No regression: handshake + frame streaming still works.

## 4. Out of scope
- DXGI/H.264 performance (separate).
- mDNS library replacement (keep minimal custom responder).
