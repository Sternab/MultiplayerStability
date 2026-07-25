# Save-Transfer Speed — Root Cause & Fix Plan

*Investigated 2026-07-02 against the decompiled source (the decompiled game source), verified by
IL-disassembly of the **live** game assembly (build 2026-06-30), and **field-tested the same day** in a real
two-machine co-op session (logs: `GameLogFull-Host.txt` / `GameLogFull-Client.txt`, retained by the author
and available on request in redacted form; `EVIDENCE-EXCERPTS.md` binds the strongest captures by SHA-256).
The field
test changed the conclusions — the app-level knobs alone do NOT work; see §Field test.*

## TL;DR (revised after field test)

Transfer speed has nothing to do with your internet connection (measured relay RTT: **27 ms**), but the
app-level constants are not the real ceiling either. The binding constraints, outermost first:

1. **Photon client flow control:** `PhotonPeer.SequenceDeltaLimitSends = 75` un-acked reliable fragments
   (≈ 86 KB in flight per leg; ~1.2 KB/fragment). It's a **public field** (`Photon3Unity3D\...\PhotonPeer.cs:1339`,
   enforced `EnetPeer.cs:738-746`).
2. **Frame-gated pump:** all Photon send/receive/ack happens in `PhotonManager.Update()` once per rendered
   frame — and transfers run during loading screens, when frame time is worst. The window only turns over on
   ack receipt, so effective throughput ≈ window / (frame-gated ack loop), measured **~190 fragments/s ≈
   0.22 MB/s** — *identical* for 48 KB and 192 KB chunks.
3. **Photon server per-client buffer:** data pushed faster than the receiver drains piles up in the relay;
   past a limit the server **kicks the receiver** (`DisconnectByDisconnectMessage`). This is the hard wall —
   it is server-side and cannot be modded. Everything must pace to receiver drain.
4. Only then the app-level constants: chunk = `SaveMetaData.MaxPacketSize` (48 KB, public static),
   window = `StreamsController.DefaultStreamsCount` (3, public static), plus a hard-coded `Task.Delay(33)`
   per chunk inside `DataSender.<Send>d__12.MoveNext` (IL_0222). Vanilla 3×48 KB = 144 KB ≈ 1.7× the Photon
   window — i.e. **vanilla was already roughly tuned to constraint #1**, and the knobs can't push past it.

## The transfer pipeline

Host uploads a save at co-op session start / load (`SaveNetManager.UploadSave`):

1. **`SavePacker.RepackSaveToSend`** (`SavePacker.cs:85`) — re-zips the save: strips screenshots, recompresses
   per-area `.fog` files, rewrites the header. Output = one `byte[]` of the whole save zip.
2. **`SaveSender` : `DataSender`** — chunks the bytes; each chunk is one Photon reliable event (code 31) via
   `PhotonManager.SendMessage` (`PhotonManager.cs:831` → `OpRaiseEvent`, UDP). Photon fragments each chunk
   into ~1.2 KB reliable commands.
3. **`DataReceiver.OnDataReceived`** (`DataReceiver.cs:72`) — copies the chunk at its offset, immediately acks
   (event 30).
4. **`StreamsController`** — sender-side app window: a chunk holds a slot until its app-level ack returns.

Everything relays through Photon Cloud (no P2P). Two windowed legs: host→server and server→client, each
governed by `SequenceDeltaLimitSends` against that leg's acks.

## Field test (2026-07-02, host + client)

**Baseline** (defaults): 15.7 MB save, **69 s, 0.22 MB/s** (host log line 11004). Client log shows one 48 KB
chunk arriving every ~215 ms — despite `rtt=27ms` in the host's `[LogStats]`. The ~190 fragments/s cadence is
the 75-fragment window cycling at loading-screen frame rate, plus the app-level per-chunk ack adding another
frame-gated round trip.

**With `net_packet 192` + `net_streams 6`:** the sender burst all six 192 KB chunks (1.15 MB ≈ 960 fragments)
within **1 ms** (host log 21969-21974 — the six `Task.Delay(33)` continuations coalesced after a main-thread
hitch during save packing; the sleep provides no pacing under load). The host→server leg drained fast (server
acks at 27 ms RTT), so ~1 MB piled up in the **server's** buffer for the slow-draining receiver. Client
received chunks at the same ~230 KB/s (one 192 KB chunk per ~845 ms — same fragment rate as baseline!), and
after ~2.3 s the server kicked it: `cause=DisconnectByDisconnectMessage` (client log 10132). The host then
tore the whole session down via `StopPlayingIfLastPlayer` (host log 21976) — live confirmation of the
peer-drop fragility hazard (the transfer routing guard in MOD-PLAN's open backlog would have saved the
session).

**Verdict:** the cheats cannot reach the real levers. No further cheat experiments needed — do not re-run the
aggressive values.

*(Unrelated: the error wall in the console was the save-list scan failing on save headers referencing
`DW_Portrait_05` (`affdc2f0…`) — saves from the Deathwatch campaign are unreadable without DeathwatchMod
loaded. Pre-existing, harmless to co-op, just visible in the dev console.)*

## Revised mod plan (MultiplayerStability), in order of leverage

1. **Pump the Photon peer on a steady fast cadence during transfers** (both ends; receiver matters most).
   `PhotonManager.Receive()/Send()` are public; drive them every ~5-10 ms while a transfer is active —
   PlayerLoop insertion or a dedicated ticker that doesn't starve during loading screens (the peer is not
   thread-safe: stay on the main thread). With continuous pumping, the ack loop approaches true RTT:
   75 fragments × 1.2 KB / 27 ms ≈ **~3 MB/s** without touching anything else.
2. **Raise `PhotonPeer.SequenceDeltaLimitSends`** (public field via
   `m_LoadBalancingClient.LoadBalancingPeer`) on **both** clients, e.g. 75 → 300 (≈ 350 KB in flight).
   Combined with (1): ~12 MB/s potential. Server-side relay throughput will cap somewhere below that —
   find the plateau empirically.
3. **Keep app-level in-flight bounded to ~2× the Photon window** — e.g. chunk 96 KB × streams 4 ≈ 384 KB.
   The app-level ack pacing (StreamsController) is what keeps the *server* buffer bounded; never remove it,
   never flood it (that's what got the receiver kicked). Replace the collapse-prone `Task.Delay(33)` with
   drain-aware pacing via the same transpiler target if needed — removing the sleep alone changes nothing
   while (1) and (2) bind.
4. **Compression** (both-clients patch on `SavePacker.RepackSaveToSend`/`RepackReceivedSave`): ~1.3-1.5×
   on top, linear and safe. Optional last step.

Realistic outcome for the tested 15.7 MB save: **69 s → ~5-15 s** (levers 1+2+3), verified via the same
`Uploading process complete! ... speed=…MB/s` log line.

## Field test 2 (2026-07-02, mod v0.1) — the transport ceiling is server-side

v0.1 ran exactly as designed on both machines (`allPlayersModded=True`, 96 KB × 4 applied, 8 ms ack pump on;
logs: `Mod Build Logs 0.1.0\`). **Throughput did not move: 16.1 MB in ~71 s ≈ 0.22 MB/s.** The decisive
evidence:

- The client received one chunk every **423 ms ± 5 ms** — machine-regular over 160+ chunks.
- Across all tests the cadence is **linear in chunk size** (48 KB→215 ms, 96 KB→423 ms, 192 KB→845 ms):
  a constant **~230 KB/s byte-rate**, which no frame-timing or window effect can produce.
- The ack pump was genuinely working: Photon's UDP socket runs a dedicated receive thread
  (`SocketUdp.ReceiveLoop`, Photon3Unity3D) that queues transport acks as datagrams arrive, so
  `SendAcksOnly()` at 8 ms had fresh acks to flush — and it changed nothing.
- Even the initial 4-chunk burst (sent by the host in one frame) was delivered serialized at 423 ms
  spacing: the relay holds queued data and drips it out.

**Conclusion: Owlcat's Photon Cloud relay paces per-peer reliable-fragment delivery at ~230 KB/s
(~1.9 Mbit/s).** It is a server-side token-bucket-style limit — invariant to chunk size, window depth, and
ack cadence — and cannot be modded around at the transport level. It retroactively explains everything:
the 69 s baseline (15.7 MB ÷ 230 KB/s = 68 s), and the receiver kick in field test 1 (1.15 MB queued
against a 230 KB/s drain).

### Revised levers (v0.2+) — reduce bytes or leave the relay

1. **Delta transfer (recommended v0.2):** repeat saves within a session share most zip entries (per-area
   state for unvisited areas doesn't change). Cache the last-transferred save per peer; send a manifest +
   only changed entries; reassemble on receive. Both-sides patch around `SavePacker.RepackSaveToSend` /
   `RepackReceivedSave` or the `SaveSender`/`SaveReceiver` byte arrays. Session start still pays full price
   once; every later load/resync moves ~1-3 MB instead of ~16 MB → **5-10× on the transfers that hurt**.
2. **Stronger compression:** the save zip is per-entry Deflate; a solid-stream recompress (managed LZMA)
   should get ~1.3-1.6× always-on. Cheap add-on to (1), same patch points, also both-sides.
3. **Steam P2P side-channel (v0.3, the real fix):** stream save bytes directly peer-to-peer via
   Steamworks (`com.rlabrecque.steamworks.net` ships with the game; peers' SteamID64s are already the
   Photon UserIds) with automatic fallback to the vanilla Photon path (non-Steam peers, NAT failure).
   Full-bandwidth transfers, 10-50×.
4. *(Noted, not recommended:* striping chunks across extra parallel Photon connections multiplies the
   per-peer cap but burns Owlcat's Photon CCU/traffic budget and adds room-management complexity.)*

v0.1's ack pump and gated window stay in (harmless; the pump still trims ack latency edges, and the whole
16 MB moved with zero relay kicks at 96 KB × 4), but transport tuning is now a closed chapter.

## Field test 3 (2026-07-02, mod v0.2) — P2P worked, still 0.24 MB/s → Valve's default send-rate cap

Logs: `Mod Build Logs 0.2.0\`. The Steam path ran **end-to-end** (handshake, session accepted, all 15,771 KB
delivered over Steam, `fed to game=True`, host `speed=0.24MB/s` over 65 s). The client loading bar not moving
was the expected cosmetic gap (our receive path didn't feed `SaveNetManager.m_Progress`) — and it doubles as
proof the bytes really took the Steam path.

Two independent transports (Photon relay, Steam SDR) both landing at ~230-245 KB/s has a clean explanation
for the Steam side: **GameNetworkingSockets does no real bandwidth estimation by default — connections send
at a fixed configured rate (`k_ESteamNetworkingConfig_SendRateMin/Max`), whose effective default lands at
~256 KB/s.** 245 KB/s measured ≈ 256 KB/s minus framing. Games are expected to raise these; we never did.

**v0.2.1 (built 2026-07-02):**
- `SteamP2P.EnsureInit` now sets global config before any session opens: `SendRateMin=4 MB/s`,
  `SendRateMax=64 MB/s`, `SendBufferSize=4 MB` (raw `SetConfigValue`, int marshalled by pinned handle —
  this Steamworks.NET version has no Int32 convenience wrapper).
- **Decisive telemetry**: during send, every 2 s the host logs `SteamP2P.SessionStats` —
  `GetSessionConnectionInfo` realtime status: negotiated `sendRate=`, actual `wire=` KB/s, `pendingReliable=`
  backlog, ping. Whatever the next run does, this line names the limiter: `sendRate≈256` before-fix values →
  theory confirmed & fixed; `sendRate` high but `wire` low + backlog growing → a real link limit between the
  two machines (then measure the reverse direction / check the receiving side's connection).
- Client progress bar fixed: the P2P receive path now Reports into `SaveNetManager.m_Progress` (reflected
  once, used via public `IProgress<DataTransferProgressInfo>`).

Expected if the theory holds: 15.7 MB in **~4 s** (SendRateMin floor) or better.

## Field test 4 (2026-07-02, v0.2.1) — VERDICT: the link itself is ~2 Mbit/s

Logs: `Mod Build Logs 0.2.1\`. The telemetry closed the case, one line every 2 s for 145 s:
`sendRate=4096KB/s wire=4057KB/s pendingReliable≈4MB` — Steam obeyed our forced 4 MB/s floor and genuinely
transmitted ~575 MB of wire traffic… to deliver 15.7 MB. Goodput **0.11 MB/s, worse than default**: ~97 %
loss, the send buffer pinned full, zero backoff (the floor forbids it).

**Final picture — three transports agree:**
| Path | Rate | Goodput |
|------|------|---------|
| Photon relay (all tests) | — | ~0.22 MB/s |
| Steam SDR, default config (v0.2) | estimator settled ~256 KB/s | ~0.24 MB/s |
| Steam SDR, forced 4 MB/s floor (v0.2.1) | 4 MB/s wire | **0.11 MB/s** (loss collapse) |

Valve's bandwidth estimator was *right* all along: **the network path between these two machines carries
only ~2 Mbit/s of sustained UDP.** The earlier "Photon relay pacing" theory is retired — the relay was
never the limiter for this pair; the link is. (Whether it's one side's ISP shaping, Wi-Fi loss, or plan
asymmetry is not determinable from logs — see checklist below.)

**v0.2.2 (built):** `SendRateMin` restored to 128 KB/s (let the estimator work), `SendRateMax` raised to
64 MB/s (the *real* config win — Valve's default ceiling is ~1 MB/s, which would cap healthy links).
For THIS pair it will settle back at ~245 KB/s because that is the pipe; for typical broadband pairs the
P2P path can now run at link speed. Telemetry stays in.

### What actually helps this pair
1. **Physical/network checklist (the real fix):** role-swap test (friend hosts) to localize the slow
   direction; the slow side checks — Wi-Fi → wired ethernet, router QoS / "UDP flood protection" settings,
   any VPN, ISP upload tier. ~2 Mbit/s sustained UDP with a working 27 ms-RTT path is a shaped or lossy
   last-mile signature.
2. **Send fewer bytes (next mod feature):** delta transfer over the now-working P2P channel — repeat loads
   move ~1-3 MB instead of 16 MB → ~10 s per load even on this pipe; plus LZMA recompress (~1.4×).

## Field test 5 (2026-07-02, v0.2.2) — the rate model, finally exact

Logs: `Mod Build Logs 0.2.2\`. With floor 128 KB/s: `sendRate=128KB/s wire=127-128KB/s`, zero loss, 129 s.
Combined with tests 2-4 this proves the exact model: **Steam's live send rate = `clamp(256 KB/s, SendRateMin,
SendRateMax)`, fixed for the connection's life — Valve ships no upward bandwidth probing at all.**
- v0.2 default → 245 KB/s = the hard-coded 256 KB/s *starting constant* (NOT a measurement of the path)
- v0.2.1 floor 4 MB → sent 4 MB/s (97 % loss — possibly the SDR relay policing, possibly the path; unknown)
- v0.2.2 floor 128 KB → sent 128 KB/s clean

Both test peers verified on fast connections (92 ms ping = transatlantic distance, not a bandwidth cap).
**The "~2 Mbit path" theory from test 4 is retired as unproven** — the path has never been *tested* between
256 KB/s and 4 MB/s. The loss cliff at 4 MB/s may be Valve relay policing rather than the link.

**v0.2.3 (built):** the mod now does what Valve didn't:
1. **Adaptive rate controller** in `SendToPeer`: start 1 MB/s, every ~2 s compare delivered goodput
   (queued − pendingReliable) against the current rate — ≥80 % ⇒ double (cap 16 MB/s), <50 % ⇒ halve
   (floor 256 KB/s), applied live via `SendRateMin` (`SetSendRateFloor`). Telemetry logs
   `goodput= rateCtl= wire= pending= via [...]` every step, including through the buffer-drain tail.
2. **ICE direct P2P enabled** (`P2P_Transport_ICE_Enable=All`): sessions may now negotiate a direct UDP
   path (automatic NAT punch — no manual port forwarding needed) instead of Valve's SDR relay; the
   `via [...]` field in the telemetry names the transport actually in use, which also settles the
   relay-policing question.

Expected outcomes: `rateCtl` climbs until it finds the real ceiling — either multi-MB/s (done: ~5-15 s
transfers) or a clean plateau that finally *names* this pair's true capacity with loss-free evidence.

## Field test 6 (2026-07-02, v0.2.3) — **RESOLVED: 8.33 s, 1.85 MB/s (was 69 s)**

Logs: `Mod Build Logs 0.2.3\`. The session negotiated a **direct ICE peer-to-peer connection** (telemetry:
`via [#... P2P ICE steamid:... msg vport]`) — no relay in the path, NAT punched automatically (no port
forwarding). The adaptive controller ramped 1024 → 2048 → 4096 KB/s with goodput tracking each step and the
transfer finished mid-ramp, so larger saves cruise at higher rates for most of their bytes. Live
`SetConfigValue(SendRateMin)` propagation to an open session confirmed working.

**Final root-cause summary of the whole saga:** (a) Photon's relay delivers ~230 KB/s per peer; (b) Valve's
transport sends at a fixed `clamp(256 KB/s, SendRateMin, SendRateMax)` — no bandwidth probing exists, the
mod supplies its own AIMD controller; (c) relays police bulk traffic (the 4 MB/s forced-floor test lost 97 %
through the SDR relay), direct ICE connections don't. Case closed.

## Implementation status

**v0.2 — Steam P2P side-channel (built 2026-07-02)** — the real fix for the relay cap. Routes only the bulk
save bytes peer-to-peer over Steam's SDR network; Photon stays the control plane and the fallback.

Files (`Assets\Modifications\MultiplayerStability\Scripts\`):
- `SteamP2P.cs` — the only file touching Steamworks: init/identity gate (`StoreManager.Store==Steam` +
  probe `SteamUser.GetSteamID()`), reliable `SendMessageToUser` on channel 47, a per-frame receive pump
  (`SteamNetworkingMessagesPump` MonoBehaviour → `ReceiveMessagesOnChannel`), and session-request callbacks
  that accept **only** SteamIDs we're actively expecting (anti-spoof).
- `SteamSaveTransfer.cs` — Harmony seams + orchestration. Host: prefix on `DataTransporter.SendSave`
  (`DataTransporter.cs:122`) replaces the returned Task with the P2P send. Handshake over free Photon code
  100 (prefix on `MessageNetManager.OnMessage`, `MessageNetManager.cs:9`): host QUERYs targets, each
  Steam+modded target PONGs its real SteamID64 (so we only ever open a Steam session to an ID the peer itself
  asserted over the trusted Photon channel — no sends to strangers). Receiver reassembles chunks and
  completes the vanilla download by reflection-setting `SaveNetManager.m_DownloadSaveTcs` — which is the
  *only* thing `DownloadSave` awaits (`SaveNetManager.cs:359`), so no fake receiver, no ack suppression. On
  full receipt it COMPLETEs back to the host over Photon so the host's `SendSave` Task finishes.

Requirements verified: package `com.rlabrecque.steamworks.net#20.0.0` is in `Packages/manifest.json` and
compiled; added it to `MultiplayerStability.asmdef` references. All game/Steam members dnfile-checked against
the template assemblies. Photon UserId == SteamID64 on Steam (`SteamAuthenticationService.cs:66`).

**Fallback is total and safe** (a P2P failure = "slow like before", never a broken session): non-Steam or
non-modded peer, Steam init failure, handshake timeout (3 s), session-failed callback, or any send error →
the host transparently re-invokes the original `SendSave` (re-entrancy-guarded) and the vanilla Photon path
delivers; `TrySetResult` is idempotent so a Photon delivery racing a partial P2P delivery is benign. v0.1's
`TransferBooster` stays as the fallback's fallback (boosted, ack-pumped Photon).

Expected: 15.7 MB save **69 s → a few seconds** on two Steam clients. Success metric unchanged: the
`Uploading process complete! … speed=…MB/s` line, plus new `[MPStability][Transfer]` P2P log lines.

Top risks to watch on first run (all fail safe to vanilla): Harmony binding the Span-param `OnMessage` prefix
(Harmony 2.2.2 supports it — if `[Init] Patches applied.` is missing, the log will show the throw); the mod
compiling against Steamworks 20.0.0 but binding the game's newer runtime DLL (same proven pattern as Code.dll
— only leading struct fields are read); asmdef package reference resolving in-editor; and whether SDR P2P
actually connects between the two machines (else session-failed → fallback).

---

**v0.1 — TransferBooster (built 2026-07-02)** — `TransferBooster.cs`:
lever 1 as an 8 ms `SendAcksOnly()` timer during transfers (starts/stops via Harmony prefix+finalizer on
`SaveNetManager.UploadSave`/`DownloadSave`); lever 3 as 96 KB × 4, gated on every room player advertising
`MultiplayerStability` in the Photon mod-list property, vanilla values restored after each transfer.
Lever 2 (`SequenceDeltaLimitSends`) deliberately deferred: host→server already runs at 27 ms RTT and the
server→client leg's window is server-owned — measure v0.1 first. Success metric unchanged: the
`Uploading process complete! … speed=…MB/s` log line.

### Safety rails

- The server per-client buffer kick is the failure mode to design against: always ack-paced, in-flight
  bounded, all values tunable at runtime (F9 tuning) so plateau-hunting can't strand a session.
- Both clients need the mod for lever 2 (receiver-side window matters for its ack behaviour and any reverse
  transfers); lever 1 helps even installed on one side (whichever end pumps faster improves the loop).
- Re-verify all targets after each game patch (`tools/check-harmony-targets.py` covers the documented
  target set; an IL-dump helper used during the original investigation is retained privately).
