# Test plan

One staged save per cluster of axes. Stage once with dev tools, save, then every
code iteration is: rebuild → relaunch → load save (assemblies don't hot-reload).

## Benchmark

`./test/run-bench.sh [label]` loads CETEST-2-selection and times the call Simple Sidearms
makes once per tick per warming-up pawn (`findBestRangedWeapon`, on a colonist carrying four
ranged CE weapons with a hostile at 10 cells), 20,000 iterations x 5 rounds, best-of. Two
arms run in one process — patches active, then `UnpatchAll` for stock SS — so the save, map,
pawn and JIT state are identical. Results land in `test/SaveData/bench-results-<label>.json`.

Only same-run `patched - stock` deltas are meaningful: the absolute baseline drifts 47-56 us
between runs on the same machine. The runner resets the patch's per-tick memo before every
timed iteration, in both arms, because the game always calls this path with a cold cache and
a tight loop inside one tick would otherwise measure a warm one.

Measured 2026-08-20 (CE 16.7.3.0, SS v1.6), selection overhead vs stock SS, and its cost at
twenty simultaneously warming-up pawns against a 60fps frame:

| build | overhead per call | % of frame @ 20 pawns |
|---|---|---|
| before the per-tick memo | +27.5 us | 3.30% |
| current | +16.2 us | 1.95% |

Combat Extended's convention is to benchmark inside RimWorld rather than in a desktop
harness (perkinslr, PR #4029) — hence the in-game runner rather than a unit benchmark.

## Automated acceptance runs

Most of this plan runs unattended via `test/run-assert.sh <scenario> <save>`
(in-game assertion runner in `test/StagingMod/Source/CETestRunner.cs`, results as
`test/SaveData/test-results-<scenario>.json`):

```
./test/run-test.sh stage                       # regenerate CETEST saves (kill after letter)
./test/run-assert.sh cetest1 CETEST-1-pickup
./test/run-assert.sh cetest2 CETEST-2-selection
./test/run-assert.sh cetest3 CETEST-3-combat
./test/run-assert.sh cetest4 CETEST-4-generation
./test/verdict.py test/SaveData/test-results-<scenario>.json   # judge + pretty-print
```

Runner discipline (2026-08-28 machinery, ported from the Loadouts module's suite):

- **Checks never latch wrongly.** Positive checks latch on first pass; *negative*
  checks (`N(...)`, must-not-happen) re-evaluate on every 30-tick poll and fail the
  phase the moment they trip; *informational* checks re-evaluate and never gate.
- **Preconditions or it didn't happen.** `P(...)` checks assert the world a phase
  needs (weapon carried, raiders present, magazine loaded). The phase's act
  (`mutate`) is deferred until they hold; if they never hold the phase reports
  **INVALID** (a broken test), not FAIL (broken code) — and `verdict.py` treats
  INVALID as red.
- **Unexpected diagnostics fail the phase.** Any Error or Warning not on the
  justified allowlist — from this mod, CE, or SS — fails the phase it appeared in.
- **Every phase carries a `state` dump** (memory, carried, preferences, primary,
  bulk, job) for forensics; a scenario-specific colonist is followed by default.
- **Phase 0 of every scenario is the patch census**: reflection over Harmony for
  methods patched by `eebette.CESimpleSidearmsCompat` (>= 19 today). A Prepare
  that quietly skipped, a Bootstrap per-class failure, or a TargetMethods that
  resolved nothing shows up before any behavioral phase runs half-patched.
- **Isolated sweep**: `./test/run-isolated.sh <scenario> <save>` runs every phase
  in its own process against a freshly loaded save (results merged by
  `verdict.py --merge`). The sequenced run proves phases work against accumulated
  state; this proves each stands alone.
- **A/B regression proof**: `./test/verify-regression.sh [--ref REV] <scenario>
  <phase-label> <files...>` stashes/reverts a fix, proves the named phase FAILS
  without it (not VOID, not unevaluated), restores it, proves the whole scenario
  passes. A check that has never been seen to fail is an assertion, not a test —
  new failing-capable checks get one of these before they are trusted.
- The Loadouts module's derivations are switched off in-memory for CETEST runs
  (`DisableLoadoutsModule`, keyed to its current `CESimpleSidearmsCompat.Loadouts`
  identity). If the module is active but the reflection misses, the runner
  fails LOUD instead of silently testing a contaminated world.

Full green pass recorded 2026-08-20 (all four scenarios, zero exceptions in logs)
on the old latching machinery; re-verified green 2026-08-28 on the current
machinery (sequenced + isolated, see the suite-can-fail proofs below).

Suite-can-fail proofs (2026-08-28, scratch A/B — a patch class disabled via its
Prepare, run, restored):

- **P10 disabled** → cetest1: census FAIL naming exactly the two missing methods,
  `forget-releases-hold` FAIL (CE dropped the unprotected pistol mid-run), and
  `re-remember-idempotent` VOID on its `pistol-carried` precondition. Three
  independent detections of one dead class.
- **P03 disabled** → cetest2: census FAIL, and both behavioral phases VOID — see
  the staging note below for why VOID and not FAIL.

Known staging weakness (queue): the CETEST colonists have no CE loadout rows (or
hold records) covering their staged kit, so any run that lingers past a phase
deadline gives CE's own loadout enforcement time to strip whatever Simple
Sidearms does not remember (observed: Picky stripped to bulk 0.0 during the P03
scratch's long red windows; the rifle is not remembered, so P10 rightly does not
protect it). Green runs never linger, so this only distorts *already-red* runs —
but it turns a clean FAIL into a VOID cascade. Fix when next staging: give each
CETEST colonist a CE loadout covering their kit.
Coverage highlights beyond the manual checklist: axis-5 direct unit hit (SS switch
entry point invoked DURING a live CE reload job — reload survived), axis-8 full
chain (a one-use weapon actually fired at a ground cell, consumption →
SS-preference re-equip; since 2026-08-28 the projectile is a CE smoke grenade —
same Verb_ShootCEOneUse class as the staged rocket, but the rocket's FRAGMENTS
reached far beyond its blast radius and downed or killed the shooter often
enough to make the phase a coin flip; the phase also parks Boomy away from the
P04-armed raiders while the earlier phases run, which was the other way he died),
axis-10 hold-record lifecycle + dedup, axis-4 per-raider capacity audit
+ orphan-ammo scan + generator idempotence. The Loadouts module's derivations are
disabled in-memory for these runs, so they exercise the compat patch alone.

Findings worth knowing (none are compat-patch defects):
- **SS upstream quirk:** `CompSidearmMemory.InformOfAddedSidearm` has no duplicate
  guard (the dedup is commented out upstream) — repeated calls grow
  RememberedWeapons. The patch's own hold records dedup correctly regardless.
  Candidate for the SS upstream report batch (issue #5).
- **SS drafted-weapon-selection skips manual-use weapons:** drafting a pawn whose
  primary is a one-use launcher holsters it in favor of a sidearm. SS-native
  (vanilla-visible too), amplified by CE's launcher availability.
- Test-harness scenario design must keep hostiles away from behavior-under-test
  pawns (return fire / melee charges corrupt phases) and target ground cells for
  AOE weapons.

## Dev-tool crib sheet

- Map: main menu → **Dev quicktest** (instant 75×75 map with debug colonists), or
  `./test/run-test.sh -quicktest`.
- Debug actions (top icon bar → wrench): **Spawning → Spawn pawn** (pick kind +
  faction), **Try place near thing...** (weapons, CE ammo — search e.g.
  "FMJ", "ammo"), **Pawns → Damage until down**, **General → Explosion...**.
- God mode (hotkey `Ctrl+Shift+G` / icon) to insta-build.
- SS gizmo on a selected pawn: sidearm list; right-click weapons on ground →
  "equip as sidearm".
- CE loadouts: Assign tab → Manage loadouts.
- Confirm patch installed: dev console shows
  `[CE+SimpleSidearms] Compatibility patches installed.`

## Save 1 — "pickup" (axes 1, 10)

Stage: 1 colonist. Fill inventory near CE bulk cap (spawn + force-carry armor
plates / ammo crates). Spawn heavy sidearm (LMG) + light sidearm (pistol) nearby.

- A1: try pick up LMG via SS right-click → expect deny ("no free space") even
  though raw *mass* would fit. Pistol → allowed.
- A10: give colonist non-default CE loadout that does NOT contain the pistol.
  Remember pistol as sidearm (SS gizmo). Wait/skip time → pawn must NOT drop it.
  Forget the sidearm in SS gizmo → CE should then drop it (exemption removed).

## Save 2 — "selection" (axes 2, 3, 9, 11)

Stage: 1 drafted colonist with: rifle (loaded), pistol sidearm (loaded), revolver
sidearm (empty, NO spare .44 ammo in inventory). Caliber matters: the dry gun
must not share ammo with anything carried — CE treats "reloadable from
inventory" as having ammo (which is correct; a shared-caliber gun IS usable).
Spawn hostile pirates at distance (Spawn pawn → faction pirate).

- A2: SS never shows DPS in its UI (it's internal selection state) — probe it via
  Debug actions → **CE+SS Compat → Log carried weapon DPS**, then click the pawn.
  Console prints one row per carried gun: `dpsAvg` sane and different per weapon
  (not 0/NaN for loaded guns), `cycle` includes reload time, the dry revolver
  shows `mag 0/6, hasAmmoOrMag=False`. (A dry gun whose caliber IS carried —
  e.g. a MAC-10 next to M1911 spares, both .45 ACP — correctly shows True:
  CE can reload it from inventory, so it counts as usable.)
- A3/A9: dev-drain rifle mag (fire until empty or unload+drop ammo mid-fight) →
  pawn must auto-switch to the LOADED pistol, never the empty SMG, and never
  fists while a loaded gun remains.
- A11: what the patch changes is *classification* (read from loaded CE ammo, not
  the verb's default projectile), so probe it directly: Debug actions →
  **CE+SS Compat → Log weapon classification**, click the pawn. Expect EMP
  grenades `EMP=True`, incendiary-loaded guns `dangerous=True`, plain FMJ guns
  both False. Notes on behavior: grenades are `manualUse=True` and SS NEVER
  auto-equips manual-use weapons for player colonists, and SS's out-of-ammo
  re-equip path hardcodes skip-EMP (no target context) — so don't expect a pawn
  to auto-draw EMP grenades even against mechs; that eligibility only exists in
  the axis-7 mid-warmup swap, and only for non-manual EMP weapons (none in
  Core+CE).

## Save 3 — "combat flow" (axes 5, 6, 7)

Stage: colonist A: ranged primary + melee sidearm (knife/gladius). Colonist B:
sniper rifle + shotgun sidearm. Melee raider nearby.

- A6 (CQC): let melee raider reach colonist A → A must auto-draw the melee
  sidearm when attacked (SS CQC setting on).
- A7: order B to attack a target at close range while holding the sniper →
  during warmup B should swap to the shotgun (SS "ranged combat auto-switch"
  on; tune threshold in SS settings). Caveats, both SS-by-design (the swap
  trigger only runs during aim warmup with the current weapon): a target outside
  the CURRENT weapon's range can't even be ordered ("Out of range" — no job, no
  warmup, no swap; swap manually via gizmo), and no switch happens when only the
  current weapon can reach the target from where the pawn stands.
- A5: start a CE reload (empty mag, spare ammo in inventory, take cover) →
  while reload job runs, SS must not cancel it (watch job readout; reload
  completes).

## Save 4 — "generation + one-use" (axes 4, 8)

- A4: staged raiders are force-fed the SS generator until each carries a ranged
  ammo-using sidearm (natural generation is chance-rolled; melee shivs are
  common and irrelevant — no ammo to provision). Inspect raider Gear tabs:
  ranged sidearms have a full magazine + a spare ammo stack in inventory; melee
  sidearms correctly get nothing. For extra samples use Debug actions →
  **CE+SS Compat → Force-generate SS sidearm** on any pawn (logs the resulting
  inventory with mag counts). Watch dev log for CE "over capacity" warnings
  (should be none).
- A8: give colonist a one-use launcher (CE disposable, e.g. RPG-7/AT launcher
  variants) + a remembered pistol sidearm. Fire the launcher → launcher consumed,
  pawn re-equips per SS preference (pistol), not bare fists.

## Regression sweep

Load any save, play 10 min with dev log open: no red errors, no yellow spam from
Harmony/CE/SS, caravan dialog opens, pawn Gear tab renders, save+reload works.
