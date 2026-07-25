// Weather VFX RNG fix. Weather effects drain the hashed PFStatefulRandom.Weather stream once per
// rendered frame (WeatherMinMaxRateSpawnController.Update rolls per frame with Time.deltaTime timing;
// WeatherLightningBoltController.Spawn rolls twice per bolt) -- two co-op clients at different frame
// rates can diverge the randomState hash in a weather area.
//
// Fix: wrap the single render-loop driver of all weather effect controllers (VFXWeatherSystem.Update)
// in DisableStatefulRandomContext. Under it, Rand.Get returns UnityEngine.Random values without
// advancing the hashed stream (the same pattern UnitDescriptionHelper uses for UI rolls).
// The deterministic sim consumers of the same stream (InclemencyController via WeatherController.Tick)
// run in the simulation tick, never inside this Unity Update, so they are untouched.
//
// The v0.9 compatibility decision enables this stream change only under exact-build parity. Post-fix
// paired captures kept the Weather stream synchronized.
using System;
using HarmonyLib;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.Utility.StatefulRandom;

namespace MultiplayerStability
{
    internal static class WeatherRngFix
    {
        internal static void Wire(Harmony harmony)
        {
            try
            {
                // Owlcat.Runtime.Visual.dll is not among the template's reference assemblies -- resolve at runtime.
                var type = AccessTools.TypeByName("Owlcat.Runtime.Visual.Effects.WeatherSystem.VFXWeatherSystem");
                var update = (type != null) ? AccessTools.Method(type, "Update") : null;
                if (update == null)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[WeatherFix][ERR] VFXWeatherSystem.Update not found; weather RNG fix inactive.");
                    return;
                }
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(WeatherRngFix), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(WeatherRngFix), nameof(Finalizer)));
                MultiplayerStabilityMain.LogNoThrow(
                    "[WeatherFix] VFXWeatherSystem.Update wrapper installed; runtime use requires exact parity.");
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow("[WeatherFix][ERR] wire failed: " + e);
            }
        }

        private static void Prefix(out IDisposable __state)
        {
            __state = null;
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return;
            try
            {
                __state = ContextData<DisableStatefulRandomContext>.Request();
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[WeatherFix][ERR] RNG context request failed; vanilla draw path remains: " + e.Message);
            }
        }

        // Finalizer, not postfix: an unbalanced Request can leave the process-global flag set, route
        // later draws away from hashed streams, and cause a persistent desync.
        private static Exception Finalizer(IDisposable __state, Exception __exception)
        {
            try
            {
                __state?.Dispose();
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[WeatherFix][ERR] RNG context dispose failed; RNG context may remain active: " + e);
            }
            return __exception;
        }
    }
}
