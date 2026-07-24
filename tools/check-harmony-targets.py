#!/usr/bin/env python3
"""check-harmony-targets.py -- offline Harmony-target resolution smoke test.

Verifies, without launching anything, that every documented patch target still exists in a given
Code.dll (or other assembly) by reading .NET metadata with `dnfile` (pip install dnfile).

Usage:
    python tools/check-harmony-targets.py <path-to-Code.dll> [<more assemblies...>]

Standard full run (template reference assemblies + the one game-install-only assembly):
    python tools/check-harmony-targets.py Code.dll RogueTrader.GameCore.dll StatefulRandom.dll         "<game>/WH40KRT_Data/Managed/Owlcat.Runtime.Visual.dll"

Scope and honesty:
- Checks TYPE presence and METHOD-NAME presence (with expected parameter count where stated).
- Does NOT verify IL patterns (transpiler drift shows up at runtime as PATTERN NOT FOUND logs) and
  cannot detect JIT inlining. Targets living in assemblies you did not pass are reported SKIPPED.
- The list below is the manual source of truth, kept in lockstep with PATCH-CATALOG.md.
"""
import sys
from collections import defaultdict

# (component, type full name, method name, param-count or None, note)
TARGETS = [
    ("C01", "Kingmaker.Networking.SaveNetManager", "UploadSave", None, ""),
    ("C01", "Kingmaker.Networking.SaveNetManager", "DownloadSave", None, ""),
    ("C02", "Kingmaker.Networking.DataTransporter", "SendSave", None, ""),
    ("C02", "Kingmaker.Networking.MessageNetManager", "OnMessage", None, ""),
    ("C03", "Kingmaker.Networking.SyncNetManager", "HandleActorsState", None, ""),
    ("C03", "Kingmaker.Networking.Desync.UIDesyncHandler", "RaiseDesync", None, ""),
    ("C03", "Kingmaker.Controllers.Net.SyncStateCheckerController", "Kingmaker.Controllers.Interfaces.IControllerTick.Tick", None, "explicit interface implementation"),
    ("C03", "Kingmaker.Controllers.Net.SyncStateCheckerController", "CheckHash", None, ""),
    ("C03", "Kingmaker.Networking.HashCalculator", "GetStateHashByNewMethod", None, ""),
    ("C04", "Owlcat.Runtime.Visual.Effects.WeatherSystem.VFXWeatherSystem", "Update", 0, "game-install Owlcat.Runtime.Visual.dll; resolved at runtime via TypeByName"),
    ("C05", "Kingmaker.Controllers.Projectiles.Projectile", "BeforeLaunch", None, "transpiler: Random<FxBone> swap"),
    ("C06", "Kingmaker.Networking.LockNetManager", "Lock", None, ""),
    ("C06", "Kingmaker.Networking.LockNetManager", "OnLockReceived", None, ""),
    ("C07", "Kingmaker.Controllers.SleepingUnitsController", "Tick", 0, "replacing prefix"),
    ("C07", "Kingmaker.View.EntityFader", "set_Visible", 1, ""),
    ("C08", "Kingmaker.EntitySystem.Entities.AreaEffectEntity", "ShouldUnitBeInside", 1, "transpiler"),
    ("C08", "Kingmaker.Controllers.Combat.UnitCombatJoinController", "ShouldStartCombat", None, "transpiler"),
    ("C08", "Kingmaker.LOSGetter", "GetBaseValue", 0, "transpiler"),
    ("C08", "Kingmaker.View.UnitMovementAgentBase", "TickMovement", 1, "transpiler"),
    ("C08", "Kingmaker.Controllers.MapObjects.PartyAwarenessController", "Tick", 0, "transpiler"),
    ("C08", "Kingmaker.RicochetHelper", "GetPossibleRicochetTargets", None, "transpiler"),
    ("C09", "Kingmaker.UnitLogic.Abilities.Components.AbilityCustomDirectMovement", "HandleNecessaryTargets", None, ""),
    ("C10", "Kingmaker.EntitySystem.EntityFact", "Attach", None, ""),
    ("C10", "Kingmaker.UnitLogic.UnitHelper", "Copy", 5, ""),
    ("C11", "Kingmaker.Utility.StatefulRandom.Rand", "Get", 0, "StatefulRandom.dll; inlining-sensitive; verified live by its own firing"),
    ("C12", "Kingmaker.PubSubSystem.Core.RulebookEventBus", "Subscribe", 2, "RogueTrader.GameCore.dll"),
    ("C13", "Kingmaker.Controllers.TurnBased.TurnController", "SetTime", 0, "transpiler"),
    ("C13", "Kingmaker.Controllers.UnpauseController", "Tick", 0, "transpiler: 0.6f constant"),
    ("C14", "Kingmaker.Controllers.Optimization.EntityBoundsHelper", "FindUnitsInRange", 2, ""),
    ("C15", "Kingmaker.Controllers.Projectiles.Projectile", "GetTargetPoint", 0, ""),
    ("C15", "Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack.AbilityProjectileAttackLineHelper", "TryGetTargetPointByRandomLocator", None, ""),
    ("C16", "Kingmaker.DialogSystem.Blueprints.BlueprintAnswer", "get_SkillChecks", 0, ""),
    ("C16", "Kingmaker.DialogSystem.Blueprints.BlueprintAnswer", "get_SkillChecksDC", 0, ""),
    ("C16", "Kingmaker.Controllers.Dialog.DialogController", "HasNextUnselectedAnswers", 1, ""),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager", "TickIdleVariants", 1, "transpiler"),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager", "OnAnimationSetChanged", 0, "transpiler"),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionMicroIdle", "OnStart", 1, "transpiler"),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionVariantIdle", "OnStart", 1, "transpiler"),
    ("C18", "Kingmaker.Code.UI.MVVM.VM.ActionBar.ActionBarSlotVM", "HandleRoleSet", 1, ""),
    ("C18", "Kingmaker.Code.UI.MVVM.VM.ActionBar.ActionBarSlotVM", "HandlePlayerEnteredRoom", 1, ""),
    ("C18", "Kingmaker.Code.UI.MVVM.VM.ActionBar.ActionBarSlotVM", "HandlePlayerLeftRoom", 1, ""),
    ("C19", "Kingmaker.Controllers.WeatherController", "HandlePartyCombatStateChanged", 1, ""),
    ("C19", "Kingmaker.Controllers.InclemencyController", "SetNewInclemency", 3, "terminal overload"),
    ("C20", "Kingmaker.Mechanics.Entities.AbstractUnitEntity", "ForceRotateToDesired", 0, ""),
    ("C20", "Kingmaker.Game", "HandleGameModeChanged", 2, ""),
    ("C21", "Kingmaker.Code.UI.MVVM.VM.ServiceWindows.Augmentations.AugmentationsVM", ".ctor", None, ""),
    ("C21", "Kingmaker.Controllers.BarkBanterController", "HandleBarkBanter", 1, ""),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "FindPathChargeTB_Blocking", 5, ""),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "FindFullCachedPath", 5, ""),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "FindPartialCachedPath", 4, "the disabled lookup"),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "ComputeAndCachePath", 5, ""),
    ("C23", "Kingmaker.UnitLogic.FactLogic.TacticalAdvantagePassive", "OnEventDidTrigger", 1, ""),
]


def load_assembly(path):
    import dnfile
    pe = dnfile.dnPE(path)
    types = {}  # full name -> list of (method name, param count)
    for t in pe.net.mdtables.TypeDef.rows:
        ns = str(t.TypeNamespace or "")
        name = str(t.TypeName)
        full = (ns + "." + name) if ns else name
        bucket = types.setdefault(full, [])
        deref = lambda x: getattr(x, "row", x)   # run-list members are MDTableIndex references
        for mref in (t.MethodList or []):
            m = deref(mref)
            if m is None:
                continue
            # Param sequence 0 is the return slot; count real parameters only.
            pcount = 0
            for pref in (m.ParamList or []):
                p = deref(pref)
                if p is not None and p.Sequence > 0:
                    pcount += 1
            bucket.append((str(m.Name), pcount))
    return types


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    universe = {}
    for path in argv[1:]:
        try:
            print(f"loading {path} ...")
            universe.update(load_assembly(path))
        except Exception as e:
            print(f"  ERROR loading {path}: {e}")
            return 2
    ok = missing = skipped = 0
    by_component = defaultdict(list)
    for comp, tname, mname, pcount, note in TARGETS:
        if tname not in universe:
            # type absent from the assemblies provided -> can't judge
            by_component[comp].append(("SKIP", f"{tname} not in provided assemblies ({note})"))
            skipped += 1
            continue
        cands = [m for m in universe[tname] if m[0] == mname]
        if pcount is not None:
            cands = [m for m in cands if m[1] == pcount]
        if cands:
            by_component[comp].append(("ok", f"{tname}.{mname}" + (f"({pcount}p)" if pcount is not None else "")))
            ok += 1
        else:
            by_component[comp].append(("MISSING", f"{tname}.{mname}" + (f"({pcount}p)" if pcount is not None else "") + (f" [{note}]" if note else "")))
            missing += 1
    fail_components = []
    for comp in sorted(by_component):
        entries = by_component[comp]
        worst = "MISSING" if any(s == "MISSING" for s, _ in entries) else ("SKIP" if all(s == "SKIP" for s, _ in entries) else "ok")
        if worst == "MISSING":
            fail_components.append(comp)
        print(f"[{comp}] {worst}")
        for s, msg in entries:
            print(f"    {s:7s} {msg}")
    print(f"\nresolved={ok} missing={missing} skipped={skipped}")
    if missing:
        print(f"FAIL: components with missing targets: {', '.join(fail_components)}")
        print("(each such component fails open at runtime, but the fix it carries is inert)")
        return 1
    print("ALL PROVIDED TARGETS RESOLVE")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
