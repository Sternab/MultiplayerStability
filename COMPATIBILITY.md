# Compatibility

## Game builds
Developed and tested against the live Steam build of *Warhammer 40,000: Rogue Trader* as of July 2026
(engine source cross-checked against a decompile refreshed 2026-06-25). Every Harmony target is fail-open:
after a game patch, a missing target logs `[ERR] ... not found` / `PATTERN NOT FOUND` at boot and that
component reverts to vanilla behavior instead of crashing. **Check the log after every game update.**

## Player counts
- 2 players: extensively field-tested (the primary development configuration).
- 3 players: field-tested (transfer, barriers, and all seams verified in session captures).
- 4–6 players: supported by design (engine cap is 6; all fixes are player-count-agnostic; save transfer is
  sequential per peer at ~8 s each) — not yet field-tested.

## Platform
- **Steam (both/all peers):** full functionality including fast P2P save transfer.
- **GOG (any peer):** fast transfer disables itself (Steam networking unavailable); everything else works.
  Transfers fall back to vanilla Photon speed.

## Version mixing
See the peer-compatibility table in `README.md`. Until the 0.9 session-latched gate ships: **identical mod
version on every machine.** The mod's own peer check matches mod *identity*, not version — a mixed-version
lobby will not warn you.

## Other mods
- Coexists with OwlcatModification and UnityModManager mods (tested alongside ToyBox, MicroPatches, and the
  Deathwatch chargen mod in solo).
- Any *gameplay-affecting* mod must itself be installed identically on all peers — lockstep applies to the
  whole process, not just this mod. Content mods with client-side-only behavior (UI, cosmetics) are fine
  asymmetric.
- Known interop note: units whose weapon views differ across machines (e.g. from asymmetric content mods)
  can desync via the engine's own view-dependent projectile paths — symmetric installs avoid this.
