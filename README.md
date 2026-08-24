# CombatExtended-SimpleSidearms Compatibility Patch

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
[![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)](#my-other-mods)
![CE + Simple Sidearms Compatibility Patch](Media/Badge_Patch.png)

RimWorld compatibility mod making [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended) and [Simple Sidearms](https://github.com/PeteTimesSix/SimpleSidearms) work together.

This mod patches core compatibility issues but doesn't bridge AI behaviors or UI elements (see [My other mods](#my-other-mods)).

Inspired by the [discontinued mod by Ghosty](https://steamcommunity.com/sharedfiles/filedetails/?id=3694067502), I decompiled that mod and searched *even harder* for incompatibilities between the mods.

## Fixes

- Sidearm carry limits now respect CE's inventory system (weight *and* bulk). *(#1)*
- Weapon ranking uses real CE damage numbers, so pawns actually pick the better gun. *(#2)*
- Pawns never auto-switch to a gun that has no ammo. *(#3)*
- Enemies spawn with their sidearms loaded and carrying spare ammo. *(#4)*
- Switching weapons no longer interrupts a reload partway through. *(#5)*
- Drawing a melee sidearm when attacked in melee works again. *(#6)*
- Mid-fight auto-switching to a better-suited gun works again. *(#7)*
- Firing a single-use launcher leaves the pawn holding their preferred backup, not fists. *(#8)*
- When a weapon is destroyed or used up, the replacement follows your sidearm
  preferences instead of CE's guess. *(#9)*
- CE loadout enforcement no longer strips remembered sidearms out of inventories. *(#10)*
- EMP and incendiary weapon detection matches the ammo actually loaded. *(#11)*

## Load order

> Harmony → Combat Extended → Simple Sidearms → this mod.
 
## My other mods

### The CE + Simple Sidearms suite

Two optional modules sit on top of this patch and require it. This patch only repairs
what the two mods break in each other; anything that would *change* how they behave
lives out here, so you can take the repairs without taking my opinions.

[![Compatibility Module - Loadouts](Media/Badge_Loadouts.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Loadouts)

CE loadouts and Simple Sidearms memory as one idea: weapons listed in a loadout are
remembered as sidearms, roles follow list order, and their ammo is kept stocked off CE's
own ad-hoc flag.

*For you if you manage kit through CE loadouts and are tired of teaching every new
recruit their sidearms by hand.*

[![Compatibility Module - Tactics](Media/Badge_Tactics.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Tactics)

Better triggers for *when* a pawn changes weapon: don't abandon a reload to switch,
break DPS ties by how much ammo is actually carried, pick a melee weapon that can hurt
what is standing in front of you.

*For you if your pawns pick sensible weapons at senseless moments.*

### Standalone

None of these need this patch, or each other.

[![Better Attack Orders for Simple Sidearms](Media/Badge_BAO.png)](https://github.com/eebette/Better-Attack-Orders-for-Simple-Sidearms)

Simple Sidearms only, no CE. Your pawn holds a revolver and carries a sniper rifle;
right-clicking a distant enemy tells you they can't reach. This makes attack orders
consider every gun the pawn is carrying.

*For you if you have ever manually swapped a weapon just to issue an attack order.*

[![Loadout Quality for Combat Extended](Media/Badge_LQ.png)](https://github.com/eebette/Loadout-Quality-for-Combat-Extended)

Combat Extended only, no Simple Sidearms. Per-loadout quality and durability floors, and
pawns upgrade themselves when a better copy of a weapon they already carry shows up.

*For you if "normal or better, no tainted" is a rule you have been enforcing by hand.*

[![Universal Patch for More Materials](Media/Badge_UPMM.png)](https://github.com/eebette/Universal-Patch-for-More-Materials)

No weapons involved at all. [More Materials](https://steamcommunity.com/sharedfiles/filedetails/?id=3055040889)
adds copper, tin, nickel and five other metals — to vanilla recipes, and to no modded
ones. This works out the rules behind those choices (*anything with a power comp and a
steel cost needs copper*) and applies them categorically, so mods it has never seen are
covered, including ones published after it. Measured on a 305-mod list: 221 buildings
across 43 content mods, no vanilla def touched.

*For you if you run More Materials and noticed modded buildings quietly sidestepping the
new metals.*

## FAQ

**CE compatible?**

I'm not answering that.

**Can I add or remove it mid-save?**

Both are safe. It writes nothing of its own to a save - no settings, no records, no scribed data. Remove it and you are left with plain CE and plain Simple Sidearms.

**Does it change balance?**

It makes the game easier in the sense that 2 core combat mods are no longer broken in your save.

But in the traditional sense, no.

**Why is my pawn keeping a sidearm that isn't in its CE loadout?**

Pawns won't automatically drop any weapon Simple Sidearms remembers. Forget it in the SS gizmo to let CE drop it.

If you'd rather the loadout be the authority — in the loadout means remembered, out of it means forgotten — that's what the [Loadouts module](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Loadouts) changes.

**Why did my pawn switch weapons *then*?**

This patch restores Simple Sidearms' own switching triggers, which CE had silently broken;
it doesn't second-guess them. Changing when those triggers fire is the
[Tactics module](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Tactics).

**Does it work with Melee Animation?**

Yes, with one known gap: animated execution kills bypass the melee-attack hook, so the CQC melee auto-draw doesn't trigger during those. Everything else works.

**AI?**

This mod was engineered with the help of an AI Coding Assistant (Claude Code, Fable 5, Max effort). The amount of researching and deep-diving the compatibility interfaces of both mods would have been insurmountable without it.

I ask that if you have unconstructive feedback regarding the usage of AI while developing this mod, that it remains outside of this community space. Thank you.

## Fixes (for nerds)

| # | Problem under CE | Fix |
|---|------------------|-----|
| 1 | SS decides what may be carried as a sidearm without CE **bulk** (weight is already CE-aware via CE's `MassUtility.Capacity` patch), and its own retrieval checks neither | Bulk check appended to `StatCalculator.CanPickupSidearmType` (also gates NPC sidearm generation); retrievals CE has no room for are cancelled in `JobGiver_RetrieveWeapon` |
| 2 | SS ranks ranged weapons with vanilla stats - meaningless for CE guns | CE-model DPS (ammo projectile damage, burst, reload amortization) with SS's speed-bias semantics preserved |
| 3 | SS can auto-switch a pawn to a gun with **no ammo** | SS's own selection is re-run with dry guns hidden, so its whole filter chain (including its rules for other mods' shields and dual-wielding) picks the fallback |
| 4 | SS-generated NPC sidearms spawn with empty mags, no spare ammo | Post-generation: magazines filled, spare mags added within CE inventory capacity (count from CE's `LoadoutPropertiesExtension` when present) |
| 5 | SS auto-swaps interrupt CE reload jobs | Idle preference swaps suppressed during `ReloadWeapon`; explicit swaps end the reload cleanly first. Melee draws and used-up replacements still fire - a pawn attacked mid-reload must still draw |
| 6 | SS CQC ("draw melee when melee-attacked") dead - `Verb_MeleeAttackCE` overrides the hooked method | SS's CQC postfix mirrored onto `Verb_MeleeAttackCE.TryCastShot` |
| 7 | SS mid-combat ranged auto-switch dead - requires `Verb_Shoot`, CE uses `Verb_ShootCE` | SS's `Stance_Warmup` logic replicated for CE shoot verbs (reuses SS settings/helpers), with the warmup window read from the stance so CE's shortened repeat-shot aim still counts |
| 8 | One-use weapons (single-shot launchers): SS re-equip hook never fires (`Verb_ShootCEOneUse` is a separate class) | Post-`SelfConsume` fallback equips by SS preference when the pawn ends up empty-handed |
| 9 | CE's `SwitchToNextViableWeapon` (weapon destroyed, one-use consumed, grenade thrown, empty gun mid-cast) ignores SS preferences | For SS-managed pawns, SS picks the weapon and CE keeps the cost: where CE asked for an `EquipFromInventory` job rather than an instant swap, SS's choice is handed back to CE as a candidate filter and CE queues its own job. CE logic (incl. fists) is the fallback. A pawn SS deliberately keeps unarmed stays unarmed. **Not covered:** an equipped gun running dry resolves inside `CompAmmoUser.DoOutOfAmmoAction`, which equips from inventory itself and never reaches this method |
| 10 | CE loadout enforcement drops SS-remembered sidearms (drop/retrieve churn) | `GetExcessThing`/`GetExcessEquipment` answer "is this remembered?" from SS memory directly. Read-only: CE's hold-tracker is shared with the player's own hold command, so nothing is written into it |
| 11 | SS EMP/dangerous-weapon detection reads the verb's default projectile, not the loaded CE ammo | Classification re-evaluated from the current CE projectile - or, on an empty magazine, the ammo the next reload will chamber |

## Building

Requires the .NET SDK and local copies of both mods' assemblies (Steam Workshop
subscription is enough):

```bash
dotnet build Source/CESimpleSidearmsCompat/CESimpleSidearmsCompat.csproj -c Release
```

The build references the workshop DLLs at
`~/.local/share/Steam/steamapps/workshop/content/294100/` (override with
`-p:RimWorldWorkshopDir=...`), compiles against
[Krafs.Rimworld.Ref](https://www.nuget.org/packages/Krafs.Rimworld.Ref) 1.6, and
uses [Krafs.Publicizer](https://github.com/krafs/Publicizer) for access to
internal members of both mods. Output lands in `Assemblies/`.

**No CI**: the compile references live in local Steam Workshop folders and can't
be vendored (CE is CC BY-NC-SA, Simple Sidearms has no license), so releases are
manual local builds with the built DLL committed in `Assemblies/` - cloning the
repo yields a working mod without a toolchain. Full process: [RELEASING.md](RELEASING.md).

## Installing locally

Symlink (or copy) this folder into RimWorld's `Mods` directory:

```bash
ln -s "$(pwd)" ~/.local/share/Steam/steamapps/common/RimWorld/Mods/CESimpleSidearmsCompat
```

## Notes / limitations

- DPS scoring is a ranking proxy, not CE's ballistics: damage-per-cycle scaled by a
  hit factor built from the weapon's spread and the shooter's accuracy against CE's
  sway model. Good enough to order weapons the way CE's own accuracy would; not a
  prediction of any individual shot. Speed-bias behavior from SS settings is preserved.
- SS-remembered weapons are exempt from CE loadout drops by design. This covers every
  entry in the sidearm list, including weapons SS remembered automatically when a pawn
  equipped them. Remove the sidearm from SS memory to let CE drop it.

## Credit

- Thanks of course to PeteTimesSix and the CE team.
- Thanks to Ghosty for the initial research put in to find incompatibilities between the 2 mods.

## License

[MIT-licensed](LICENSE) - code, build files, and docs.

The badge artwork is not: `About/Preview.png` and the `Media/Badge_*.png` set remix
the rifle glyph from Combat Extended's own compatibility badge, so they stay under
CE's CC BY-NC-SA 4.0 (attribution, non-commercial, share-alike). Details in
[NOTICE](NOTICE).
