# Testing — current verification and automated-test plan

## What verification exists today

1. **Offline target-resolution smoke test** — `tools/check-harmony-targets.py` (metadata-only, no
   game launch): verifies the documented target set — 52 type+method(+signature) rows covering all
   23 components — still exists in the assemblies you pass (standard set in the tool header), and
   reports per patch class. Run it after every game update and before every build. It cannot detect
   runtime inlining or IL-pattern drift, and it checks the documented set, not literally every
   reflected call inside every patch.
2. **Package verification** — `tools/verify-package.ps1`: manifest version, DLL presence/freshness,
   required folders (including `Blueprints/`), and SHA-256 comparison against
   `HANDOFF-MANIFEST.md`.
3. **Boot self-verification** — the mod reports its own health at startup: per-class patch isolation
   (`[Init] Patches applied (N classes)`, any failure named with `[Init][ERR]` while other components
   continue), per-transpiler swap counts, loud `PATTERN NOT FOUND` on IL drift, and runtime arming
   lines per component (`REPRODUCING.md` lists the full expected set).
4. **Field validation** — the primary evidence: two-/three-machine sessions with the built-in
   instrumentation, archived as paired captures and diffed (protocol in `REPRODUCING.md`). Statuses
   in `PATCH-CATALOG.md` reflect exactly what this has and has not proven.
5. **Solo regression** — solo sessions must show no mod arming lines for MP-gated components, no
   `[ERR]` lines, vanilla behavior intact.

## Honest gaps

- No unit tests exist yet. Most components are Harmony patches over engine behavior; meaningful
  automated coverage requires either engine assemblies in a test harness or extraction of pure logic.
- Transpiler pattern checks currently happen only at runtime (swap-count logs). An offline IL-pattern
  check is feasible (the patterns are simple: named getter calls, one float constant) but not built.
- The diagnostics' *comparison* logic (keyed multiset diffs) lives in analysis tooling outside the
  mod, not in tests.

## Automated-test plan (with 0.9)

- **Pure-logic extraction + unit tests:** the P2P framing/parser and transfer state machine (0.9
  items 2–3) are being designed test-first as plain classes with no Unity dependency; the sequenced-
  lock ordinal/baseline logic and the compatibility-latch state machine (items 1, 4) equally.
- **Offline IL assertions:** extend `check-harmony-targets.py` to verify the transpiler patterns
  (e.g. `get_IsInFogOfWar` call counts per target method, the `0.6f` constant in `UnpauseController`)
  against the reference `Code.dll`, so game updates surface as failing checks instead of runtime
  `PATTERN NOT FOUND` logs.
- **Per-component registry with health table** (0.9 item 1) turns the boot self-verification into a
  single structured report suitable for CI-style scraping from logs.

Nothing in this plan claims coverage that does not exist; where a check still requires Unity or a
live multiplayer session, the relevant document says so explicitly.
