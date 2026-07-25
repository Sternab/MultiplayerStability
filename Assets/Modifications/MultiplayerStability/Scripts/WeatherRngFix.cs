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
// Peer compatibility: exact parity required. A mixed install does not repair the stream behavior and is
// unsupported. Post-fix paired captures kept the Weather stream synchronized.
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
                    MultiplayerStabilityMain.Log("[WeatherFix][ERR] VFXWeatherSystem.Update not found; weather RNG fix inactive.");
                    return;
                }
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(WeatherRngFix), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(WeatherRngFix), nameof(Finalizer)));
                MultiplayerStabilityMain.Log("[WeatherFix] VFXWeatherSystem.Update wrapped; hashed Weather stream protected from render-frame drains.");
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[WeatherFix][ERR] wire failed: " + e);
            }
        }

        private static void Prefix(out IDisposable __state)
        {
            __state = ContextData<DisableStatefulRandomContext>.Request();
        }

        // Finalizer, not postfix: an unbalanced Request would leave the process-global flag set, making ALL
        // stateful RNG non-deterministic from then on -- a guaranteed permanent desync.
        private static Exception Finalizer(IDisposable __state, Exception __exception)
        {
            __state?.Dispose();
            return __exception;
        }
    }
}
