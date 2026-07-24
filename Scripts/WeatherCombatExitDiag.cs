// Weather combat-exit DIAGNOSTIC -- log-only instrumentation for the second weather determinism bug
// (capture "0.8.17 SECOND"): at combat end (tick 7595434, FakeEmperorAppear), one
// client drew the hashed Weather stream once more than the other; Player.Weather/Wind.NextWeatherChange
// are in the player hash, so the player bucket forked first, then randomState, persisting.
//
// Suspected mechanism (verified in decompile): WeatherController.HandlePartyCombatStateChanged(false)
// (:320) chooses post-combat weather/wind inclemency from VFXWeatherSystem.Instance.IsProfileOverriden --
// a VISUAL-system flag (Channel B on the simulation path) -- plus the veil counter and CurrentWeatherEffect,
// then calls SetNewInclemency on the WEATHER controller iff its TargetInclemency differs and on the WIND
// controller iff its TargetInclemency differs (:347-:354). Each call draws hashed PFStatefulRandom.Weather
// and writes hashed player fields. The capture cannot distinguish WHICH predicate differed, so this logs
// EVERY gating input: both controllers' TargetInclemency, the profile-override flag, the veil counter,
// CurrentWeatherEffect -- plus pre->post Weather fingerprints per SetNewInclemency call, attributed to
// weather vs wind via the controller's m_WeatherData reference. The next two-sided capture with a combat
// in a veil-affected area names the differing predicate directly.
//
// Deliberately NOT a fix. Do NOT wrap this path in DisableStatefulRandomContext: the draws write into
// HASHED player fields, so diverting them to client-random fallback values would write different values on
// each machine and make the fork worse (explicit review warning).
//
// Compile-safety: InclemencyType, IsProfileOverriden, and TargetInclemency's return type all live in
// Owlcat.Runtime.Visual (NOT a template reference assembly) -- every such member is accessed reflectively;
// CurrentWeatherEffect is a public FIELD (not property; review catch -- AccessTools.Field, not .Property).
//
// Log-only -> subset-safe. MP-gated to keep solo logs quiet (mid-session instrumentation, not
// teardown-path -- the 0.8.17 gate lesson does not apply).
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
            // Explicit signature (the 0.8.13 lesson).
            var handle = AccessTools.Method(typeof(WeatherController), "HandlePartyCombatStateChanged",
                new[] { typeof(bool) });
            if (handle != null)
                yield return handle;
            else
                MultiplayerStabilityMain.Log("[WeatherDiag][ERR] WeatherController.HandlePartyCombatStateChanged(bool) not found -- diagnostic inactive.");
        }

        private static void Prefix(WeatherController __instance, bool inCombat)
        {
            Snapshot("pre ", __instance, inCombat);
        }

        private static void Postfix(WeatherController __instance, bool inCombat)
        {
            Snapshot("post", __instance, inCombat);
        }

        private static readonly FieldInfo s_weatherCtrl =
            AccessTools.Field(typeof(WeatherController), "m_WeatherInclemencyController");
        private static readonly FieldInfo s_windCtrl =
            AccessTools.Field(typeof(WeatherController), "m_WindInclemencyController");
        private static readonly PropertyInfo s_targetInclemency =
            AccessTools.Property(typeof(InclemencyController), "TargetInclemency");

        private static void Snapshot(string phase, WeatherController controller, bool inCombat)
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
                sb.Append(" profileOverriden=").Append(ProfileOverriden());
                // The two ACTUAL gating predicates (:347/:351): each controller updates iff its
                // TargetInclemency differs from the chosen value.
                sb.Append(" weatherTarget=").Append(Target(controller, s_weatherCtrl));
                sb.Append(" windTarget=").Append(Target(controller, s_windCtrl));
                try
                {
                    // Public FIELD of InclemencyType? (unreferenced Visual enum) -- reflective read.
                    object weather = Game.Instance.Player.Weather;
                    object effect = weather != null
                        ? AccessTools.Field(weather.GetType(), "CurrentWeatherEffect")?.GetValue(weather)
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

        private static string Target(WeatherController controller, FieldInfo ctrlField)
        {
            try
            {
                var inclemencyController = ctrlField != null ? ctrlField.GetValue(controller) : null;
                if (inclemencyController == null)
                    return "noctrl";
                var v = s_targetInclemency != null ? s_targetInclemency.GetValue(inclemencyController) : null;
                return v != null ? v.ToString() : "prop?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static PropertyInfo s_profileOverridenProp;
        private static PropertyInfo s_vfxInstanceProp;

        private static string ProfileOverriden()
        {
            try
            {
                if (s_profileOverridenProp == null)
                {
                    var t = AccessTools.TypeByName("Owlcat.Runtime.Visual.Effects.WeatherSystem.VFXWeatherSystem");
                    if (t == null)
                        return "type?";
                    s_profileOverridenProp = AccessTools.Property(t, "IsProfileOverriden");
                    s_vfxInstanceProp = AccessTools.Property(t, "Instance");
                }
                var inst = s_vfxInstanceProp != null ? s_vfxInstanceProp.GetValue(null) : null;
                if (inst == null)
                    return "noinst";
                return s_profileOverridenProp != null
                    ? (s_profileOverridenProp.GetValue(inst)?.ToString() ?? "null") : "prop?";
            }
            catch (Exception)
            {
                return "?";
            }
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

    // Draw-site half: every terminal SetNewInclemency logs WHICH controller (weather vs wind, identified by
    // the private m_WeatherData reference against Player.Weather/Player.Wind), the arguments, and the
    // pre->post Weather fingerprint (captured in __state so the draw itself is bracketed) -- so calls from
    // Tick or veil handlers are distinguishable from the combat-exit pair, and an unequal call COUNT vs an
    // unequal ARGUMENT is immediately visible in a two-sided diff. The inclemency parameter's type is the
    // unreferenced Visual enum, so arguments arrive as object[].
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
                // The terminal overload: (InclemencyType, bool, float?) -- the other overloads chain into it.
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

        private static readonly FieldInfo s_weatherData =
            AccessTools.Field(typeof(InclemencyController), "m_WeatherData");

        private static void Prefix(out string __state)
        {
            __state = WeatherCombatExitDiag.Fingerprint(PFStatefulRandom.Weather);
        }

        private static void Postfix(InclemencyController __instance, object[] __args, string __state)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return;
                string role = "?";
                try
                {
                    var data = s_weatherData != null ? s_weatherData.GetValue(__instance) : null;
                    if (ReferenceEquals(data, Game.Instance.Player.Weather)) role = "weather";
                    else if (ReferenceEquals(data, Game.Instance.Player.Wind)) role = "wind";
                }
                catch (Exception) { }
                MultiplayerStabilityMain.Log("[WeatherDiag] SetNewInclemency ctrl=" + role
                    + " inclemency=" + (__args != null && __args.Length > 0 && __args[0] != null ? __args[0].ToString() : "?")
                    + " instantly=" + (__args != null && __args.Length > 1 ? __args[1] : "?")
                    + " tick=" + Game.Instance.RealTimeController.CurrentNetworkTick
                    + " Weather=" + __state + "->" + WeatherCombatExitDiag.Fingerprint(PFStatefulRandom.Weather));
            }
            catch (Exception)
            {
                // log-only diagnostic: never interfere
            }
        }
    }
}
