#!/usr/bin/env python3
"""check-harmony-targets.py -- offline Harmony-target resolution smoke test.

Verifies, without launching anything, that every documented patch target still exists in the given
assemblies by reading .NET metadata with `dnfile` (tested against dnfile==0.18.0; other versions may
change the metadata API -- a version mismatch prints a warning).

Usage:
    python tools/check-harmony-targets.py <assembly.dll> [<more assemblies...>] [options]

Standard full run (template reference assemblies + the one game-install-only assembly):
    python tools/check-harmony-targets.py Code.dll RogueTrader.GameCore.dll StatefulRandom.dll \\
        "<game>/WH40KRT_Data/Managed/Owlcat.Runtime.Visual.dll"

Options:
    --allow-skip        Downgrade unresolvable-type SKIPs to warnings. By default ANY skipped target
                        fails the run: a skip means you did not give the tool the assembly that would
                        prove the target, so nothing was verified for it.
    --reconcile <dir>   Scan the mod source in <dir> (e.g. Scripts) for patch sites
                        ([HarmonyPatch(typeof(X), ...)], AccessTools.Method(typeof(X), ...)) and fail
                        if any discovered (type, method) pair is missing from the table below.

Scope and honesty:
- Checks TYPE presence and METHOD presence; where the table row carries parameter types they are
  matched exactly against the decoded metadata signature, where it carries a count only the
  parameter count is matched, and a bare row matches by name alone.
- Does NOT verify IL patterns (transpiler drift shows up at runtime as PATTERN NOT FOUND logs) and
  cannot detect JIT inlining.
- The table below is the manual source of truth, kept in lockstep with PATCH-CATALOG.md; --reconcile
  guards it against drifting from the actual source.
"""
import re
import sys
from collections import defaultdict

TESTED_DNFILE = "0.18.0"

# (component, type full name, method name, params, note)
# params: None = match by name only; int = parameter count; [names] = exact parameter type names
# (short names, generics rendered Name<Arg>, ref parameters rendered "ref Name").
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
    ("C06", "Kingmaker.Networking.LockNetManager", "OnLeave", None, "ordinal-baseline reset"),
    ("C06", "Kingmaker.Networking.SaveNetManager", "UploadSave", None, "shared target with C01 (separate patch class)"),
    ("C06", "Kingmaker.Networking.SaveNetManager", "DownloadSave", None, "shared target with C01 (separate patch class)"),
    ("C07", "Kingmaker.Controllers.SleepingUnitsController", "Tick", 0, "replacing prefix"),
    ("C07", "Kingmaker.View.EntityFader", "set_Visible", 1, "MethodType.Setter"),
    ("C08", "Kingmaker.EntitySystem.Entities.AreaEffectEntity", "ShouldUnitBeInside", ["BaseUnitEntity"], "transpiler; also C10 prefix"),
    ("C08", "Kingmaker.Controllers.Combat.UnitCombatJoinController", "ShouldStartCombat", None, "transpiler"),
    ("C08", "Kingmaker.LOSGetter", "GetBaseValue", 0, "transpiler"),
    ("C08", "Kingmaker.View.UnitMovementAgentBase", "TickMovement", 1, "transpiler"),
    ("C08", "Kingmaker.Controllers.MapObjects.PartyAwarenessController", "Tick", 0, "transpiler"),
    ("C08", "Kingmaker.RicochetHelper", "GetPossibleRicochetTargets", None, "transpiler"),
    ("C09", "Kingmaker.UnitLogic.Abilities.Components.AbilityCustomDirectMovement", "HandleNecessaryTargets", None, ""),
    ("C09", "Kingmaker.UnitLogic.Abilities.Components.AbilityCustomDirectMovement", "HandleTarget", None, "not a patch: reflection-invoked private delivery method (delegate)"),
    ("C10", "Kingmaker.EntitySystem.EntityFact", "Attach", None, "also instrumented by C03"),
    ("C10", "Kingmaker.UnitLogic.UnitHelper", "Copy", ["BaseUnitEntity", "Boolean", "Boolean", "Boolean", "Boolean"], ""),
    ("C11", "Kingmaker.Utility.StatefulRandom.Rand", "Get", 0, "StatefulRandom.dll; inlining-sensitive; verified live by its own firing"),
    ("C12", "Kingmaker.PubSubSystem.Core.RulebookEventBus", "Subscribe", 2, "RogueTrader.GameCore.dll"),
    ("C13", "Kingmaker.Controllers.TurnBased.TurnController", "SetTime", 0, "transpiler"),
    ("C13", "Kingmaker.Controllers.UnpauseController", "Tick", 0, "transpiler: 0.6f constant"),
    ("C14", "Kingmaker.Controllers.Optimization.EntityBoundsHelper", "FindUnitsInRange", ["Vector3", "Single"], ""),
    ("C15", "Kingmaker.Controllers.Projectiles.Projectile", "GetTargetPoint", 0, ""),
    ("C15", "Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack.AbilityProjectileAttackLineHelper", "TryGetTargetPointByRandomLocator", None, ""),
    ("C16", "Kingmaker.DialogSystem.Blueprints.BlueprintAnswer", "get_SkillChecks", 0, ""),
    ("C16", "Kingmaker.DialogSystem.Blueprints.BlueprintAnswer", "get_SkillChecksDC", 0, ""),
    ("C16", "Kingmaker.Controllers.Dialog.DialogController", "HasNextUnselectedAnswers", ["BlueprintAnswer"], ""),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager", "TickIdleVariants", ["Single"], "transpiler"),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager", "OnAnimationSetChanged", 0, "transpiler"),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionMicroIdle", "OnStart", ["UnitAnimationActionHandle"], "transpiler"),
    ("C17", "Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionVariantIdle", "OnStart", ["UnitAnimationActionHandle"], "transpiler"),
    ("C18", "Kingmaker.Code.UI.MVVM.VM.ActionBar.ActionBarSlotVM", "HandleRoleSet", ["String"], ""),
    ("C18", "Kingmaker.Code.UI.MVVM.VM.ActionBar.ActionBarSlotVM", "HandlePlayerEnteredRoom", 1, ""),
    ("C18", "Kingmaker.Code.UI.MVVM.VM.ActionBar.ActionBarSlotVM", "HandlePlayerLeftRoom", 1, ""),
    ("C19", "Kingmaker.Controllers.WeatherController", "HandlePartyCombatStateChanged", 1, ""),
    ("C19", "Kingmaker.Controllers.InclemencyController", "SetNewInclemency", 3, "terminal overload; resolved by parameter shape at runtime"),
    ("C20", "Kingmaker.Mechanics.Entities.AbstractUnitEntity", "ForceRotateToDesired", 0, "two patch classes on this method (diag + containment)"),
    ("C20", "Kingmaker.Game", "HandleGameModeChanged", ["GameModeType", "GameModeType"], ""),
    ("C21", "Kingmaker.Code.UI.MVVM.VM.ServiceWindows.Augmentations.AugmentationsVM", ".ctor", None, "MethodType.Constructor"),
    ("C21", "Kingmaker.Controllers.BarkBanterController", "HandleBarkBanter", ["BlueprintBarkBanter"], ""),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "FindPathChargeTB_Blocking", ["UnitMovementAgentBase", "Vector3", "Vector3", "Boolean", "MechanicEntity"], ""),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "FindFullCachedPath", ["UnitMovementAgentBase", "Vector3", "Vector3", "Boolean", "MechanicEntity"], ""),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "FindPartialCachedPath", ["UnitMovementAgentBase", "Vector3", "Vector3", "Boolean"], "the disabled lookup"),
    ("C22", "Kingmaker.Pathfinding.PathfindingService", "ComputeAndCachePath", ["UnitMovementAgentBase", "Vector3", "Vector3", "Boolean", "MechanicEntity"], ""),
    ("C23", "Kingmaker.UnitLogic.FactLogic.TacticalAdvantagePassive", "OnEventDidTrigger", ["RulePerformMomentumChange"], ""),
    ("C23", "Kingmaker.UnitLogic.FactLogic.TacticalAdvantagePassive", "RequestSavableData", None, "not a patch: reflection read of the component data object"),
]

# Patch sites the --reconcile source scan cannot attribute mechanically, with why they are covered.
RECONCILE_EXCEPTIONS = {
    # WeatherRngFix resolves its type by name at runtime (AccessTools.TypeByName) -- covered by C04.
    ("VFXWeatherSystem", "Update"),
    # WeatherCombatExitDiag resolves SetNewInclemency by name + parameter shape -- covered by C19.
    ("InclemencyController", "SetNewInclemency"),
}

ELEMENT_PRIMITIVES = {
    0x01: "Void", 0x02: "Boolean", 0x03: "Char", 0x04: "SByte", 0x05: "Byte", 0x06: "Int16",
    0x07: "UInt16", 0x08: "Int32", 0x09: "UInt32", 0x0A: "Int64", 0x0B: "UInt64", 0x0C: "Single",
    0x0D: "Double", 0x0E: "String", 0x16: "TypedReference", 0x18: "IntPtr", 0x19: "UIntPtr",
    0x1C: "Object",
}


class SigReader:
    """Minimal ECMA-335 MethodDefSig decoder (enough for parameter type names)."""

    def __init__(self, blob, typename_by_coded):
        self.b = blob
        self.i = 0
        self.names = typename_by_coded  # coded TypeDefOrRef token -> short type name

    def u8(self):
        v = self.b[self.i]
        self.i += 1
        return v

    def cuint(self):
        b0 = self.u8()
        if b0 < 0x80:
            return b0
        if (b0 & 0xC0) == 0x80:
            return ((b0 & 0x3F) << 8) | self.u8()
        return ((b0 & 0x1F) << 24) | (self.u8() << 16) | (self.u8() << 8) | self.u8()

    def tok_name(self):
        return self.names.get(self.cuint(), "?")

    def type_name(self):
        t = self.u8()
        while t in (0x1F, 0x20):            # CMOD_REQD / CMOD_OPT
            self.cuint()
            t = self.u8()
        if t == 0x45:                        # PINNED
            t = self.u8()
        if t in ELEMENT_PRIMITIVES:
            return ELEMENT_PRIMITIVES[t]
        if t == 0x0F:                        # PTR
            return self.type_name() + "*"
        if t == 0x10:                        # BYREF
            return "ref " + self.type_name()
        if t in (0x11, 0x12):                # VALUETYPE / CLASS
            return self.tok_name()
        if t == 0x13:                        # VAR
            return "!" + str(self.cuint())
        if t == 0x1E:                        # MVAR
            return "!!" + str(self.cuint())
        if t == 0x1D:                        # SZARRAY
            return self.type_name() + "[]"
        if t == 0x14:                        # ARRAY
            inner = self.type_name()
            rank = self.cuint()
            for _ in range(self.cuint()):
                self.cuint()
            for _ in range(self.cuint()):
                self.cuint()
            return inner + "[" + "," * (rank - 1) + "]"
        if t == 0x15:                        # GENERICINST
            self.u8()                        # CLASS or VALUETYPE
            base = self.tok_name()
            base = re.sub(r"`\d+$", "", base)
            args = [self.type_name() for _ in range(self.cuint())]
            return base + "<" + ", ".join(args) + ">"
        return "?e%02x" % t

    def method_params(self):
        cc = self.u8()
        if cc & 0x10:                        # generic method: skip GenParamCount
            self.cuint()
        n = self.cuint()
        self.type_name()                     # return type
        out = []
        for _ in range(n):
            if self.i < len(self.b) and self.b[self.i] == 0x41:  # SENTINEL (vararg)
                self.i += 1
            out.append(self.type_name())
        return out


def load_assembly(path):
    import dnfile
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables

    # coded TypeDefOrRef token -> short type name (tag 0 TypeDef, 1 TypeRef, 2 TypeSpec)
    coded = {}
    coded_full = {}  # same tokens -> full names (for base-chain resolution)
    def fullname(row):
        ns = str(row.TypeNamespace or "")
        return (ns + "." + str(row.TypeName)) if ns else str(row.TypeName)
    for idx, t in enumerate(md.TypeDef.rows, 1):
        coded[(idx << 2) | 0] = str(t.TypeName)
        coded_full[(idx << 2) | 0] = fullname(t)
    if md.TypeRef:
        for idx, t in enumerate(md.TypeRef.rows, 1):
            coded[(idx << 2) | 1] = str(t.TypeName)
            coded_full[(idx << 2) | 1] = fullname(t)
    if md.TypeSpec:
        for idx in range(1, len(md.TypeSpec.rows) + 1):
            coded[(idx << 2) | 2] = "spec"

    def typespec_base_full(spec_row):
        # A generic base (Extends -> TypeSpec) encodes GENERICINST (CLASS|VALUETYPE) <token> ...;
        # the open type's TypeDef/TypeRef token is what the inheritance walk needs.
        try:
            blob = bytes(spec_row.Signature.value)
            r = SigReader(blob, coded)
            t = r.u8()
            while t in (0x1F, 0x20):
                r.cuint()
                t = r.u8()
            if t != 0x15:
                return None
            r.u8()                        # CLASS or VALUETYPE
            return coded_full.get(r.cuint())
        except Exception:
            return None

    deref = lambda x: getattr(x, "row", x)
    types = {}  # full name -> list of (method name, [param type names] or None)
    bases = {}  # full name -> base type full name (for inherited-method resolution)
    for t in md.TypeDef.rows:
        ns = str(t.TypeNamespace or "")
        name = str(t.TypeName)
        full = (ns + "." + name) if ns else name
        ext = deref(t.Extends) if t.Extends is not None else None
        if ext is not None and getattr(ext, "TypeName", None) is not None:
            bns = str(ext.TypeNamespace or "")
            bases[full] = (bns + "." + str(ext.TypeName)) if bns else str(ext.TypeName)
        elif ext is not None and getattr(ext, "Signature", None) is not None:
            b = typespec_base_full(ext)   # generic base via TypeSpec
            if b:
                bases[full] = b
        bucket = types.setdefault(full, [])
        for mref in (t.MethodList or []):
            m = deref(mref)
            if m is None:
                continue
            try:
                blob = bytes(m.Signature.value) if m.Signature is not None else b""
                params = SigReader(blob, coded).method_params()
            except Exception:
                params = None                # undecodable: falls back to name-only matching
            bucket.append((str(m.Name), params))
    return types, bases


def scan_source(scripts_dir):
    """Find (short type name, method name) patch sites in the mod source."""
    import os
    attr_re = re.compile(
        r"\[HarmonyPatch\(typeof\((\w+)\)\s*,\s*(?:nameof\(\w+\.(\w+)\)|\"([\w.]+)\")(.{0,120})", re.S)
    manual_re = re.compile(
        r"AccessTools\.Method\(typeof\((\w+)\)\s*,\s*(?:nameof\(\w+\.(\w+)\)|\"([\w.]+)\")")
    found = set()
    for fn in sorted(os.listdir(scripts_dir)):
        if not fn.endswith(".cs"):
            continue
        src = open(os.path.join(scripts_dir, fn), encoding="utf-8-sig", errors="replace").read()
        for m in attr_re.finditer(src):
            tname, meth = m.group(1), (m.group(2) or m.group(3))
            tail = m.group(4)
            if "MethodType.Setter" in tail:
                meth = "set_" + meth
            elif "MethodType.Getter" in tail:
                meth = "get_" + meth
            found.add((tname, meth))
        for m in manual_re.finditer(src):
            found.add((m.group(1), m.group(2) or m.group(3)))
        # constructor patches: [HarmonyPatch(typeof(X), MethodType.Constructor ...)]
        for m in re.finditer(r"\[HarmonyPatch\(typeof\((\w+)\)\s*,\s*MethodType\.Constructor", src):
            found.add((m.group(1), ".ctor"))
    return found


def main(argv):
    args = []
    allow_skip = False
    reconcile_dir = None
    it = iter(argv[1:])
    for a in it:
        if a == "--allow-skip":
            allow_skip = True
        elif a == "--reconcile":
            reconcile_dir = next(it, None)
        else:
            args.append(a)
    if not args:
        print(__doc__)
        return 2

    try:
        import dnfile
        if getattr(dnfile, "__version__", "?") != TESTED_DNFILE:
            print("WARNING: dnfile %s installed; this tool is tested against %s"
                  % (getattr(dnfile, "__version__", "?"), TESTED_DNFILE))
    except ImportError:
        print("ERROR: dnfile is not installed (pip install dnfile==%s)" % TESTED_DNFILE)
        return 2

    universe, bases = {}, {}
    for path in args:
        try:
            print("loading %s ..." % path)
            t, b = load_assembly(path)
            universe.update(t)
            bases.update(b)
        except Exception as e:
            print("  ERROR loading %s: %s" % (path, e))
            return 2

    def find_candidates(tname, mname, params):
        # Walk the inheritance chain like AccessTools.Method: nearest declaring type wins.
        cur, depth = tname, 0
        while cur in universe and depth < 16:
            cands = [m for m in universe[cur] if m[0] == mname]
            if isinstance(params, int):
                cands = [m for m in cands if m[1] is not None and len(m[1]) == params]
            elif isinstance(params, list):
                cands = [m for m in cands if m[1] == params]
            if cands:
                return cands, cur
            cur = bases.get(cur)
            depth += 1
        return [], None

    ok = missing = skipped = 0
    by_component = defaultdict(list)
    for comp, tname, mname, params, note in TARGETS:
        if tname not in universe:
            by_component[comp].append(("SKIP", "%s not in provided assemblies (%s)" % (tname, note)))
            skipped += 1
            continue
        cands, declaring = find_candidates(tname, mname, params)
        label = "%s.%s" % (tname, mname)
        if isinstance(params, int):
            label += "(%dp)" % params
        elif isinstance(params, list):
            label += "(" + ", ".join(params) + ")"
        if cands:
            if declaring != tname:
                label += "  [inherited from %s]" % declaring
            by_component[comp].append(("ok", label))
            ok += 1
        else:
            near = [m for m in universe[tname] if m[0] == mname]
            hint = ("; same-name signatures: " + "; ".join(
                "(" + ", ".join(p if p is not None else ["?"]) + ")" for _, p in near)) if near else ""
            by_component[comp].append(("MISSING", label + (" [%s]" % note if note else "") + hint))
            missing += 1

    fail_components = []
    for comp in sorted(by_component):
        entries = by_component[comp]
        worst = ("MISSING" if any(s == "MISSING" for s, _ in entries)
                 else ("SKIP" if any(s == "SKIP" for s, _ in entries) else "ok"))
        if worst != "ok":
            fail_components.append(comp)
        print("[%s] %s" % (comp, worst))
        for s, msg in entries:
            print("    %-7s %s" % (s, msg))

    rc = 0
    print("\nresolved=%d missing=%d skipped=%d" % (ok, missing, skipped))
    if missing:
        print("FAIL: missing targets in: %s" % ", ".join(sorted(set(fail_components))))
        print("(each such patch class fails open at runtime, but the fix it carries is inert)")
        rc = 1
    if skipped:
        msg = ("%d target(s) SKIPPED: their types are in assemblies you did not provide, "
               "so nothing was verified for them." % skipped)
        if allow_skip:
            print("WARNING (--allow-skip): " + msg)
        else:
            print("FAIL: " + msg + " Pass those assemblies or rerun with --allow-skip.")
            rc = 1

    if reconcile_dir:
        print("\n-- reconcile: scanning %s --" % reconcile_dir)
        found = scan_source(reconcile_dir)
        covered = set()
        for _, tname, mname, _, _ in TARGETS:
            covered.add((tname.rsplit(".", 1)[-1], mname))
        exceptions_hit = found & RECONCILE_EXCEPTIONS
        unlisted = sorted(p for p in found if p not in covered and p not in RECONCILE_EXCEPTIONS)
        print("source patch sites found: %d; covered by table: %d; listed exceptions: %d"
              % (len(found), len(found) - len(unlisted) - len(exceptions_hit), len(exceptions_hit)))
        if unlisted:
            print("FAIL: source patch sites missing from the TARGETS table:")
            for t, m in unlisted:
                print("    %s.%s" % (t, m))
            rc = 1
        else:
            print("all discovered patch sites are covered by the table (or listed exceptions)")

    if rc == 0:
        print("\nALL TARGETS RESOLVE (no skips%s)"
              % ("; source reconciled" if reconcile_dir else ""))
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
