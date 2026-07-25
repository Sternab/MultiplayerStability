// Out-of-tick leak detector (v0.7.0). Reports a leaking call site on one machine before a paired
// desync capture is available. It also works in solo sessions.
//
// Several confirmed desyncs involved view code drawing a hashed RNG stream or minting a hashed
// entity/fact id outside deterministic execution. This class has one testable invariant:
//
//     A serializable PFStatefulRandom draw should occur inside a deterministic simulation tick.
//
// Implementation:
//   - Rand.Get() (Rand.cs:50) is the common entry point for hashed RNG draws and uuid allocation
//     (Uuid.CreateGuid -> m_Random.Range -> Rand.Get).
//   - PFStatefulRandom.Serializable (PFStatefulRandom.cs:311) identifies the hashed streams; each
//     StatefulRandom exposes .Rand (the Rand instance Get() runs on) and .Name.
//   - RealTimeController.IsSimulationTick (RealTimeController.cs:334) identifies simulation execution.
//   - Rand.Get()'s built-in DisableStatefulRandomContext branch (Rand.cs:52) already diverts whitelisted
//     view-time draws to the non-hashed fallback.
//
// A Harmony prefix reports a main-thread draw when the Rand belongs to a hashed stream, execution is
// outside a simulation tick, and DisableStatefulRandomContext is not active. The stack identifies the
// call site for review.
//
// This component is log-only and never changes a draw. Automatically diverting an out-of-tick draw to
// the non-deterministic fallback would create a desync when the report is a false positive, such as
// legitimate simulation work immediately outside the IsSimulationTick bracket. Reports are used to tune
// the allow-list and identify call sites; they are not suppressed automatically.
//
// Limits: (a) Rand.Get() is small and a JIT-inlining candidate. Patching during initialization should
// precede gameplay JIT; the verification case is the known
// weather leak in a lightning area. If it stays silent there, hook Rand.RangedRandom/GetFloat too.
// (b) Off-main-thread draws are skipped because IsSimulationTick cannot be read safely there.
// (c) Mechanics paths that read a view flag without calling Rand.Get are outside this detector's scope.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Kingmaker;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.Utility.Random;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    internal static class LeakDetector
    {
        // Reference-identity map: each hashed stream's Rand instance -> its name (Rand does not override
        // Equals/GetHashCode, so the default Dictionary compares by reference -- exactly what we want).
        private static Dictionary<Rand, string> s_hashedStreams;
        private static int s_mainThreadId;
        private static readonly Dictionary<string, int> s_logCount = new Dictionary<string, int>();
        private const int PerStreamCap = 6;   // enough to name a site a few times, then go quiet

        internal static void Wire(Harmony harmony)
        {
            try
            {
                s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
                s_hashedStreams = new Dictionary<Rand, string>();
                foreach (var stream in PFStatefulRandom.Serializable)
                {
                    if (stream != null && stream.Rand != null && !s_hashedStreams.ContainsKey(stream.Rand))
                        s_hashedStreams[stream.Rand] = stream.Name;
                }
                var target = AccessTools.Method(typeof(Rand), nameof(Rand.Get));
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(LeakDetector), nameof(OnRandGet)));
                harmony.Patch(target, prefix: prefix);
                MultiplayerStabilityMain.Log("[LeakDetector] Armed -- watching " + s_hashedStreams.Count
                    + " hashed RNG streams for out-of-tick draws (log-only, proactive). If a hashed draw fires"
                    + " outside a sim tick it will be named here BEFORE any desync.");
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[LeakDetector][ERR] failed to arm, disabled: " + e);
                s_hashedStreams = null;
            }
        }

        // Prefix on Rand.Get(). HOT PATH: the first check (IsSimulationTick) is the near-universal early-out,
        // so in-tick draws -- the overwhelming majority -- cost one bool read and return.
        private static void OnRandGet(Rand __instance)
        {
            try
            {
                if (s_hashedStreams == null)
                    return;
                var game = Game.Instance;
                var rtc = game != null ? game.RealTimeController : null;
                if (rtc == null || rtc.IsSimulationTick)
                    return;                                   // in a sim tick (or pre-game) = deterministic, fine
                if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                    return;                                   // off-thread (Pathfinding) -- do BEFORE any Unity singleton
                if (ContextData<DisableStatefulRandomContext>.Current)
                    return;                                   // engine already diverts this to the non-hashed fallback
                // Load/prepare/chargen initialization draws outside the tick but is DETERMINISTIC (both
                // machines run the identical load), so it never forks -- the false-positive surface the audit
                // predicted. Suppress the whole loading-SCREEN window (fade-in..shown..fade-out), not just the
                // narrow area-swap work: v0.7.1 used only IsLoadingInProcess and the LoadingScreen/chargen/
                // view draws leaked through the gap where the screen is up but the swap flag is momentarily off.
                var lp = LoadingProcess.Instance;
                if (lp != null && (lp.IsLoadingInProcess || lp.IsLoadingScreenActive))
                    return;
                string name;
                if (!s_hashedStreams.TryGetValue(__instance, out name))
                    return;                                   // a non-hashed stream drawn out-of-tick = harmless
                // Still out-of-tick outside any load window: classify by call path. Deterministic init/view
                // lifecycle (entity prepare/postload, chargen doll rebuild, view attach, animation-set) is
                // benign and stays silent; anything ELSE is a real ACTIVE-PLAY out-of-tick leak worth naming.
                bool benign;
                string site = ClassifyStack(out benign);
                if (benign)
                    return;
                ReportLeak(name, site);
            }
            catch (Exception)
            {
                // never throw into the engine's RNG path
            }
        }

        private static void ReportLeak(string stream, string site)
        {
            int n;
            s_logCount.TryGetValue(stream, out n);
            if (n >= PerStreamCap)
                return;
            s_logCount[stream] = n + 1;
            string tail = (n + 1 == PerStreamCap) ? " (further out-of-tick draws on this stream suppressed)" : "";
            MultiplayerStabilityMain.Log("[LeakDetector] OUT-OF-TICK hashed draw: stream '" + stream
                + "' drawn during active play (not loading) -> latent desync. Site: " + site + tail);
        }

        // Init/view lifecycle methods that legitimately draw a hashed stream outside a tick and outside a
        // load screen (unit views attaching as they stream into camera range, the main-character chargen doll
        // rebuilding). These are deterministic on every client, so they are benign -- classify and stay silent.
        private static readonly string[] BenignMarkers =
        {
            "PrepareOrPrePostLoad", "PrePostLoad", "PostLoad", "LoadRoutine",
            "ChargenUnit", "RecreateUnit", "PrepareChargenUnits",
            "SetupCharacterAvatar", "AttachToData", "OnAnimationSetChanged", "SetupCharacterView",
            "LoadingScreen", "SetupLoadingArea",
        };

        // Walk the managed frames once: build a readable "who called" site string AND decide benign. Skip by
        // NAME (not a fixed frame count) so Harmony glue frames between the prefix and Rand.Get don't shift it.
        private static string ClassifyStack(out bool benign)
        {
            benign = false;
            try
            {
                var st = new StackTrace(0, false);
                var sb = new System.Text.StringBuilder();
                int shown = 0;
                for (int i = 0; i < st.FrameCount && i < 20; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null)
                        continue;
                    var t = m.DeclaringType;
                    var tn = t != null ? t.Name : "?";
                    for (int k = 0; k < BenignMarkers.Length; k++)
                    {
                        if (tn.IndexOf(BenignMarkers[k], StringComparison.Ordinal) >= 0
                            || m.Name.IndexOf(BenignMarkers[k], StringComparison.Ordinal) >= 0)
                        {
                            benign = true;
                            break;
                        }
                    }
                    // skip our own plumbing and the RNG wrappers for the DISPLAY string (still scanned above)
                    if (tn == "LeakDetector" || tn == "Rand" || tn == "StatefulRandom" || tn == "Uuid" || tn == "PFUuid")
                        continue;
                    if (shown < 6)
                    {
                        if (shown > 0)
                            sb.Append(" <- ");
                        sb.Append(tn).Append('.').Append(m.Name);
                        shown++;
                    }
                }
                return sb.Length > 0 ? sb.ToString() : "(unresolved)";
            }
            catch (Exception)
            {
                benign = false;
                return "(stack unavailable)";
            }
        }
    }
}
