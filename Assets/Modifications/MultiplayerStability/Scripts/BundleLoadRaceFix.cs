// Async AssetBundle request coalescing for the area-load race captured in v0.9.5.
//
// In the three-player FeudalWorld capture, the host alone attempted to load one unit bundle twice
// concurrently. Unity rejected the second request because the same files were already loaded, the
// host then failed to instantiate FW_Blacksmith, and only the two clients ran the blacksmith's
// on-spawn cutscene. The first divergent hashes and RNG fingerprints follow that missing entity.
//
// Rogue Trader's BundlesLoadService.RequestBundleAsync replaces m_Bundles[bundleName] before its
// AssetBundleCreateRequest completes. A second request therefore sees no loaded Bundle, replaces
// the first BundleData, and starts another Unity load. Dark Heresy's newer service prevents this
// exact race with a per-bundle dictionary of in-flight Task<AssetBundle> instances.
//
// This patch supplies the same missing synchronization around Rogue Trader's public async entry:
// the first caller still runs the complete vanilla method, while concurrent callers receive one
// proxy task carrying that method's result, cancellation, or exception. Once BundleData.Bundle is
// populated, later callers return to vanilla so its RequestCount still advances even if dependency
// loading is finishing. Only callers that overlap the unfinished Unity load are joined, matching
// Dark Heresy's branch order. The synchronous RequestBundle path is untouched.
//
// Preventing a local bundle failure can determine whether a simulation entity exists, so the patch
// is active only under the exact-build compatibility latch. Solo, mixed, and unresolved sessions
// retain vanilla behavior. Tracking setup fails open before the original method starts.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.BundlesLoading;
using UnityEngine;

namespace MultiplayerStability
{
    [HarmonyPatch(
        typeof(BundlesLoadService),
        nameof(BundlesLoadService.RequestBundleAsync),
        typeof(string))]
    internal static class BundlesLoadService_RequestBundleAsync_Coalesce_Patch
    {
        private const int JoinLogLimit = 32;

        private static readonly ConditionalWeakTable<BundlesLoadService, ServiceState> s_states =
            new ConditionalWeakTable<BundlesLoadService, ServiceState>();
        private static readonly FieldInfo s_bundlesField =
            AccessTools.Field(typeof(BundlesLoadService), "m_Bundles");
        private static readonly Type s_bundleDataType =
            typeof(BundlesLoadService).GetNestedType("BundleData", BindingFlags.NonPublic);
        private static readonly FieldInfo s_bundleField = s_bundleDataType != null
            ? AccessTools.Field(s_bundleDataType, "Bundle")
            : null;

        private static bool s_loggedActive;
        private static int s_joinLogCount;
        private static int s_setupErrorLogged;

        private static bool Prepare()
        {
            if (s_bundlesField != null && s_bundleField != null)
                return true;

            MultiplayerStabilityMain.LogNoThrow(
                "[BundleLoadFix][ERR] required BundlesLoadService fields were not found; coalescing inactive.");
            return false;
        }

        private sealed class ServiceState
        {
            internal readonly object Gate = new object();
            internal readonly Dictionary<string, PendingLoad> Pending =
                new Dictionary<string, PendingLoad>(StringComparer.Ordinal);
        }

        private sealed class PendingLoad
        {
            internal readonly TaskCompletionSource<AssetBundle> Completion =
                new TaskCompletionSource<AssetBundle>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            internal int JoinCount;

            internal Task<AssetBundle> Task => Completion.Task;
        }

        private sealed class OwnerCall
        {
            internal readonly ServiceState Service;
            internal readonly string BundleName;
            internal readonly PendingLoad Pending;

            internal OwnerCall(ServiceState service, string bundleName, PendingLoad pending)
            {
                Service = service;
                BundleName = bundleName;
                Pending = pending;
            }
        }

        private static bool Prefix(
            BundlesLoadService __instance,
            string bundleName,
            ref Task<AssetBundle> __result,
            out OwnerCall __state)
        {
            __state = null;
            if (!MultiplayerCompatibility.SimulationFixesEnabled)
                return true;

            // Best-effort presence signal. It is deliberately outside the fail-open setup block.
            LogActiveOnce();

            try
            {
                // Dark Heresy checks loaded BundleData before its in-flight dictionary. Preserve
                // that order so a caller arriving during dependency loading takes vanilla's
                // already-loaded branch and increments RequestCount.
                if (IsBundleLoaded(__instance, bundleName))
                    return true;

                ServiceState service = s_states.GetValue(
                    __instance, _ => new ServiceState());
                PendingLoad pending;
                int joinNumber = 0;

                lock (service.Gate)
                {
                    if (service.Pending.TryGetValue(bundleName, out pending))
                    {
                        if (!pending.Task.IsCompleted)
                        {
                            joinNumber = ++pending.JoinCount;
                            __result = pending.Task;
                        }
                        else
                        {
                            // Completion is published before the observer's finally removes the
                            // entry. Let a later request use vanilla's already-loaded path.
                            service.Pending.Remove(bundleName);
                            pending = null;
                        }
                    }

                    if (joinNumber == 0)
                    {
                        pending = new PendingLoad();
                        var owner = new OwnerCall(service, bundleName, pending);
                        service.Pending.Add(bundleName, pending);
                        __state = owner;
                    }
                }

                if (joinNumber == 0)
                    return true;

                LogJoinNoThrow(bundleName, joinNumber);
                return false;
            }
            catch (Exception e)
            {
                LogSetupErrorOnce(e);
                return true;
            }
        }

        private static bool IsBundleLoaded(
            BundlesLoadService service,
            string bundleName)
        {
            if (s_bundlesField == null || s_bundleField == null)
            {
                throw new MissingFieldException(
                    typeof(BundlesLoadService).FullName,
                    s_bundlesField == null ? "m_Bundles" : "BundleData.Bundle");
            }

            var bundles = s_bundlesField.GetValue(service) as IDictionary;
            if (bundles == null)
                throw new InvalidOperationException("m_Bundles is not an IDictionary.");
            if (!bundles.Contains(bundleName))
                return false;

            object data = bundles[bundleName];
            var bundle = data != null
                ? s_bundleField.GetValue(data) as AssetBundle
                : null;
            return bundle;
        }

        private static void Postfix(ref Task<AssetBundle> __result, OwnerCall __state)
        {
            if (__state == null)
                return;

            Task<AssetBundle> vanillaTask = __result;
            try
            {
                Task observer = MirrorVanillaTask(__state, vanillaTask);
                __result = __state.Pending.Task;
                _ = observer;
            }
            catch (Exception e)
            {
                // This can only cover a failure to start the observer itself. Joined callers may
                // already hold the proxy, so complete it with the same visible failure while the
                // owning caller keeps the original task.
                __state.Pending.Completion.TrySetException(e);
                RemoveOwnerNoThrow(__state);
                MultiplayerStabilityMain.LogNoThrow(
                    "[BundleLoadFix][ERR] task observer setup failed; owner kept vanilla task: "
                    + e.Message);
                __result = vanillaTask;
            }
        }

        private static async Task MirrorVanillaTask(
            OwnerCall owner,
            Task<AssetBundle> vanillaTask)
        {
            try
            {
                if (vanillaTask == null)
                {
                    throw new InvalidOperationException(
                        "RequestBundleAsync returned a null task.");
                }

                AssetBundle bundle = await vanillaTask;
                owner.Pending.Completion.TrySetResult(bundle);
            }
            catch (OperationCanceledException)
            {
                owner.Pending.Completion.TrySetCanceled();
            }
            catch (Exception e)
            {
                owner.Pending.Completion.TrySetException(e);
            }
            finally
            {
                RemoveOwnerNoThrow(owner);
            }
        }

        private static void RemoveOwnerNoThrow(OwnerCall owner)
        {
            try
            {
                lock (owner.Service.Gate)
                {
                    if (owner.Service.Pending.TryGetValue(
                            owner.BundleName, out PendingLoad current)
                        && ReferenceEquals(current, owner.Pending))
                    {
                        owner.Service.Pending.Remove(owner.BundleName);
                    }
                }
            }
            catch (Exception e)
            {
                // A completed stale entry is also discarded by the next prefix call.
                MultiplayerStabilityMain.LogNoThrow(
                    "[BundleLoadFix][ERR] in-flight entry cleanup failed: " + e.Message);
            }
        }

        private static void LogActiveOnce()
        {
            try
            {
                if (s_loggedActive)
                    return;
                MultiplayerStabilityMain.Log(
                    "[BundleLoadFix] Active; duplicate async bundle requests will share one in-flight task.");
                s_loggedActive = true;
            }
            catch
            {
                // A failed activation log retries on the next eligible request.
            }
        }

        private static void LogJoinNoThrow(string bundleName, int joinNumber)
        {
            int record = Interlocked.Increment(ref s_joinLogCount);
            if (record <= JoinLogLimit)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[BundleLoadFix] Joined in-flight request bundle=" + bundleName
                    + " join=" + joinNumber + ".");
            }
            else if (record == JoinLogLimit + 1)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[BundleLoadFix] Further joined-request logs suppressed.");
            }
        }

        private static void LogSetupErrorOnce(Exception e)
        {
            if (Interlocked.Exchange(ref s_setupErrorLogged, 1) != 0)
                return;
            MultiplayerStabilityMain.LogNoThrow(
                "[BundleLoadFix][ERR] request tracking failed; leaving vanilla active: "
                + e.Message);
        }
    }
}
