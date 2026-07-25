// Preview rulebook guard. This is the rulebook-handler half of the preview-unit fix;
// PreviewGhostFix handles uuid allocation. A client-local preview unit created for a character or
// level-up screen can remain subscribed to the global rulebook. Its fact-component handlers can then
// run during combat only on the peer that created the preview. Capture evidence recorded a burst
// attack that forked RuleSystem with PascalCompanion[PREVIEW] as the only associated anomaly.
//
// Design: narrow registration-time guard. The first implementation used a broad reflection sweep that
// patched every unit-owned global handler method and resolved owners during dispatch. The subscribe site
// provides the required owner information with a smaller behavioral surface:
// owner via the proxy and skip preview-owned GLOBAL registrations centrally. Verified before adopting:
//   - RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy) (RulebookEventBus.cs:72) is
//     the single point where a global rulebook handler enters GlobalRulebookSubscribers.
//   - Owner IS resolvable there: fact-component handlers register with their ComponentRuntime as the proxy
//     (EntityFactComponentDelegate.ComponentRuntime : ISubscriptionProxy), and ISubscriptionProxy.
//     GetSubscribingEntity() returns the owner; the rare no-proxy path falls back to IEntitySubscriber.
//   - PartPreviewUnit is added during
//     CreateEntity under the PreviewUnit context (BaseUnitEntity.cs:916-918) BEFORE the preview copy calls
//     Subscribe() (UnitHelper.CopyInternal:135), so IsPreviewUnit is already true at registration.
//
// One registration patch replaces approximately 30 per-handler patches. In multiplayer, a
// preview ghost's fact never enters GlobalRulebookSubscribers, so it can never fire during real combat.
// Target/initiator/hooks registration is left untouched (a preview is never a real participant, so those are
// already inert). Fail-open (any null/throw -> subscribe normally), MP-only (solo byte-identical vanilla),
// and it logs each skip. A skip log confirms runtime activation. An absent skip is meaningful only when
// the reproduction created a preview unit carrying a global rulebook handler.
// Inlining caveat: the target is a small private static method and a JIT-inline candidate. Patching at
// startup should prevent inlining into Subscribe(object). If a controlled reproduction creates the
// expected handler but logs no skip, move the patch to the public Subscribe(object) entry.
using System;
using HarmonyLib;
using Kingmaker.Mechanics.Entities;   // AbstractUnitEntity
using Kingmaker.Networking;           // NetworkingManager
using Kingmaker.PubSubSystem.Core;    // RulebookEventBus, IGlobalRulebookSubscriber, ISubscriptionProxy, IEntitySubscriber

namespace MultiplayerStability
{
    internal static class PreviewRulebookGuard
    {
        private static int s_skipped;
        private const int SkipLogCap = 10;

        internal static void Wire(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(RulebookEventBus), "Subscribe",
                    new[] { typeof(IGlobalRulebookSubscriber), typeof(ISubscriptionProxy) });
                if (target == null)
                {
                    MultiplayerStabilityMain.LogNoThrow(
                        "[GhostRulebookGuard][ERR] RulebookEventBus.Subscribe(global) not found; disabled.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(PreviewRulebookGuard), nameof(Prefix))));
                MultiplayerStabilityMain.LogNoThrow(
                    "[GhostRulebookGuard] Armed; runtime blocking requires exact-build compatibility.");
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[GhostRulebookGuard][ERR] failed to arm, disabled: " + e);
            }
        }

        // Prefix on the private RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy).
        // false == skip the GlobalRulebookSubscribers.Subscribe (do not register this preview ghost's global
        // handler); true == register as vanilla.
        private static bool Prefix(IGlobalRulebookSubscriber subscriber, ISubscriptionProxy proxy)
        {
            bool previewOwned;
            try
            {
                if (!MultiplayerCompatibility.SimulationFixesEnabled)
                    return true;                                   // solo/unresolved/mixed: vanilla
                var entity = proxy != null
                    ? proxy.GetSubscribingEntity()
                    : (subscriber as IEntitySubscriber)?.GetSubscribingEntity();
                var owner = entity as AbstractUnitEntity;
                previewOwned = owner != null && owner.IsPreviewUnit;
            }
            catch
            {
                // any failure to resolve the owner -> register normally (never block a real unit)
                return true;
            }
            if (!previewOwned)
                return true;

            int skipped = ++s_skipped;
            if (skipped <= SkipLogCap)
            {
                MultiplayerStabilityMain.LogNoThrow(
                    "[GhostRulebookGuard] skipped preview-owned global subscription #" + skipped
                    + (skipped == SkipLogCap ? " (further skips silent)" : ""));
            }
            return false;                                          // logging cannot re-enable registration
        }
    }
}
