# Evidence excerpts — sanitized capture extracts with source hashes

Small verbatim extracts from the strongest two-sided captures, so the core convictions can be
audited without the multi-GB raw logs. Every quoted line is checked against the hashed source file
at generation time: the generator (`tools/build-evidence-excerpts.py`, in this repository)
asserts an exact substring match and fails rather than emit an unverified quote.

**Raw captures and redaction.** The raw logs are retained by the author. They contain Windows
usernames and Steam IDs belonging both to the author and to other players, so they are available
on request only in redacted form: identifiers replaced with positional labels, no log-line content
altered. Each table below records the SHA-256 of the RAW archived file; a redacted copy is
delivered with its own SHA-256. Redaction cannot affect the quoted lines below - they were selected
to contain no identifiers - so every quote stays verbatim-checkable against either copy.

Third-party-mod log lines containing other players' local paths are excluded from these extracts.

Machine labels (HOST/CLIENT/PEER-x) are positional, not identities. All captures: game 1.6.1.514.

---

## E-DLG · Dialogue UI RNG (capture 0.8.8) — conviction

| File | SHA-256 |
|---|---|
| HOST `GameLogFull-Host.txt` (826,871 B) | `CCA1D0DC8BB1820A0D49CD04F68951C04D3698F43BBB22AF6F9FF1AC52D4E7FB` |
| CLIENT `GameLogFull-Client.txt` (779,538 B) | `4B256AB6B931F3FB02B3083728AA12A3B7595837DA689E1E4895620E08F5E336` |

The host's hash stays frozen while the client's keeps moving (repeated one-sided draws):

```
[11.07.2026 00:09:04:944 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 3060966: P1=DF488006 P2=8C71C83A -> bucket=randomState (senderTick 3060908, inferred)
[11.07.2026 00:09:05:948 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 3060986: P1=DF488006 P2=AC001518 -> bucket=randomState (senderTick 3060908, inferred)
[11.07.2026 00:09:06:941 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 3061006: P1=DF488006 P2=3B91DB70 -> bucket=randomState (senderTick 3060908, inferred)
```

The client's RNG-fingerprint ring shows `DialogSystem` advancing just before the mismatch tick:

```
  t3060961: DialogSystem:EF85BD3F
  t3060964: DialogSystem:3C5A0317
```

The host's ring for the same episode contains **no `DialogSystem` entries at all** — only:

```
[11.07.2026 00:09:04:943 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] rng streams advanced near tick 3060966:
  t3060441: Weather:B7AD9AA7
  t3060461: GlobalUuid:A605A670
  t3060833: Weather:CB255DFC
```

One-sided `DialogSystem` movement at view time is exactly the C16 defect; the fix's post-fix
validation record is separate (see `../EVIDENCE-MATRIX.md`).

---

## E-WEX · Weather combat-exit, one draw ahead (capture 0.8.17-SECOND) — open investigation (C19)

| File | SHA-256 |
|---|---|
| PEER-A `GameLogFull (31).txt` (1,651,442 B) | `FF71065C3D596E02D33D2954C5934B8F3B6767A17910E630FA9EC446ED5883CB` |
| PEER-B `GameLogFull (32).txt` (1,643,899 B) | `75A31E0CD3D7015D186F03581EEA682813375B2311B51FA73FBB958653E67ADC` |

Last common `Weather` fingerprint at t7595433; at t7595434 the streams differ; PEER-B reaches
PEER-A's t7595434 value six ticks later — one side is exactly one draw ahead:

```
PEER-A:    t7595433: Weather:3B82177B,GlobalUuid:B162DC24
PEER-A:    t7595434: Weather:BB7B90B6,GlobalUuid:E17E7678
PEER-B:    t7595433: Weather:3B82177B,GlobalUuid:B162DC24
PEER-B:    t7595434: Weather:8BDD9BBB,GlobalUuid:E17E7678
PEER-B:    t7595440: Weather:BB7B90B6
```

Context: combat ends at the `FakeEmperorAppear` cutscene on both machines; the fork is detected
immediately after:

```
[20.07.2026 19:46:52:136 - History.Area][Message]: 17:26:36 25.02.4729 - FakeEmperorAppear (fb11cc7da9224e808e70a1f3966b80d4): cutscene started
[20.07.2026 19:46:52:454 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] ===== POTENTIAL DESYNC detected @tick 7595438 room=eu_sera88og3 players=2 =====
```

---

## E-TRP · Trap/pause command-lifecycle storm (capture 0.8.xx) — conviction for the C20 containment

| File | SHA-256 |
|---|---|
| PEER-A `GameLogFull (34).txt` (26,807,379 B) | `E08629529D638413D357AEE7A782639490FFB2F9FEB4FF0178F38E7935C769EA` |
| PEER-B `GameLogFull (4) (1).txt` (42,266,885 B) | `F1417F7613827BA43C78954436EC8A8D41DECE919332A63644D8C4EFD1B52153` |

Whole-file counts of the residual-command exception (mechanically recountable with grep):

```
PEER-A:  'Cmd is already set' x 107
PEER-B:  'Cmd is already set' x 514
```

(The catalog's 72-vs-10 trap-NRE figure is an episode-window count from the analysis of this
capture, not a whole-file grep; the 514-vs-107 residual counts above reproduce exactly.)

The diagnostic names the mechanism in one line — paused window, visible unit, missing IK graph,
NRE thrown AFTER the hashed sim orientation write — and the same `(tick, unit, seq)` key appears
on both peers (the cross-peer record-matching contract):

```
PEER-A:  [23.07.2026 17:31:25:678 - MultiplayerStability][Message]: [MPStability] [TrapDiag] ForceRotateToDesired(paused) unit=8b13291056a05e7ea4cbd93a74b80181 tick=7795963 seq=0 view=True visible=True vt=True ik=ok
PEER-B:  [23.07.2026 17:31:23:660 - MultiplayerStability][Message]: [MPStability] [TrapDiag] ForceRotateToDesired(paused) unit=8b13291056a05e7ea4cbd93a74b80181 tick=7795963 seq=0 view=True visible=True vt=True ik=ok
PEER-B:  [23.07.2026 17:31:23:661 - MultiplayerStability][Message]: [MPStability] [TrapDiag][EXC] ForceRotateToDesired threw AFTER the sim orientation write: unit=e926bcfd334551108b6ede01e19de9e4 tick=7795963 seq=0 paused=True view=True visible=True vt=True ik=nogrounder -> NullReferenceException: Object reference not set to an instance of an object
         System.Exception: Cmd is already set
```

The earlier 0.8.19 capture (first instrumented occurrence, same shape) is hashed for completeness:
`C6F3D334BE649603BCB6B04321D05388456CE7DCDD36C5C671525C3D46D42173` (PEER-A), `E1AFCECD6783D4A4378A08E00CA221A7E705F31F8A1F3621609514A007A126BA` (PEER-B).

---

## E-BRK · Player-bucket fork at the augmentation screen (capture 0.8.23) — conviction for C21

| File | SHA-256 |
|---|---|
| PEER-1 `GameLogFull-1.txt` (28,699,536 B) | `D3A9EC238177403879778BB130C6C4B088BAEE3C0DD4D26A1FDEAA4B512168E6` |
| PEER-2 `GameLogFull-2.txt` (14,672,581 B) | `9B7F1C63655E743CF2A8560BC25A4CA5A38F13ED8A3B6727B5F4429566E4446E` |

A sustained `player`-bucket fork, identically attributed on both machines:

```
[22.07.2026 20:06:33:798 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] ===== POTENTIAL DESYNC detected @tick 6157685 room=ru_4uutrupj9 players=2 =====
[22.07.2026 20:06:34:606 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 6157706: P1=61610C4D P2=07DA9B98 -> bucket=player (senderTick 6157700, inferred)
[22.07.2026 20:07:49:052 - MultiplayerStability][Message]: [MPStability] [DesyncWatch] hash mismatch @tick 6157727: P1=FAE46A1D P2=35A03A9A -> bucket=player (senderTick 6157720, inferred)
```

`Player.PlayedBanters` is in the player hash; the augmentation screen's bark write was the
convicted one-sided writer (mechanism chain in `../EVIDENCE-MATRIX.md` and the C21 catalog entry;
the fix itself is Mechanism confirmed; post-fix validation pending).

---

*To re-verify any excerpt: hash the delivered raw file, compare size and hash to the table, then
grep for the quoted line verbatim. Sizes are computed from the archived files at generation time.*
