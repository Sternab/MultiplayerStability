// =====================================================================================================
// MultiplayerStability -- co-op stability fixes for Warhammer 40,000: Rogue Trader.
//
// The engine runs deterministic lockstep: only commands cross the wire, and every machine must compute
// bit-identical state (hashed per-tick buckets: player / sceneEntities / areaPersistent / randomState /
// syncData). The recurring defect family this mod exists for is client-local state -- fog-of-war,
// camera, render visibility, view bones, UI refresh timing, preview units -- feeding hashed simulation.
//
// The repository-root DESIGN_NOTES.md indexes the components, validation status, compatibility
// rules, and known limitations.
//
// PEER COMPATIBILITY:
//   Subset-safe: diagnostics, UI-only guards, and the transfer acknowledgement pump.
//   Epoch-gated: custom protocols and every simulation-changing fix. Before each save-transfer
//                relaunch, peers exchange build identity and the save sender distributes one
//                exact-build decision directly to every other actor. Incompatible 0.9-series peers
//                select vanilla behavior. Pre-0.9 builds do not honor that decision and remain
//                unsupported in mixed-version sessions.
//
// Design rules (DESIGN_NOTES.md has the full statement):
//   - No automatic resync: recovery stays under player control; the mod never forces a reload.
//   - Patch isolation: a failed class logs [Init][ERR] while unrelated classes continue. A component
//     spanning several classes can be left partially active, so any startup error invalidates the build
//     for multiplayer. Runtime failure behavior is documented at each patch site.
//   - Solo-safe: simulation-changing behavior is MP-gated; solo play takes the vanilla paths.
//   - Evidence requirement: prevention fixes require a two-sided capture or a complete engine-source
//     path that identifies the mechanism.
//
// General loading-speed work lives in the separate FasterLoadTimes mod.
// =====================================================================================================
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Modding;                          // OwlcatModification, OwlcatModificationEnterPoint

namespace MultiplayerStability
{
    public static class MultiplayerStabilityMain
    {
        // Must match Manifest.UniqueName in MultiplayerStability.asset. MultiplayerCompatibility uses
        // this ID in the Photon mod-list property ("m") when evaluating exact-build parity.
        internal const string UniqueName = "MultiplayerStability";

        public static OwlcatModification Modification { get; private set; }

        [OwlcatModificationEnterPoint]
        public static void Initialize(OwlcatModification modification)
        {
            Modification = modification;
            LogNoThrow("[Init] Enter point reached; bootstrapping Harmony.");
            var harmony = new Harmony(modification.Manifest.UniqueName);
            // Per-class patching with isolation -- NOT a blanket PatchAll. PatchAll processes patch classes
            // sequentially and a single throw (e.g. a TargetMethods that resolves ZERO methods after a game
            // update -- Harmony throws on target-less classes) aborts EVERYTHING after it, including the
            // manual Wire() calls below: the transfer stack would silently die and saves fall back to
            // vanilla speed with no crash. Each class now fails alone, loudly.
            int patched = 0, failed = 0;
            Type[] patchTypes;
            try
            {
                patchTypes = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                patchTypes = e.Types.Where(t => t != null).ToArray();
                foreach (var loaderException in e.LoaderExceptions ?? Array.Empty<Exception>())
                    LogNoThrow("[Init][ERR] type load: " + loaderException.Message);
            }

            foreach (var type in patchTypes)
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
                    // several patch classes can be left partially active (see DESIGN_NOTES.md).
                    LogNoThrow("[Init][ERR] patch class " + type.Name + " failed (class inert, others unaffected): " + e.Message);
                }
            }
            SafeWire("SteamSaveTransfer", () => SteamSaveTransfer.Wire());
            SafeWire("DesyncWatch", () => DesyncWatch.Wire());
            SafeWire("WeatherRngFix", () => WeatherRngFix.Wire(harmony));
            SafeWire("LeakDetector", () => LeakDetector.Wire(harmony));        // proactive out-of-tick hashed-draw detector (patch early, before gameplay JIT)
            SafeWire("PreviewRulebookGuard", () => PreviewRulebookGuard.Wire(harmony)); // block preview-ghost global rulebook subscriptions (registration-time guard; patch early, before gameplay JIT)
            LogNoThrow("[Init] Patches applied (" + patched + " classes"
                + (failed > 0 ? ", " + failed + " FAILED" : "") + ").");
        }

        private static void SafeWire(string name, System.Action wire)
        {
            try
            {
                wire();
            }
            catch (System.Exception e)
            {
                LogNoThrow(
                    "[Init][ERR] " + name
                    + ".Wire failed (component inert, others unaffected): " + e.Message);
            }
        }

        public static void Log(string msg)
        {
            Modification?.Logger.Log("[MPStability] " + msg);
        }

        public static void LogNoThrow(string msg)
        {
            try
            {
                Log(msg);
            }
            catch
            {
                // Logging must never alter a patched gameplay or recovery path.
            }
        }
    }
}
