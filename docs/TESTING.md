# Testing

## What verification exists today

1. **Offline target-resolution smoke test:** `tools/check-harmony-targets.py` (metadata-only, no
   game launch) verifies that the documented target set still exists in the supplied assemblies.
   The set contains 57 rows covering all 23 components: 55 patch targets and 2 reflection
   dependencies. The standard assembly list is in the tool header. Rows with recorded parameter types
   are matched against the decoded metadata
   signature; inherited targets are resolved through the base-type chain like `AccessTools.Method`.
   Skipped targets (type in an assembly you did not pass) **fail the run** unless `--allow-skip`;
   `--reconcile Scripts` scans the source for patch sites and fails if any is missing from the
   table. Run it after every game update and before every build. It cannot detect runtime inlining
   or IL-pattern drift. Requires Python 3 with `dnfile==0.18.0` (`pip install dnfile==0.18.0`).
2. **Comment-only verification:** `tools/verify-comments-only.py` tokenizes every `.cs` file changed in a
   git range, strips comments, and compares, proving a documentation pass changed no executable
   statement. Use it for documentation-only source changes, including the current
   `v0.8.32..HEAD` delta.
3. **Package verification:** `tools/verify-package.ps1` checks manifest version, DLL presence and freshness,
   required folders (including `Blueprints/`), and SHA-256 comparison against
   `RELEASE-0.8.32.md`.
4. **Boot verification:** the mod reports its patch status at startup: per-class patch isolation
   (`[Init] Patches applied (N classes)`, any failure named with `[Init][ERR]` while other components
   continue), per-transpiler swap counts, `PATTERN NOT FOUND` on IL drift, and runtime activation
   lines per component (`REPRODUCING.md` lists the full expected set).
5. **Field validation:** two-machine and three-machine sessions use the built-in
   instrumentation, archived as paired captures and diffed (protocol in `REPRODUCING.md`). Statuses
   in `PATCH-CATALOG.md` reflect exactly what this has and has not proven.
6. **Solo regression:** solo sessions must show no mod activation lines for MP-gated components, no
   `[ERR]` lines, vanilla behavior intact.

## Current gaps

- No unit tests exist yet. Most components are Harmony patches over engine behavior; meaningful
  automated coverage requires either engine assemblies in a test harness or extraction of pure logic.
- Transpiler pattern checks currently happen only at runtime (swap-count logs). An offline IL-pattern
  check is feasible (the patterns are simple: named getter calls, one float constant) but not built.
- The diagnostics' *comparison* logic (keyed multiset diffs) lives in analysis tooling outside the
  mod, not in tests.

## Planned automated tests

- **Pure-logic extraction and unit tests:** the P2P framing/parser and transfer state machine (0.9
  items 2-3) are being designed test-first as plain classes with no Unity dependency; the sequenced-
  lock ordinal/baseline logic and the compatibility-latch state machine (items 1 and 4) will use the
  same approach.
- **Offline IL assertions:** extend `check-harmony-targets.py` to verify the transpiler patterns
  (e.g. `get_IsInFogOfWar` call counts per target method, the `0.6f` constant in `UnpauseController`)
  against the reference `Code.dll`, so game updates surface as failing checks instead of runtime
  `PATTERN NOT FOUND` logs.
- **Per-component registry with health table** (0.9 item 1) turns the boot self-verification into a
  single structured report suitable for CI-style scraping from logs.

Checks that still require Unity or a live multiplayer session are marked in the relevant document.
