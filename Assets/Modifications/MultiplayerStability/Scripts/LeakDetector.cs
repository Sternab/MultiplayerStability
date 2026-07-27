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
//   - During simulation ticks, the GlobalUuid Rand alone is forwarded to DesyncWatch's bounded ring with
//     its managed call site. This closes the attribution gap for ItemEntity, AbilityData, and other UUID
//     callers that do not pass through EntityFact.Attach.
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
using Kingmaker.Networking;
using Kingmaker.Utility.Random;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    internal static class LeakDetector
    {
        // Reference-identity map: each hashed stream's Rand instance -> its name (Rand does not override
        // Equals/GetHashCode, so the default Dictionary compares by reference -- exactly what we want).
        private static Dictionary<Rand, string> s_hashedStreams;
        private static Rand s_globalUuidRand;
        private static int s_mainThreadId;
        private static readonly Dictionary<string, int> s_logCount = new Dictionary<string, int>();
        private const int PerSiteCap = 6;
        private const int MaxDistinctSites = 256;
        private static bool s_siteTableCapLogged;

        internal static void Wire(Harmony harmony)
        {
            try
            {
                s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
                s_hashedStreams = new Dictionary<Rand, string>();
                s_globalUuidRand = null;
                foreach (var stream in PFStatefulRandom.Serializable)
                {
                    if (stream != null && stream.Rand != null && !s_hashedStreams.ContainsKey(stream.Rand))
                        s_hashedStreams[stream.Rand] = stream.Name;
                    if (stream != null && stream.Rand != null
                        && string.Equals(stream.Name, "GlobalUuid", StringComparison.Ordinal))
                    {
                        s_globalUuidRand = stream.Rand;
                    }
                }
                var target = AccessTools.Method(typeof(Rand), nameof(Rand.Get));
                if (target == null)
                    throw new MissingMethodException(typeof(Rand).FullName, nameof(Rand.Get));
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(LeakDetector), nameof(OnRandGet)));
                harmony.Patch(target, prefix: prefix);
                MultiplayerStabilityMain.LogNoThrow("[LeakDetector] Armed; watching " + s_hashedStreams.Count
                    + " hashed RNG streams for candidate out-of-tick draws (log-only).");
                if (s_globalUuidRand == null)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[LeakDetector][WARN] GlobalUuid stream not found; in-tick UUID caller "
                        + "attribution is unavailable.");
                }
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow("[LeakDetector][ERR] failed to arm, disabled: " + e);
                s_hashedStreams = null;
            }
        }

        // Prefix on Rand.Get(). HOT PATH: simulation draws pay one reference comparison against the
        // GlobalUuid Rand; stack walking occurs only for an actual serialized UUID draw.
        private static void OnRandGet(Rand __instance)
        {
            try
            {
                if (s_hashedStreams == null)
                    return;
                if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
                    return;                                   // do not touch Game/Unity state from worker threads
                var game = Game.Instance;
                var rtc = game != null ? game.RealTimeController : null;
                if (rtc == null)
                    return;
                if (rtc.IsSimulationTick)
                {
                    // GlobalUuid is the one in-tick stream whose caller identity is needed after a
                    // count fork. Do not record DisableStatefulRandomContext calls: Rand.Get diverts
                    // those to the non-hashed fallback and the serialized stream does not advance.
                    if (ReferenceEquals(__instance, s_globalUuidRand)
                        && NetworkingManager.IsMultiplayer
                        && !ContextData<DisableStatefulRandomContext>.Current)
                    {
                        DesyncWatch.RecordGlobalUuidDraw(DescribeStack());
                    }
                    return;
                }
                if (ContextData<DisableStatefulRandomContext>.Current)
                    return;                                   // engine already diverts this to the non-hashed fallback
                // Loading and character-setup callbacks produce a high volume of initialization draws while
                // synchronized gameplay is not advancing. This detector excludes the whole loading-screen
                // window because its purpose is to identify actionable runtime callers, not to prove loading
                // symmetry. The exclusion is a diagnostic blind spot, not evidence that those draws are safe.
                // Cover both the area-swap flag and the wider visible loading-screen interval.
                var lp = LoadingProcess.Instance;
                if (lp != null && (lp.IsLoadingInProcess || lp.IsLoadingScreenActive))
                    return;
                string name;
                if (!s_hashedStreams.TryGetValue(__instance, out name))
                    return;                                   // a non-hashed stream drawn out-of-tick = harmless
                // A call path alone cannot prove that every peer executes the same lifecycle callback. Report
                // view attach, animation setup, and similar paths as candidates instead of allow-listing them.
                string site = DescribeStack();
                ReportLeak(name, site);
            }
            catch (Exception)
            {
                // never throw into the engine's RNG path
            }
        }

        private static void ReportLeak(string stream, string site)
        {
            string key = stream + "\n" + site;
            int n;
            if (!s_logCount.TryGetValue(key, out n))
            {
                if (s_logCount.Count >= MaxDistinctSites)
                {
                    if (!s_siteTableCapLogged)
                    {
                        MultiplayerStabilityMain.LogNoThrow(
                            "[LeakDetector] distinct call-site cap reached; new sites are omitted this session.");
                        s_siteTableCapLogged = true;
                    }
                    return;
                }
                n = 0;
            }
            if (n >= PerSiteCap)
                return;
            s_logCount[key] = n + 1;
            string tail = (n + 1 == PerSiteCap)
                ? " (further records for this stream/site suppressed this session)"
                : "";
            MultiplayerStabilityMain.LogNoThrow("[LeakDetector] CANDIDATE out-of-tick hashed draw: stream='"
                + stream + "' site=" + site + tail);
        }

        internal static void ResetSession()
        {
            s_logCount.Clear();
            s_siteTableCapLogged = false;
        }

        // Walk managed frames once and build a stable call-site key. Skip by name rather than frame count so
        // Harmony glue frames between the prefix and Rand.Get do not shift the reported caller.
        private static string DescribeStack()
        {
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
                    // Skip our own plumbing and RNG wrappers in the display key.
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
                return "(stack unavailable)";
            }
        }
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnJoinedLobby))]
    internal static class ModsNetManager_OnJoinedLobby_LeakDetectorReset_Patch
    {
        private static void Prefix() => LeakDetector.ResetSession();
    }

    [HarmonyPatch(typeof(ModsNetManager), nameof(ModsNetManager.OnLeave))]
    internal static class ModsNetManager_OnLeave_LeakDetectorReset_Patch
    {
        private static void Postfix() => LeakDetector.ResetSession();
    }
}
