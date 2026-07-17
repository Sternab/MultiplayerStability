// Out-of-tick leak DETECTOR -- the proactive keystone (v0.7.0). Turns the whole campaign from reactive
// (wait for both machines to desync, diff two logs) to PROACTIVE (name the leaking site on ONE machine,
// with no desync required -- it even works solo).
//
// ROOT CAUSE it targets: Rogue Trader is a single-player engine retrofitted for lockstep with no enforced
// boundary between the VIEW layer (frame-timed, client-local) and the SIM layer (the hashed state). Every
// desync we have fixed was the view layer drawing a HASHED RNG stream or minting a hashed entity/fact id
// from outside deterministic execution. That whole class shares ONE testable invariant:
//
//     a HASHED (serializable) PFStatefulRandom draw must only happen INSIDE a deterministic sim tick.
//
// The engine gives us every piece to check it (verified against the decompile):
//   - Rand.Get() (Rand.cs:50) is the SINGLE universal chokepoint: every hashed RNG draw AND every uuid mint
//     (Uuid.CreateGuid -> m_Random.Range -> Rand.Get) funnels through this one instance method.
//   - PFStatefulRandom.Serializable (PFStatefulRandom.cs:311) is the exact set of HASHED streams; each
//     StatefulRandom exposes .Rand (the Rand instance Get() runs on) and .Name.
//   - RealTimeController.IsSimulationTick (RealTimeController.cs:334, public) is the engine's own marker for
//     "we are inside a deterministic sim tick".
//   - Rand.Get()'s built-in DisableStatefulRandomContext branch (Rand.cs:52) already diverts whitelisted
//     view-time draws to the non-hashed fallback -- so a draw under that context is NOT a hashed mutation.
//
// So one Harmony prefix on Rand.Get() detects the entire leak class: if this Rand belongs to a hashed
// stream, we are NOT in a sim tick, not under the disable-context, and on the main thread -> that draw is a
// latent desync, and the captured stack names the exact leaking site (e.g. WeatherMinMaxRateSpawnController).
//
// LOG-ONLY by design. It never changes a draw -- the plan's "firewall" (auto-diverting a leaked draw to the
// fallback) is deliberately NOT built here: the fallback is non-deterministic, so firewalling a FALSE
// POSITIVE (real sim work that legitimately runs just outside the IsSimulationTick bracket -- area load,
// spawners, the few TickType.Any controllers) would MANUFACTURE the desync it was meant to prevent. The
// detector's out-of-tick set is exactly that false-positive surface; we LOG it and tune the allow-list from
// evidence before ever considering a whitelisted firewall.
//
// KNOWN LIMITS (documented, not bugs): (a) Rand.Get() is tiny and a JIT-inlining candidate -- patching at
// mod init (before gameplay JIT) should make it fire; the VERIFICATION is that it must name the known
// weather leak in a lightning area. If it stays silent there, hook Rand.RangedRandom/GetFloat too.
// (b) off-main-thread draws (Pathfinding is serializable and can draw from a worker) are skipped -- a blind
// spot, since IsSimulationTick can't be read safely off-thread. (c) Channel-B leaks (a view FLAG read by a
// mechanics decision, e.g. LOSGetter) do NOT pass through Rand.Get, so the detector cannot see them -- those
// stay a manual grep discipline. This catches the RNG/uuid-mint class (channels A, D, and part of C).
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
