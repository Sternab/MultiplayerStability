// Weather combat-exit DIAGNOSTIC -- log-only instrumentation for the second weather determinism bug
// (capture "0.8.17 SECOND", Codex round 20): at combat end (tick 7595434, FakeEmperorAppear), one client
// drew the hashed Weather stream once more than the other; Player.Weather/Wind.NextWeatherChange are in
// the player hash, so the player bucket forked first, then randomState, persisting until the peer left.
//
// Suspected mechanism (verified in decompile): WeatherController.HandlePartyCombatStateChanged(false)
// (:320) chooses the post-combat inclemency from VFXWeatherSystem.Instance.IsProfileOverriden -- a
// VISUAL-system flag (Channel B, on the simulation path this time) -- plus the veil counter and
// CurrentWeatherEffect; each resulting InclemencyController.SetNewInclemency (:151) draws once from hashed
// PFStatefulRandom.Weather and writes hashed player weather fields. The capture cannot distinguish WHICH
// predicate differed (IsProfileOverriden vs TargetInclemency/CurrentWeatherEffect), so per the
// instrument-before-fix discipline this logs every input and the pre/post stream fingerprints; the next
// two-sided capture with a combat near weather names the differing predicate directly.
//
// Deliberately NOT a fix. In particular, do NOT wrap this path in DisableStatefulRandomContext: the draws
// write into HASHED player fields, so diverting them to client-random fallback values would write
// different values on each machine and make the fork worse (Codex's explicit warning).
//
// Log-only -> subset-safe. MP-gated to keep solo logs quiet (combat-state events fire constantly solo;
// this is mid-session instrumentation, not teardown-path -- the 0.8.17 gate lesson does not apply).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers;
using Kingmaker.Networking;
using Kingmaker.Utility.Random;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    [HarmonyPatch]
    internal static class WeatherCombatExitDiag
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // Explicit signatures (the 0.8.13 lesson).
            var handle = AccessTools.Method(typeof(WeatherController), "HandlePartyCombatStateChanged",
                new[] { typeof(bool) });
            if (handle != null)
                yield return handle;
            else
                MultiplayerStabilityMain.Log("[WeatherDiag][ERR] WeatherController.HandlePartyCombatStateChanged(bool) not found -- diagnostic inactive.");
        }

        private static void Prefix(bool inCombat)
        {
            Snapshot("pre ", inCombat);
        }

        private static void Postfix(bool inCombat)
        {
            Snapshot("post", inCombat);
        }

        private static void Snapshot(string phase, bool inCombat)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                var sb = new StringBuilder(256);
                sb.Append("[WeatherDiag] ").Append(phase)
                  .Append(" combatStateChanged inCombat=").Append(inCombat)
                  .Append(" tick=").Append(Game.Instance.RealTimeController.CurrentNetworkTick);
                try
                {
                    sb.Append(" veil=").Append(Game.Instance.TurnController.VeilThicknessCounter.Value);
                }
                catch (Exception) { sb.Append(" veil=?"); }
                try
                {
                    // VFXWeatherSystem lives in Owlcat.Runtime.Visual -- resolve reflectively, cached.
                    sb.Append(" profileOverriden=").Append(ProfileOverriden());
                }
                catch (Exception) { sb.Append(" profileOverriden=?"); }
                try
                {
                    // CurrentWeatherEffect's type is InclemencyType? (Owlcat.Runtime.Visual -- NOT a
                    // template reference assembly), so it must be read reflectively or the mod fails to
                    // compile (the PFStatefulRandom-namespace lesson, assembly edition).
                    object weather = Game.Instance.Player.Weather;
                    object effect = weather != null
                        ? AccessTools.Property(weather.GetType(), "CurrentWeatherEffect")?.GetValue(weather)
                        : null;
                    sb.Append(" currentEffect=").Append(effect != null ? effect.ToString() : "null");
                }
                catch (Exception) { sb.Append(" currentEffect=?"); }
                sb.Append(" Weather=").Append(Fingerprint(PFStatefulRandom.Weather));
                MultiplayerStabilityMain.Log(sb.ToString());
            }
            catch (Exception)
            {
                // log-only diagnostic: never interfere
            }
        }

        private static PropertyInfo s_profileOverriden;
        private static object s_vfxInstance;

        private static string ProfileOverriden()
        {
            if (s_profileOverriden == null)
            {
                var t = AccessTools.TypeByName("Owlcat.Runtime.Visual.Effects.WeatherSystem.VFXWeatherSystem");
                if (t == null)
                    return "type?";
                s_profileOverriden = AccessTools.Property(t, "IsProfileOverriden");
                var instProp = AccessTools.Property(t, "Instance");
                s_vfxInstance = instProp != null ? instProp.GetValue(null) : null;
            }
            if (s_profileOverriden == null)
                return "prop?";
            // Instance can change across scene loads -- re-read it each call via the cached property.
            var tLive = s_profileOverriden.DeclaringType;
            var inst = AccessTools.Property(tLive, "Instance")?.GetValue(null);
            if (inst == null)
                return "noinst";
            return s_profileOverriden.GetValue(inst)?.ToString() ?? "null";
        }

        internal static string Fingerprint(StatefulRandom rand)
        {
            try
            {
                var st = rand.State;
                return (st.x ^ st.y ^ st.z ^ st.w).ToString("X8");
            }
            catch (Exception)
            {
                return "????????";
            }
        }
    }

    // Second half: log every terminal SetNewInclemency call (the draw site). The inclemency parameter's
    // type is InclemencyType (Owlcat.Runtime.Visual -- unreferenced), so the target is resolved by
    // name + parameter-shape match and the arguments are received as object[] (no compile-time enum).
    [HarmonyPatch]
    internal static class InclemencyController_SetNewInclemency_Diag_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase found = null;
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(InclemencyController)))
            {
                if (m.Name != "SetNewInclemency")
                    continue;
                var p = m.GetParameters();
                // The terminal overload: (InclemencyType, bool, float?) -- both other overloads chain into it.
                if (p.Length == 3 && p[1].ParameterType == typeof(bool)
                    && p[2].ParameterType == typeof(float?))
                {
                    found = m;
                    break;
                }
            }
            if (found != null)
                yield return found;
            else
                MultiplayerStabilityMain.Log("[WeatherDiag][ERR] InclemencyController.SetNewInclemency(Inclemency,bool,float?) not found -- draw-site diagnostic inactive.");
        }

        private static void Postfix(object[] __args)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                MultiplayerStabilityMain.Log("[WeatherDiag] SetNewInclemency inclemency="
                    + (__args != null && __args.Length > 0 && __args[0] != null ? __args[0].ToString() : "?")
                    + " instantly=" + (__args != null && __args.Length > 1 ? __args[1] : "?")
                    + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick
                    + " Weather=" + WeatherCombatExitDiag.Fingerprint(PFStatefulRandom.Weather));
            }
            catch (Exception)
            {
                // log-only diagnostic: never interfere
            }
        }
    }
}
