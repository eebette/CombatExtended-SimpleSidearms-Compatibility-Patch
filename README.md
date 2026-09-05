# CombatExtended-SimpleSidearms Compatibility Patch

[![Combat Extended Compatible](Media/Badge_CE_compatible.png)](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
[![CE + Simple Sidearms Compatibility Suite](Media/Badge_Suite.png)](#my-other-mods)
![CE + Simple Sidearms Compatibility Patch](Media/Badge_Patch.png)

RimWorld compatibility mod making [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended)
and [Simple Sidearms](https://github.com/PeteTimesSix/SimpleSidearms) work together.

See [My other mods](#my-other-mods) for additional modules to create a cohesive game experience while using both mods.

Inspired by the [discontinued mod by Ghosty](https://steamcommunity.com/sharedfiles/filedetails/?id=3694067502), I
decompiled that mod and searched *even harder* for incompatibilities between the mods.

## Fixes

- Sidearm carry limits now respect CE's inventory system (weight *and* bulk).
- Weapon ranking uses real CE damage numbers, so pawns actually pick the better gun.
- Pawns never auto-switch to a gun that has no ammo.
- Enemies spawn with their sidearms loaded and carrying spare ammo.
- Switching weapons no longer interrupts a reload partway through.
- Drawing a melee sidearm when attacked in melee works again.
- Mid-fight auto-switching to a better-suited gun works again.
- Firing a single-use launcher leaves the pawn holding their preferred backup, not fists.
- When a weapon is destroyed or used up, the replacement follows your sidearm preferences instead of CE's guess.
- CE loadout enforcement no longer strips remembered sidearms out of inventories.
- EMP and incendiary weapon detection matches the ammo actually loaded.
- Melee ranking now accounts for CE armor penetration.

## Load order

> Harmony → Combat Extended → Simple Sidearms → this mod.

## My other mods

### The CE + Simple Sidearms suite

Two optional modules sit on top of this patch and require it. This patch only repairs what the two mods break in each
other; anything opinionated lives in one of these instead.

| Module                                                                                                                                          | What it does                                                         |
|-------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------|
| [![Compatibility Module - Loadouts](Media/Badge_Loadouts.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Loadouts) | Synchronizes CE Loadouts with SS memory/gizmo.                       |
| [![Compatibility Module - Tactics](Media/Badge_Tactics.png)](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Tactics)    | Sensible tweaks to nonsense pawn behavior when CE + SS run together. |

### Standalone

None of these need this patch, or each other.

| Mod                                                                                                                                     | What it does                                                                                                                    |
|-----------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| [![Better Attack Orders for Simple Sidearms](Media/Badge_BAO.png)](https://github.com/eebette/Better-Attack-Orders-for-Simple-Sidearms) | Adds sidearm attack orders to the right-click target menu.                                                                      |
| [![Loadout Quality for Combat Extended](Media/Badge_LQ.png)](https://github.com/eebette/Loadout-Quality-for-Combat-Extended)            | Automatically upgrades a pawn's held guns when a higher-quality copy is available.                                              |
| [![Universal Patch for More Materials](Media/Badge_UPMM.png)](https://github.com/eebette/Universal-Patch-for-More-Materials)            | Adds materials from [More Materials](https://steamcommunity.com/sharedfiles/filedetails/?id=3055040889) to non-vanilla recipes. |

## FAQ

**CE compatible?**

I'm not answering that.

**Can I add or remove it mid-save?**

Both are safe. It writes nothing of its own to a save - no settings, no records, no scribed data. Remove it and you are
left with plain CE and plain Simple Sidearms.

**Does it change balance?**

It makes the game easier in the sense that 2 core combat mods are no longer broken in your save.

But in the traditional sense, no.

**Why is my pawn keeping a sidearm that isn't in its CE loadout?**

Pawns won't automatically drop any weapon Simple Sidearms remembers. Forget it in the SS gizmo to let CE drop it.

Use the [Loadouts module](https://github.com/eebette/CombatExtended-SimpleSidearms-Compatibility-Loadouts) for
synchronization between the CE Loadout and SS inventory.

**AI?**

This mod was engineered with the help of an AI Coding Assistant (Claude Code, Fable 5, Max effort). The amount of
researching and deep-diving the compatibility interfaces of both mods would have been insurmountable without it.

Development followed a standard process driven and scrutinized by a human (me, the person writing this):
explore, design, build, test, fix, review, scrutinize, test again over many rounds.

I have manually reviewed and verified all code in this mod.

I ask that if you have unconstructive feedback regarding the usage of AI while developing this mod, that it remains
outside of this community space. Thank you.

## Building

Requires the .NET SDK and local copies of both mods' assemblies (Steam Workshop subscription is enough):

```bash
dotnet build Source/CESimpleSidearmsCompat/CESimpleSidearmsCompat.csproj -c Release
```

The build references the workshop DLLs at
`~/.local/share/Steam/steamapps/workshop/content/294100/` (override with
`-p:RimWorldWorkshopDir=...`), compiles against
[Krafs.Rimworld.Ref](https://www.nuget.org/packages/Krafs.Rimworld.Ref) 1.6, and
uses [Krafs.Publicizer](https://github.com/krafs/Publicizer) for access to internal members of both mods. Output lands
in `Assemblies/`.

**No CI**: the compile references live in local Steam Workshop folders and can't be vendored (CE is CC BY-NC-SA, Simple
Sidearms has no license), so releases are manual local builds with the built DLL committed in `Assemblies/` - cloning
the repo yields a working mod without a toolchain. Full process: [RELEASING.md](RELEASING.md).

## Installing locally

Symlink (or copy) this folder into RimWorld's `Mods` directory:

```bash
ln -s "$(pwd)" ~/.local/share/Steam/steamapps/common/RimWorld/Mods/CESimpleSidearmsCompat
```

## Notes / limitations

- DPS scoring is a ranking proxy, not CE's ballistics: damage-per-cycle scaled by a hit factor built from the weapon's
  spread and the shooter's accuracy against CE's sway model. Good enough to order weapons the way CE's own accuracy
  would; not a prediction of any individual shot. Speed-bias behavior from SS settings is preserved.
- SS-remembered weapons are exempt from CE loadout drops by design. This covers every entry in the sidearm list,
  including weapons SS remembered automatically when a pawn equipped them. Remove the sidearm from SS memory to let CE
  drop it.

## Credit

- Thanks of course to PeteTimesSix and the CE team.
- Thanks to Ghosty for the initial research put in to find incompatibilities between the 2 mods.

## License

[MIT-licensed](LICENSE) - code, build files, and docs.

The badge artwork is not: `About/Preview.png` and the `Media/Badge_*.png` set remix the rifle glyph from Combat
Extended's own compatibility badge, so they stay under CE's CC BY-NC-SA 4.0 (attribution, non-commercial, share-alike).
Details in
[NOTICE](NOTICE).
