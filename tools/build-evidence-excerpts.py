#!/usr/bin/env python3
"""build-evidence-excerpts.py -- regenerate EVIDENCE-EXCERPTS.md from the raw capture archive.

Every quoted line is asserted to exist verbatim in its source log before it is emitted: if a capture
is edited, replaced, or mis-transcribed, this script fails instead of publishing an unverified quote.
File sizes and SHA-256 values in the output are computed from the files themselves.

Writes docs/investigation/EVIDENCE-EXCERPTS.md.

Usage (run from the repository root):
    python tools/build-evidence-excerpts.py [<capture-root>]

<capture-root> is the directory holding the "Mod Build Logs <version>" capture folders; it defaults
to the repository root. The raw captures are NOT tracked in this repository (they contain other
players' Windows usernames and Steam IDs); they are retained by the author and released only in
redacted form. Without them this script cannot run - which is the point: the document it produces is
only as good as the archive it was generated against.
"""
import io, os, sys, hashlib

REPO_ROOT = os.getcwd()   # captured before chdir: the output path is repo-relative
os.chdir(sys.argv[1] if len(sys.argv) > 1 else REPO_ROOT)

FILES = {
 'H88':  'Mod Build Logs 0.8.8/GameLogFull-Host.txt',
 'C88':  'Mod Build Logs 0.8.8/GameLogFull-Client.txt',
 'P231': 'Mod Build Logs 0.8.23/GameLogFull-1.txt',
 'P232': 'Mod Build Logs 0.8.23/GameLogFull-2.txt',
 'PA17': 'Mod Build Logs 0.8.17 SECOND/GameLogFull (31).txt',
 'PB17': 'Mod Build Logs 0.8.17 SECOND/GameLogFull (32).txt',
 'PA19': 'Mod Build Logs 0.8.19/GameLogFull (33).txt',
 'PB19': 'Mod Build Logs 0.8.19/GameLogFull (3) (1).txt',
 'PAXX': 'Mod Build Logs 0.8.xx/GameLogFull (34).txt',
 'PBXX': 'Mod Build Logs 0.8.xx/GameLogFull (4) (1).txt',
}

sha = {}
content = {}
for k, p in FILES.items():
    data = open(p, 'rb').read()
    sha[k] = hashlib.sha256(data).hexdigest().upper()
    content[k] = data.decode('utf-8', errors='replace')

def sz(key):
    return "{:,}".format(os.path.getsize(FILES[key]))

def q(key, line):
    """Assert the quoted line exists verbatim in the source log, then return it."""
    assert line in content[key], "NOT IN %s: %r" % (key, line[:80])
    return line

def count(key, needle):
    return content[key].count(needle)

L = []
A = L.append
A("# Evidence excerpts — sanitized capture extracts with source hashes")
A("")
A("Small verbatim extracts from the strongest two-sided captures, so the core convictions can be")
A("audited without the multi-GB raw logs. Every quoted line is checked against the hashed source file")
A("at generation time: the generator (`tools/build-evidence-excerpts.py`, in this repository)")
A("asserts an exact substring match and fails rather than emit an unverified quote.")
A("")
A("**Raw captures and redaction.** The raw logs are retained by the author. They contain Windows")
A("usernames and Steam IDs belonging both to the author and to other players, so they are available")
A("on request only in redacted form: identifiers replaced with positional labels, no log-line content")
A("altered. Each table below records the SHA-256 of the RAW archived file; a redacted copy is")
A("delivered with its own SHA-256. Redaction cannot affect the quoted lines below - they were selected")
A("to contain no identifiers - so every quote stays verbatim-checkable against either copy.")
A("")
A("Third-party-mod log lines containing other players' local paths are excluded from these extracts.")
A("")
A("Machine labels (HOST/CLIENT/PEER-x) are positional, not identities. All captures: game 1.6.1.514.")
A("")
A("---")
A("")
A("## E-DLG · Dialogue UI RNG (capture 0.8.8) — conviction")
A("")
A("| File | SHA-256 |")
A("|---|---|")
A("| HOST `GameLogFull-Host.txt` (%s B) | `%s` |" % (sz('H88'), sha['H88']))
A("| CLIENT `GameLogFull-Client.txt` (%s B) | `%s` |" % (sz('C88'), sha['C88']))
A("")
A("The host's hash stays frozen while the client's keeps moving (repeated one-sided draws):")
A("")
A("```")
A(q('H88', "[11.07.2026 00:09:04:944 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 3060966: P1=DF488006 P2=8C71C83A -> bucket=randomState (senderTick 3060908, inferred)"))
A(q('H88', "[11.07.2026 00:09:05:948 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 3060986: P1=DF488006 P2=AC001518 -> bucket=randomState (senderTick 3060908, inferred)"))
A(q('H88', "[11.07.2026 00:09:06:941 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 3061006: P1=DF488006 P2=3B91DB70 -> bucket=randomState (senderTick 3060908, inferred)"))
A("```")
A("")
A("The client's RNG-fingerprint ring shows `DialogSystem` advancing just before the mismatch tick:")
A("")
A("```")
A(q('C88', "  t3060961: DialogSystem:EF85BD3F"))
A(q('C88', "  t3060964: DialogSystem:3C5A0317"))
A("```")
A("")
A("The host's ring for the same episode contains **no `DialogSystem` entries at all** — only:")
A("")
A("```")
A(q('H88', "[11.07.2026 00:09:04:943 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] rng streams advanced near tick 3060966:"))
A(q('H88', "  t3060441: Weather:B7AD9AA7"))
A(q('H88', "  t3060461: GlobalUuid:A605A670"))
A(q('H88', "  t3060833: Weather:CB255DFC"))
A("```")
A("")
A("One-sided `DialogSystem` movement at view time is exactly the C16 defect; the fix's post-fix")
A("validation record is separate (see `../EVIDENCE-MATRIX.md`).")
A("")
A("---")
A("")
A("## E-WEX · Weather combat-exit, one draw ahead (capture 0.8.17-SECOND) — open investigation (C19)")
A("")
A("| File | SHA-256 |")
A("|---|---|")
A("| PEER-A `GameLogFull (31).txt` (%s B) | `%s` |" % (sz('PA17'), sha['PA17']))
A("| PEER-B `GameLogFull (32).txt` (%s B) | `%s` |" % (sz('PB17'), sha['PB17']))
A("")
A("Last common `Weather` fingerprint at t7595433; at t7595434 the streams differ; PEER-B reaches")
A("PEER-A's t7595434 value six ticks later — one side is exactly one draw ahead:")
A("")
A("```")
A("PEER-A:  " + q('PA17', "  t7595433: Weather:3B82177B,GlobalUuid:B162DC24"))
A("PEER-A:  " + q('PA17', "  t7595434: Weather:BB7B90B6,GlobalUuid:E17E7678"))
A("PEER-B:  " + q('PB17', "  t7595433: Weather:3B82177B,GlobalUuid:B162DC24"))
A("PEER-B:  " + q('PB17', "  t7595434: Weather:8BDD9BBB,GlobalUuid:E17E7678"))
A("PEER-B:  " + q('PB17', "  t7595440: Weather:BB7B90B6"))
A("```")
A("")
A("Context: combat ends at the `FakeEmperorAppear` cutscene on both machines; the fork is detected")
A("immediately after:")
A("")
A("```")
A(q('PA17', "[20.07.2026 19:46:52:136 - History.Area][Message]: 17:26:36 25.02.4729 - FakeEmperorAppear (fb11cc7da9224e808e70a1f3966b80d4): cutscene started"))
A(q('PA17', "[20.07.2026 19:46:52:454 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] ===== POTENTIAL DESYNC detected @tick 7595438 room=eu_sera88og3 players=2 ====="))
A("```")
A("")
A("---")
A("")
A("## E-TRP · Trap/pause command-lifecycle storm (capture 0.8.xx) — conviction for the C20 containment")
A("")
A("| File | SHA-256 |")
A("|---|---|")
A("| PEER-A `GameLogFull (34).txt` (%s B) | `%s` |" % (sz('PAXX'), sha['PAXX']))
A("| PEER-B `GameLogFull (4) (1).txt` (%s B) | `%s` |" % (sz('PBXX'), sha['PBXX']))
A("")
A("Whole-file counts of the residual-command exception (mechanically recountable with grep):")
A("")
A("```")
A("PEER-A:  'Cmd is already set' x %d" % count('PAXX', 'Cmd is already set'))
A("PEER-B:  'Cmd is already set' x %d" % count('PBXX', 'Cmd is already set'))
A("```")
A("")
A("(The catalog's 72-vs-10 trap-NRE figure is an episode-window count from the analysis of this")
A("capture, not a whole-file grep; the 514-vs-107 residual counts above reproduce exactly.)")
A("")
A("The diagnostic names the mechanism in one line — paused window, visible unit, missing IK graph,")
A("NRE thrown AFTER the hashed sim orientation write — and the same `(tick, unit, seq)` key appears")
A("on both peers (the cross-peer record-matching contract):")
A("")
A("```")
A("PEER-A:  " + q('PAXX', "[23.07.2026 17:31:25:678 - MultiplayerStability][Message]: [MPStability] [TrapDiag] ForceRotateToDesired(paused) unit=8b13291056a05e7ea4cbd93a74b80181 tick=7795963 seq=0 view=True visible=True vt=True ik=ok"))
A("PEER-B:  " + q('PBXX', "[23.07.2026 17:31:23:660 - MultiplayerStability][Message]: [MPStability] [TrapDiag] ForceRotateToDesired(paused) unit=8b13291056a05e7ea4cbd93a74b80181 tick=7795963 seq=0 view=True visible=True vt=True ik=ok"))
A("PEER-B:  " + q('PBXX', "[23.07.2026 17:31:23:661 - MultiplayerStability][Message]: [MPStability] [TrapDiag][EXC] ForceRotateToDesired threw AFTER the sim orientation write: unit=e926bcfd334551108b6ede01e19de9e4 tick=7795963 seq=0 paused=True view=True visible=True vt=True ik=nogrounder -> NullReferenceException: Object reference not set to an instance of an object"))
A("         " + q('PAXX', "System.Exception: Cmd is already set"))
A("```")
A("")
A("The earlier 0.8.19 capture (first instrumented occurrence, same shape) is hashed for completeness:")
A("`%s` (PEER-A), `%s` (PEER-B)." % (sha['PA19'], sha['PB19']))
A("")
A("---")
A("")
A("## E-BRK · Player-bucket fork at the augmentation screen (capture 0.8.23) — conviction for C21")
A("")
A("| File | SHA-256 |")
A("|---|---|")
A("| PEER-1 `GameLogFull-1.txt` (%s B) | `%s` |" % (sz('P231'), sha['P231']))
A("| PEER-2 `GameLogFull-2.txt` (%s B) | `%s` |" % (sz('P232'), sha['P232']))
A("")
A("A sustained `player`-bucket fork, identically attributed on both machines:")
A("")
A("```")
A(q('P231', "[22.07.2026 20:06:33:798 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] ===== POTENTIAL DESYNC detected @tick 6157685 room=ru_4uutrupj9 players=2 ====="))
A(q('P231', "[22.07.2026 20:06:34:606 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 6157706: P1=61610C4D P2=07DA9B98 -> bucket=player (senderTick 6157700, inferred)"))
A(q('P232', "[22.07.2026 20:07:49:052 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 6157727: P1=FAE46A1D P2=35A03A9A -> bucket=player (senderTick 6157720, inferred)"))
A("```")
A("")
A("`Player.PlayedBanters` is in the player hash; the augmentation screen's bark write was the")
A("convicted one-sided writer (mechanism chain in `../EVIDENCE-MATRIX.md` and the C21 catalog entry;")
A("the fix itself is Mechanism confirmed; post-fix validation pending).")
A("")
A("---")
A("")
A("*To re-verify any excerpt: hash the delivered raw file, compare size and hash to the table, then")
A("grep for the quoted line verbatim. Sizes are computed from the archived files at generation time.*")
out = "\n".join(L) + "\n"
OUT = os.path.join(REPO_ROOT, 'docs', 'investigation', 'EVIDENCE-EXCERPTS.md')
io.open(OUT, 'w', encoding='utf-8').write(out)
print("%s written: %d bytes; all quoted lines asserted verbatim" % (OUT, len(out)))
