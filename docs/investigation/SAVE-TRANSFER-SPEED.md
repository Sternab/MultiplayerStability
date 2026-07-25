# Save Transfer Performance

Investigated on 2026-07-02 using decompiled source and IL from the live game assembly
(build 2026-06-30), then tested in a two-machine co-op session. The logs are retained privately and
can be shared in redacted form. `EVIDENCE-EXCERPTS.md` records SHA-256 hashes for the principal
captures. This is a chronological investigation record; later tests supersede earlier hypotheses.

## Summary

The tested transfer rate was not limited by round-trip latency (measured relay RTT: **27 ms**), and
the app-level constants were not the primary limit. The relevant constraints were:

1. **Photon client flow control:** `PhotonPeer.SequenceDeltaLimitSends = 75` un-acked reliable fragments
   (~ 86 KB in flight per leg; ~1.2 KB/fragment). It's a **public field** (`Photon3Unity3D\...\PhotonPeer.cs:1339`,
   enforced `EnetPeer.cs:738-746`).
2. **Frame-gated pump:** all Photon send/receive/ack happens in `PhotonManager.Update()` once per rendered
   frame, and transfers run during loading screens, when frame time is worst. The window only turns over on
   ack receipt, so effective throughput ~ window / (frame-gated ack loop), measured **~190 fragments/s ~
   0.22 MB/s**: *identical* for 48 KB and 192 KB chunks.
3. **Photon server per-client buffer:** data pushed faster than the receiver drains piles up in the relay;
   past a limit the server **kicks the receiver** (`DisconnectByDisconnectMessage`). This server-side
   limit is not accessible to the mod. The sender must pace against receiver drain.
4. Only then the app-level constants: chunk = `SaveMetaData.MaxPacketSize` (48 KB, public static),
   window = `StreamsController.DefaultStreamsCount` (3, public static), plus a hard-coded `Task.Delay(33)`
   per chunk inside `DataSender.<Send>d__12.MoveNext` (IL_0222). Vanilla 3x48 KB = 144 KB ~ 1.7x the Photon
   window: i.e. **vanilla was already roughly tuned to constraint #1**, and the knobs can't push past it.

## Transfer pipeline

Host uploads a save at co-op session start / load (`SaveNetManager.UploadSave`):

1. **`SavePacker.RepackSaveToSend`** (`SavePacker.cs:85`): re-zips the save: strips screenshots, recompresses
   per-area `.fog` files, rewrites the header. Output = one `byte[]` of the whole save zip.
2. **`SaveSender`: `DataSender`**: chunks the bytes; each chunk is one Photon reliable event (code 31) via
   `PhotonManager.SendMessage` (`PhotonManager.cs:831` -> `OpRaiseEvent`, UDP). Photon fragments each chunk
   into ~1.2 KB reliable commands.
3. **`DataReceiver.OnDataReceived`** (`DataReceiver.cs:72`): copies the chunk at its offset, immediately acks
   (event 30).
4. **`StreamsController`**: sender-side app window: a chunk holds a slot until its app-level ack returns.

Everything relays through Photon Cloud (no P2P). Two windowed legs: host->server and server->client, each
governed by `SequenceDeltaLimitSends` against that leg's acks.

## Field test (2026-07-02, host + client)

**Baseline** (defaults): 15.7 MB save, **69 s, 0.22 MB/s** (host log line 11004). Client log shows one 48 KB
chunk arriving every ~215 ms: despite `rtt=27ms` in the host's `[LogStats]`. The ~190 fragments/s cadence is
the 75-fragment window cycling at loading-screen frame rate, plus the app-level per-chunk ack adding another
frame-gated round trip.

**With `net_packet 192` + `net_streams 6`:** the sender burst all six 192 KB chunks (1.15 MB ~ 960 fragments)
within **1 ms** (host log 21969-21974: the six `Task.Delay(33)` continuations coalesced after a main-thread
hitch during save packing; the sleep provides no pacing under load). The host->server leg drained fast (server
acks at 27 ms RTT), so ~1 MB piled up in the **server's** buffer for the slow-draining receiver. Client
received chunks at the same ~230 KB/s (one 192 KB chunk per ~845 ms: same fragment rate as baseline!), and
after ~2.3 s the server kicked it: `cause=DisconnectByDisconnectMessage` (client log 10132). The host then
  tore the whole session down via `StopPlayingIfLastPlayer` (host log 21976), confirming the
  peer-drop behavior documented in `MOD-PLAN.md`.

**Result:** the cheats do not expose the required controls. Do not repeat the
aggressive values.

*(Unrelated: the error wall in the console was the save-list scan failing on save headers referencing
`DW_Portrait_05` (`affdc2f0...`): saves from the Deathwatch campaign are unreadable without DeathwatchMod
loaded. This is pre-existing and appears only in the development console.)*

## Initial modification plan

1. **Pump the Photon peer on a steady fast cadence during transfers** (both ends; receiver matters most).
   `PhotonManager.Receive()/Send()` are public; drive them every ~5-10 ms while a transfer is active:
   PlayerLoop insertion or a dedicated ticker that doesn't starve during loading screens (the peer is not
   thread-safe: stay on the main thread). With continuous pumping, the ack loop approaches true RTT:
   75 fragments x 1.2 KB / 27 ms ~ **~3 MB/s** without touching anything else.
2. **Raise `PhotonPeer.SequenceDeltaLimitSends`** (public field via
   `m_LoadBalancingClient.LoadBalancingPeer`) on **both** clients, e.g. 75 -> 300 (~ 350 KB in flight).
   Combined with (1): ~12 MB/s potential. Server-side relay throughput will cap somewhere below that:
   find the plateau empirically.
3. **Keep app-level in-flight bounded to ~2x the Photon window**: e.g. chunk 96 KB x streams 4 ~ 384 KB.
   The app-level ack pacing (`StreamsController`) keeps the server buffer bounded. Removing it or flooding
   the relay causes receiver disconnects. Replace the collapse-prone `Task.Delay(33)` with
   drain-aware pacing via the same transpiler target if needed: removing the sleep alone changes nothing
   while (1) and (2) bind.
4. **Compression** (both-clients patch on `SavePacker.RepackSaveToSend`/`RepackReceivedSave`): ~1.3-1.5x
   on top, linear and safe. Optional last step.

Realistic outcome for the tested 15.7 MB save: **69 s -> ~5-15 s** (levers 1+2+3), verified via the same
`Uploading process complete! ... speed=...MB/s` log line.

## Field test 2 (2026-07-02, mod v0.1): server-side transport limit

v0.1 ran exactly as designed on both machines (`allPlayersModded=True`, 96 KB x 4 applied, 8 ms ack pump on;
logs: `Mod Build Logs 0.1.0\`). **Throughput did not move: 16.1 MB in ~71 s ~ 0.22 MB/s.** The decisive
evidence:

- The client received one chunk every **423 ms +/- 5 ms**: machine-regular over 160+ chunks.
- Across all tests the cadence is **linear in chunk size** (48 KB->215 ms, 96 KB->423 ms, 192 KB->845 ms):
  a constant **~230 KB/s byte-rate**, which no frame-timing or window effect can produce.
- The ack pump was active: Photon's UDP socket runs a dedicated receive thread
  (`SocketUdp.ReceiveLoop`, Photon3Unity3D) that queues transport acks as datagrams arrive, so
  `SendAcksOnly()` at 8 ms had fresh acks to flush, and it changed nothing.
- Even the initial 4-chunk burst (sent by the host in one frame) was delivered serialized at 423 ms
  spacing: the relay holds queued data and drips it out.

**Conclusion: Owlcat's Photon Cloud relay paces per-peer reliable-fragment delivery at ~230 KB/s
(~1.9 Mbit/s).** It is a server-side token-bucket-style limit: invariant to chunk size, window depth, and
ack cadence and cannot be changed at the transport level. It explains the earlier results:
the 69 s baseline (15.7 MB / 230 KB/s = 68 s), and the receiver kick in field test 1 (1.15 MB queued
against a 230 KB/s drain).

### Revised options for v0.2 and later

1. **Delta transfer (recommended v0.2):** repeat saves within a session share most zip entries (per-area
   state for unvisited areas doesn't change). Cache the last-transferred save per peer; send a manifest +
   only changed entries; reassemble on receive. Both-sides patch around `SavePacker.RepackSaveToSend` /
   `RepackReceivedSave` or the `SaveSender`/`SaveReceiver` byte arrays. Session start still pays full price
   once; every later load/resync moves ~1-3 MB instead of ~16 MB -> **5-10x on the transfers that hurt**.
2. **Stronger compression:** the save zip is per-entry Deflate; a solid-stream recompress (managed LZMA)
   should get ~1.3-1.6x always-on. This uses the same patch points as (1) and requires both peers.
3. **Steam P2P side channel (v0.3):** stream save bytes directly peer-to-peer via
   Steamworks (`com.rlabrecque.steamworks.net` ships with the game; peers' SteamID64s are already the
   Photon UserIds) with automatic fallback to the vanilla Photon path (non-Steam peers, NAT failure).
   Expected improvement: 10-50x where the network path permits it.
4. *(Noted, not recommended:* striping chunks across extra parallel Photon connections multiplies the
   per-peer cap but burns Owlcat's Photon CCU/traffic budget and adds room-management complexity.)*

v0.1's ack pump and gated window remain enabled. The pump reduces ack latency variation, and the full
16 MB transferred without relay disconnects at 96 KB x 4. Further Photon transport tuning was not pursued.

## Field test 3 (2026-07-02, mod v0.2): Valve send-rate default

Logs: `Mod Build Logs 0.2.0\`. The Steam path ran **end-to-end** (handshake, session accepted, all 15,771 KB
delivered over Steam, `fed to game=True`, host `speed=0.24MB/s` over 65 s). The client loading bar not moving
was the expected cosmetic gap (our receive path did not feed `SaveNetManager.m_Progress`) and also provides
confirmation that the bytes used the Steam path.

The similar Photon and Steam SDR results were consistent with the Steam-side default:
**GameNetworkingSockets does no upward bandwidth estimation by default; connections send
at a fixed configured rate (`k_ESteamNetworkingConfig_SendRateMin/Max`), whose effective default lands at
~256 KB/s.** 245 KB/s measured ~ 256 KB/s minus framing. Games can raise these values; v0.2 did not.

**v0.2.1 (built 2026-07-02):**
- `SteamP2P.EnsureInit` now sets global config before any session opens: `SendRateMin=4 MB/s`,
  `SendRateMax=64 MB/s`, `SendBufferSize=4 MB` (raw `SetConfigValue`, int marshalled by pinned handle:
  this Steamworks.NET version has no Int32 convenience wrapper).
- **Telemetry**: during send, every 2 seconds the host logs `SteamP2P.SessionStats`:
  `GetSessionConnectionInfo` realtime status: negotiated `sendRate=`, actual `wire=` KB/s, `pendingReliable=`
  backlog, and ping. A low configured rate indicates a send-rate limit. A high configured rate with
  low wire throughput and a growing backlog indicates a network-path limit. Compare both transfer
  directions before assigning the limit to either peer.
- Client progress bar fixed: the P2P receive path now Reports into `SaveNetManager.m_Progress` (reflected
  once, used via public `IProgress<DataTransferProgressInfo>`).

Expected result for the send-rate hypothesis: 15.7 MB in approximately 4 seconds or less.

## Field test 4 (2026-07-02, v0.2.1): fixed-rate result

Logs: `Mod Build Logs 0.2.1\`. Telemetry was recorded every 2 seconds for 145 seconds:
`sendRate=4096KB/s wire=4057KB/s pendingReliable~4MB`: Steam obeyed the forced 4 MB/s floor and
transmitted ~575 MB of wire traffic... to deliver 15.7 MB. Goodput **0.11 MB/s, worse than default**: ~97 %
loss, the send buffer pinned full, zero backoff (the floor forbids it).

**Observed rates:**
| Path | Rate | Goodput |
|------|------|---------|
| Photon relay (all tests) | n/a | ~0.22 MB/s |
| Steam SDR, default config (v0.2) | estimator settled ~256 KB/s | ~0.24 MB/s |
| Steam SDR, forced 4 MB/s floor (v0.2.1) | 4 MB/s wire | **0.11 MB/s** (loss collapse) |

This result initially suggested that the path between the two machines carried only ~2 Mbit/s of
sustained UDP. It did not support the earlier Photon relay pacing hypothesis. The logs could not
identify whether the apparent limit came from ISP shaping, Wi-Fi loss, or another network
asymmetry. Field test 5 revised this interpretation.

**v0.2.2 (built):** `SendRateMin` restored to 128 KB/s (let the estimator work), `SendRateMax` raised to
64 MB/s (the relevant configuration change: Valve's default ceiling is ~1 MB/s, which would cap healthy links).
For THIS pair it will settle back at ~245 KB/s because that is the pipe; for typical broadband pairs the
P2P path can now run at link speed. Telemetry stays in.

### Effective options
1. **Network checks:** role-swap test (friend hosts) to localize the slow
   direction; the slow side checks: Wi-Fi -> wired ethernet, router QoS / "UDP flood protection" settings,
   any VPN, ISP upload tier. ~2 Mbit/s sustained UDP with a working 27 ms-RTT path is a shaped or lossy
   last-mile signature.
2. **Send fewer bytes (next mod feature):** delta transfer over the now-working P2P channel: repeat loads
   move ~1-3 MB instead of 16 MB -> ~10 s per load even on this pipe; plus LZMA recompress (~1.4x).

## Field test 5 (2026-07-02, v0.2.2): rate model

Logs: `Mod Build Logs 0.2.2\`. With floor 128 KB/s: `sendRate=128KB/s wire=127-128KB/s`, zero loss, 129 s.
Combined with tests 2-4, this supports the following model for the tested runtime:
**Steam's live send rate = `clamp(256 KB/s, SendRateMin,
SendRateMax)`, fixed for the connection's life.** No upward bandwidth probing was observed in the
tested runtime.
- v0.2 default -> 245 KB/s = the hard-coded 256 KB/s *starting constant* (NOT a measurement of the path)
- v0.2.1 floor 4 MB -> sent 4 MB/s (97 % loss: possibly the SDR relay policing, possibly the path; unknown)
- v0.2.2 floor 128 KB -> sent 128 KB/s clean

Both test peers verified on fast connections (92 ms ping = transatlantic distance, not a bandwidth cap).
**Field test 4 did not establish a 2 Mbit path limit.** The path had not been tested between
256 KB/s and 4 MB/s. The loss cliff at 4 MB/s may be Valve relay policing rather than the link.

**v0.2.3 (built):** adds:
1. **Adaptive rate controller** in `SendToPeer`: start 1 MB/s, every ~2 s compare delivered goodput
   (queued - pendingReliable) against the current rate: >=80 % means double (cap 16 MB/s), <50 % means halve
   (floor 256 KB/s), applied live via `SendRateMin` (`SetSendRateFloor`). Telemetry logs
   `goodput= rateCtl= wire= pending= via [...]` every step, including through the buffer-drain tail.
2. **ICE direct P2P enabled** (`P2P_Transport_ICE_Enable=All`): sessions may now negotiate a direct UDP
   path (automatic NAT punch: no manual port forwarding needed) instead of Valve's SDR relay; the
   `via [...]` field in the telemetry names the transport actually in use, which also settles the
   relay-policing question.

Expected behavior: `rateCtl` increases until measured goodput stops improving, then backs off to a
stable rate.

## Field test 6 (2026-07-02, v0.2.3): 8.33 seconds at 1.85 MB/s

Logs: `Mod Build Logs 0.2.3\`. The session negotiated a **direct ICE peer-to-peer connection** (telemetry:
`via [#... P2P ICE steamid:... msg vport]`): no relay in the path, NAT punched automatically (no port
forwarding). The adaptive controller ramped 1024 -> 2048 -> 4096 KB/s with goodput tracking each step and the
transfer finished mid-ramp, so larger saves cruise at higher rates for most of their bytes. Live
`SetConfigValue(SendRateMin)` propagation to an open session confirmed working.

**Result summary:** Photon's relay delivered approximately 230 KB/s per peer in these tests. Valve's
transport used a fixed `clamp(256 KB/s, SendRateMin, SendRateMax)` rate with no upward probing, so the
mod supplies an AIMD controller. The forced 4 MB/s SDR test lost 97 percent of traffic, while the
direct ICE path delivered the save in 8.33 seconds.

## Implementation status

**v0.2: Steam P2P side channel (built 2026-07-02):** routes only bulk save bytes over Steam
Networking Messages, using ICE direct where available and SDR otherwise. Photon remains the control
plane and fallback.

Files (`Assets\Modifications\MultiplayerStability\Scripts\`):
- `SteamP2P.cs`: the only file touching Steamworks: init/identity gate (`StoreManager.Store==Steam` +
  probe `SteamUser.GetSteamID()`), reliable `SendMessageToUser` on channel 47, a per-frame receive pump
  (`SteamNetworkingMessagesPump` MonoBehaviour -> `ReceiveMessagesOnChannel`), and session-request callbacks
  that accept **only** SteamIDs we're actively expecting (anti-spoof).
- `SteamSaveTransfer.cs`: Harmony seams + orchestration. Host: prefix on `DataTransporter.SendSave`
  (`DataTransporter.cs:122`) replaces the returned Task with the P2P send. Handshake over free Photon code
  100 (prefix on `MessageNetManager.OnMessage`, `MessageNetManager.cs:9`): host QUERYs targets, each
  Steam+modded target PONGs its real SteamID64 (so we only ever open a Steam session to an ID the peer itself
  asserted over the trusted Photon channel: no sends to strangers). Receiver reassembles chunks and
  completes the vanilla download by reflection-setting `SaveNetManager.m_DownloadSaveTcs`, which is the
  *only* thing `DownloadSave` awaits (`SaveNetManager.cs:359`), so no fake receiver, no ack suppression. On
  full receipt it COMPLETEs back to the host over Photon so the host's `SendSave` Task finishes.

Requirements verified: package `com.rlabrecque.steamworks.net#20.0.0` is in `Packages/manifest.json` and
compiled; added it to `MultiplayerStability.asmdef` references. All game/Steam members dnfile-checked against
the template assemblies. Photon UserId == SteamID64 on Steam (`SteamAuthenticationService.cs:66`).

**Fallback behavior:** a P2P failure returns the transfer to the vanilla Photon path. Non-Steam or
non-modded peer, Steam init failure, handshake timeout (3 s), session-failed callback, or any send error ->
the host transparently re-invokes the original `SendSave` (re-entrancy-guarded) and the vanilla Photon path
delivers; `TrySetResult` is idempotent so a Photon delivery racing a partial P2P delivery is benign. v0.1's
`TransferBooster` stays as the fallback's fallback (boosted, ack-pumped Photon).

Expected: a 15.7 MB save decreases from 69 seconds to a few seconds on two Steam clients. Measure with the
`Uploading process complete! ... speed=...MB/s` line, plus new `[MPStability][Transfer]` P2P log lines.

Initial validation risks: Harmony binding the Span-parameter `OnMessage` prefix
(Harmony 2.2.2 supports it; if `[Init] Patches applied.` is missing, the log will show the exception); the mod
compiling against Steamworks 20.0.0 but binding the game's newer runtime DLL (the same binding pattern used
for Code.dll; only leading struct fields are read); asmdef package reference resolution in the editor; and whether SDR P2P
actually connects between the two machines (else session-failed -> fallback).

---

**v0.1: TransferBooster (built 2026-07-02)**: `TransferBooster.cs`:
lever 1 as an 8 ms `SendAcksOnly()` timer during transfers (starts/stops via Harmony prefix+finalizer on
`SaveNetManager.UploadSave`/`DownloadSave`); lever 3 as 96 KB x 4, gated on every room player advertising
`MultiplayerStability` in the Photon mod-list property, vanilla values restored after each transfer.
Lever 2 (`SequenceDeltaLimitSends`) deliberately deferred: host->server already runs at 27 ms RTT and the
server->client leg's window is server-owned. Measure v0.1 first with the
`Uploading process complete! ... speed=...MB/s` log line.

### Safety constraints

- The server per-client buffer kick is the failure mode to design against: always ack-paced, in-flight
  bounded, all values tunable at runtime (F9 tuning) so plateau-hunting can't strand a session.
- Both clients need the mod for lever 2 (receiver-side window matters for its ack behaviour and any reverse
  transfers); lever 1 helps even installed on one side (whichever end pumps faster improves the loop).
- Re-verify all targets after each game patch (`tools/check-harmony-targets.py` covers the documented
  target set; an IL-dump helper used during the original investigation is retained privately).
