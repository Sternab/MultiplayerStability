# Roadmap 0.9

This plan is not implemented in v0.8.32. It contains five ordered changes, each intended for a
separate reviewed commit with its own tests and rollback boundary. **New gameplay fixes are frozen
while this series is in progress.** The objective is to harden the existing behavior. See
`PATCH-CATALOG.md` for per-component validation status.

## 1. Session-latched compatibility state, component registry, lifecycle epochs

- A compatibility latch evaluated **only at simulation epochs** (initial launch and save-transfer
  relaunch, with a joiner accounted for before `PlayersReadyMask`), never per patch call and never changed
  mid-session. If every peer runs the exact compatible build, simulation-changing fixes enable;
  otherwise **all modded peers stay on vanilla behavior** and log one clear warning.
- Diagnostics and UI-only fixes remain active regardless (see the peer-compatibility categories in
  `PATCH-CATALOG.md`).
- A component registry with per-component applied/failed/inert status and a startup health table
  (replacing ad-hoc activation lines as the primary health signal).
- Lifecycle resets for all session-scoped diagnostic state (rings, episode flags, tick baselines) at
  room join/leave and save load, with tick-regression guards.
- Motivating evidence: vanilla mod-parity checking is dead code; mixed-version installs of
  simulation-changing fixes range from ineffective to actively desync-causing; the vanilla lobby mod
  list is a boot-time file snapshot (`ActiveUMMItemsInfo.txt`) and cannot be trusted for this purpose.

## 2. P2P wire framing with parser tests

- The Steam-transfer control messages (Photon event code 100) currently consume every code-100 event
  and carry no magic, protocol version, transfer ID, sequence number, or checksum; peer-supplied sizes
  are allocated without validation.
- Add framing (magic + version + transfer ID + length validation), reject malformed/stale frames, and
  cover the parser/state machine with plain unit tests (no Unity dependency).

## 3. Transfer completion ACK/NACK and per-peer fallback

- A completion ACK must mean "the game accepted the bytes." Today `MsgComplete` can be sent when the
  download TCS is absent or `TrySetResult` failed, so the host can believe a failed delivery succeeded.
  Introduce NACK for delivery failure.
- Multi-peer fallback must track per-peer completion: a late failure currently re-sends via vanilla to
  the *original full target list*, including peers that already completed over P2P.

## 4. Sequenced-lock retry, timeout, abort

- Barrier announcements are one-shot with no retry or acknowledgement; activation is recalculated
  dynamically and matches mod identity only, not version. Latch activation per session; add
  retry/timeout/abort behavior so a lost announcement cannot become a permanent loading hang.

## 5. Tick-identity-checked desync inference; per-call-site LeakDetector accounting

- Bucket attribution currently accepts the first matching 32-bit hash anywhere in a 128-entry ring;
  field data shows the inferred `senderTick` differing by +/-40 ticks between peers for the same
  mismatch. Inference this ambiguous must not drive user-facing suppression decisions; add tick
  identity checks and treat ambiguous attribution as "unknown".
- LeakDetector's warning budget is per *stream*; six identical startup warnings have exhausted a
  stream's budget and muted later, different call sites. Account per call site.
- Also in scope: audit all `static readonly AccessTools.FieldRefAccess` initializers (move to guarded
  `Prepare()` where a failed resolution would otherwise break the patched method), per the trap-fix
  precedent.

## Implementation requirements

- Reset diagnostics at **accepted transitions**, never requests (requests can reject, duplicate, or
  defer; only accepted transitions are synchronized across peers).
- Budget diagnostics around the **comparison**, not the event. Successful calls are required for
  cross-peer comparison.
- Unique record keys under batching (per-tick per-entity ordinals).
- Semantic discriminators over temporal ones (which caller, never "was it inside a tick").
- Keep activation logging outside fail-open `try` blocks.
- A validator installed by the same patch class as the target change cannot independently prove that
  the class installed.
