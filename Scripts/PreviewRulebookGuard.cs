// Preview rulebook guard -- the rulebook-HANDLER half of the preview-ghost fix (PreviewGhostFix is the
// uuid-MINT half). A client-local UI PREVIEW unit (a "ghost" copy minted when a character/level-up screen is
// opened; AbstractUnitEntity.IsPreviewUnit) stays subscribed to the GLOBAL rulebook, and its fact-component
// handlers fire during REAL combat on the ONE machine that has the ghost -- a handler that mutates a shared
// rule (e.g. a global damage modifier injecting into RuleRollDamage) then forks the RuleSystem stream ->
// desync. Field-proven: a burst attack forked RuleSystem with PascalCompanion[PREVIEW] as the only anomaly.
//
// DESIGN (narrow registration-time guard). Credit: external review of the first cut (a broad reflection sweep
// that patched every unit-owned global handler method + resolved owners during dispatch) correctly flagged
// it as more blast radius than the evidence needs, and pointed out that the SUBSCRIBE site can resolve the
// owner via the proxy and skip preview-owned GLOBAL registrations centrally. Verified before adopting:
//   - RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy) (RulebookEventBus.cs:72) is
//     the single point where a global rulebook handler enters GlobalRulebookSubscribers.
//   - Owner IS resolvable there: fact-component handlers register with their ComponentRuntime as the proxy
//     (EntityFactComponentDelegate.ComponentRuntime : ISubscriptionProxy), and ISubscriptionProxy.
//     GetSubscribingEntity() returns the owner; the rare no-proxy path falls back to IEntitySubscriber.
//   - TIMING holds (the thing that makes a registration guard actually fire): PartPreviewUnit is added during
//     CreateEntity under the PreviewUnit context (BaseUnitEntity.cs:916-918) BEFORE the preview copy calls
//     Subscribe() (UnitHelper.CopyInternal:135), so IsPreviewUnit is already true at registration.
//
// So ONE cold patch (fires only when something subscribes) replaces ~30 hot per-handler patches: in MP, a
// preview ghost's fact never enters GlobalRulebookSubscribers, so it can never fire during real combat.
// Target/initiator/hooks registration is left untouched (a preview is never a real participant, so those are
// already inert). Fail-open (any null/throw -> subscribe normally), MP-only (solo byte-identical vanilla),
// and it logs each skip. A skip log POSITIVELY proves the patch is live; its ABSENCE is only meaningful when
// the repro definitely produced a preview unit carrying a global rulebook handler -- otherwise "no skip" just
// means no such ghost was present, not that the patch failed. INLINING caveat: the target is a small private
// static, a JIT-inline candidate; patching at the enter point (before gameplay JIT) should prevent inlining
// into Subscribe(object). If a controlled repro that definitely spawns such a ghost still logs no skip, the
// prefix was bypassed -- move the patch to the public Subscribe(object) entry.
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
                    MultiplayerStabilityMain.Log("[GhostRulebookGuard][ERR] RulebookEventBus.Subscribe(global) not found -- disabled.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(PreviewRulebookGuard), nameof(Prefix))));
                MultiplayerStabilityMain.Log("[GhostRulebookGuard] Armed -- preview-owned global rulebook subscriptions are blocked in multiplayer (registration-time).");
            }
            catch (Exception e)
            {
                MultiplayerStabilityMain.Log("[GhostRulebookGuard][ERR] failed to arm, disabled: " + e);
            }
        }

        // Prefix on the private RulebookEventBus.Subscribe(IGlobalRulebookSubscriber, ISubscriptionProxy).
        // false == skip the GlobalRulebookSubscribers.Subscribe (do not register this preview ghost's global
        // handler); true == register as vanilla.
        private static bool Prefix(IGlobalRulebookSubscriber subscriber, ISubscriptionProxy proxy)
        {
            try
            {
                if (!NetworkingManager.IsMultiplayer)
                    return true;                                   // solo: byte-identical vanilla
                var entity = proxy != null
                    ? proxy.GetSubscribingEntity()
                    : (subscriber as IEntitySubscriber)?.GetSubscribingEntity();
                var owner = entity as AbstractUnitEntity;
                if (owner != null && owner.IsPreviewUnit)
                {
                    if (++s_skipped <= SkipLogCap)
                        MultiplayerStabilityMain.Log("[GhostRulebookGuard] skipped preview-owned global rulebook subscription #"
                            + s_skipped + (s_skipped == SkipLogCap ? " (further skips silent)" : ""));
                    return false;                                  // ghost stays out of the global rulebook
                }
            }
            catch
            {
                // any failure to resolve the owner -> register normally (never block a real unit)
            }
            return true;
        }
    }
}
