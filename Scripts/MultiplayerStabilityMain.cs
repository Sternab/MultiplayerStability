// =====================================================================================================
// MultiplayerStability -- co-op stability fixes for Warhammer 40,000: Rogue Trader.
//
// The engine runs deterministic lockstep: only commands cross the wire, and every machine must compute
// bit-identical state (hashed per-tick buckets: player / sceneEntities / areaPersistent / randomState /
// syncData). The recurring defect family this mod exists for is client-local state -- fog-of-war,
// camera, render visibility, view bones, UI refresh timing, preview units -- feeding hashed simulation.
//
// 23 components across 25 files: prevention fixes, transfer/lobby infrastructure, and
// log-only diagnostics. docs/PATCH-CATALOG.md is the canonical per-component inventory (targets,
// rationale, evidence status, and peer-compatibility category); docs/EVIDENCE-MATRIX.md maps claims to captures.
//
// PEER-COMPATIBILITY (three categories):
//   Subset-safe:           diagnostics + UI-only fixes + the transfer ack pump (any subset of machines).
//   Negotiated protocol:   Steam P2P transfer, Sequenced Locks, Photon window boost (self-gate on
//                          every-peer-modded and engage only then).
//   Exact parity required: EVERY RNG- or simulation-changing prevention fix. Until the 0.9
//                          session-latched compatibility gate ships, run the IDENTICAL build on every
//                          machine (docs/COMPATIBILITY.md).
//
// Design rules (docs/KNOWN-LIMITATIONS.md has the full statement):
//   - No automatic resync: recovery stays under player control; the mod never forces a reload.
//   - Best-effort fail-open: patching is isolated per class -- a failed class logs [Init][ERR] and stays
//     inert while the rest continue, so a component spanning several classes or targets can be left
//     partially active (docs/KNOWN-LIMITATIONS.md); runtime guards fall back to the vanilla path at their site.
//   - Solo-safe: simulation-changing behavior is MP-gated; solo play takes the vanilla paths.
//   - Evidence requirement: prevention fixes require a two-sided capture or a complete engine-source
//     path that identifies the mechanism.
//
// Planned work: docs/ROADMAP-0.9.md. Loading-speed work lives in the separate FasterLoadTimes mod
// (not co-op-specific; subset-safe).
// =====================================================================================================
using System.Reflection;
using HarmonyLib;
using Kingmaker.Modding;                          // OwlcatModification, OwlcatModificationEnterPoint

namespace MultiplayerStability
{
    public static class MultiplayerStabilityMain
    {
        // Must match Manifest.UniqueName in MultiplayerStability.asset: it is the identity string other
        // clients see in the Photon mod-list property ("m"), which TransferBooster compares against.
        internal const string UniqueName = "MultiplayerStability";

        public static OwlcatModification Modification { get; private set; }

        [OwlcatModificationEnterPoint]
        public static void Initialize(OwlcatModification modification)
        {
            Modification = modification;
            Log("[Init] Enter point reached; bootstrapping Harmony.");
            var harmony = new Harmony(modification.Manifest.UniqueName);
            // Per-class patching with isolation -- NOT a blanket PatchAll. PatchAll processes patch classes
            // sequentially and a single throw (e.g. a TargetMethods that resolves ZERO methods after a game
            // update -- Harmony throws on target-less classes) aborts EVERYTHING after it, including the
            // manual Wire() calls below: the transfer stack would silently die and saves fall back to
            // vanilla speed with no crash. Each class now fails alone, loudly.
            int patched = 0, failed = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                try
                {
                    var processor = harmony.CreateClassProcessor(type);
                    if (processor.Patch() != null)
                        patched++;
                }
                catch (System.Exception e)
                {
                    failed++;
                    // "component inert" here means THIS PATCH CLASS is inert; a component built from
                    // several patch classes can be left partially active (see docs/KNOWN-LIMITATIONS.md).
                    Log("[Init][ERR] patch class " + type.Name + " failed (component inert, others unaffected): " + e.Message);
                }
            }
            SafeWire("SteamSaveTransfer", () => SteamSaveTransfer.Wire());
            SafeWire("DesyncWatch", () => DesyncWatch.Wire());
            SafeWire("WeatherRngFix", () => WeatherRngFix.Wire(harmony));
            SafeWire("LeakDetector", () => LeakDetector.Wire(harmony));        // proactive out-of-tick hashed-draw detector (patch early, before gameplay JIT)
            SafeWire("PreviewRulebookGuard", () => PreviewRulebookGuard.Wire(harmony)); // block preview-ghost global rulebook subscriptions (registration-time guard; patch early, before gameplay JIT)
            Log("[Init] Patches applied (" + patched + " classes" + (failed > 0 ? ", " + failed + " FAILED" : "") + ").");
        }

        private static void SafeWire(string name, System.Action wire)
        {
            try
            {
                wire();
            }
            catch (System.Exception e)
            {
                Log("[Init][ERR] " + name + ".Wire failed (component inert, others unaffected): " + e.Message);
            }
        }

        public static void Log(string msg)
        {
            Modification?.Logger.Log("[MPStability] " + msg);
        }
    }
}
