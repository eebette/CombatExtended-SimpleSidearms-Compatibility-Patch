using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using RimWorld;
using SimpleSidearms.rimworld;
using Verse;
using Verse.AI.Group;

namespace CESSCompatTestStaging
{
    /// <summary>
    /// Builds the CETEST-* staged saves described in TESTPLAN.md.
    /// Only runs when the game is launched with: -quicktest -cestage
    /// Each scenario is staged on the quicktest map, saved, then torn down.
    /// </summary>
    public class TestStagingComponent : GameComponent
    {
        private readonly List<Thing> staged = new List<Thing>();
        private IntVec3 anchor = IntVec3.Invalid;

        public TestStagingComponent(Game game)
        {
        }

        public override void StartedNewGame()
        {
            if (!GenCommandLine.CommandLineArgPassed("cestage"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    StageAll();
                }
                catch (Exception e)
                {
                    Log.Error("[CE+SS TestStaging] Staging failed: " + e);
                }
            });
        }

        private void StageAll()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Log.Error("[CE+SS TestStaging] No current map; launch with -quicktest -cestage.");
                return;
            }
            anchor = ComputeAnchor(map);
            Log.Message($"[CE+SS TestStaging] Map {map.Size}, staging anchor {anchor}.");

            Stage1_Pickup(map);
            SaveAndReset("CETEST-1-pickup");
            Stage2_Selection(map);
            SaveAndReset("CETEST-2-selection");
            Stage3_CombatFlow(map);
            SaveAndReset("CETEST-3-combat");
            Stage4_Generation(map);
            SaveAndReset("CETEST-4-generation");

            Find.TickManager.Pause();
            Log.Message("[CE+SS TestStaging] All CETEST saves created.");
            Find.LetterStack.ReceiveLetter("CETEST saves created",
                "Staged saves written: CETEST-1-pickup, CETEST-2-selection, CETEST-3-combat, CETEST-4-generation.\n\nQuit to main menu and Load one. See TESTPLAN.md for the per-save checklist.",
                LetterDefOf.PositiveEvent);
        }

        // ---- scenarios -----------------------------------------------------

        // Sidearm capacity + hold sync: bulk-capped pawn, heavy vs light sidearm on ground; CE
        // loadout that excludes a remembered sidearm.
        private void Stage1_Pickup(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Bulky", offset: new IntVec3(-4, 0, 0));
            ThingWithComps rifle = Equip(pawn, "Gun_AssaultRifle");
            ThingWithComps pistol = GiveSidearm(pawn, "Gun_Autopistol");

            // Fill inventory close to the CE bulk/weight cap with rifle ammo.
            FillInventoryWithAmmo(pawn, rifle);

            // Heavy and light pickup candidates next to the pawn.
            SpawnNear(map, pawn.Position, "Gun_LMG");
            SpawnNear(map, pawn.Position, "Gun_Revolver");

            // CE loadout that contains the rifle but NOT the remembered pistol.
            Loadout loadout = new Loadout("CETEST pickup loadout");
            loadout.AddSlot(new LoadoutSlot(rifle.def, 1));
            LoadoutManager.AddLoadout(loadout);
            pawn.SetLoadout(loadout);
        }

        // Ranged DPS, ammo-aware selection, switch arbitration, classification: loaded rifle + loaded pistol + empty SMG (no spare
        // 9mm), hostiles at range, EMP grenades, one mechanoid.
        private void Stage2_Selection(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Picky", offset: new IntVec3(0, 0, -4));
            Equip(pawn, "Gun_AssaultRifle");
            GiveSidearm(pawn, "Gun_Autopistol");
            // Dry gun must use a caliber nothing else in the inventory shares, or CE
            // (correctly) counts it as reloadable: revolver is the kit's only .44 user.
            // (Machine pistol won't do — it shares .45 ACP with the autopistol.)
            ThingWithComps dryGun = GiveSidearm(pawn, "Gun_Revolver", loaded: false);
            CompAmmoUser dryAmmo = dryGun.TryGetComp<CompAmmoUser>();
            if (dryAmmo != null)
            {
                dryAmmo.CurMagCount = 0; // deliberately dry, and no spare .44 given
            }
            GiveSidearm(pawn, "Weapon_GrenadeEMP", loaded: false); // ammo-classification probe

            SpawnHostiles(map, pawn.Position, count: 3, distance: 30);
            SpawnMechanoid(map, pawn.Position, distance: 35);
        }

        // Combat flow: CQC melee draw, warmup swap, reload protection.
        private void Stage3_CombatFlow(Map map)
        {
            Pawn brawlBait = SpawnColonist(map, "Fency", offset: new IntVec3(4, 0, 0));
            Equip(brawlBait, "Gun_AssaultRifle");
            GiveSidearm(brawlBait, "MeleeWeapon_Gladius", loaded: false);

            Pawn sniper = SpawnColonist(map, "Scopey", offset: new IntVec3(8, 0, 0));
            Equip(sniper, "Gun_SniperRifle");
            GiveSidearm(sniper, "Gun_PumpShotgun");

            // Melee-only raider that will close distance and trigger CQC.
            Pawn raider = SpawnHostiles(map, brawlBait.Position, count: 1, distance: 18).FirstOrDefault();
            if (raider != null)
            {
                raider.equipment?.DestroyAllEquipment();
                ThingWithComps club = Make("MeleeWeapon_Club");
                if (club != null)
                {
                    raider.equipment.AddEquipment(club);
                }
            }
        }

        // NPC sidearm ammo + one-use re-equip: SS-generated raider sidearms
        // (loaded + spare ammo via the NPC-sidearm-ammo patch), one-use launcher re-equip.
        private void Stage4_Generation(Map map)
        {
            List<Pawn> raiders = SpawnHostiles(map, anchor, count: 5, distance: 25);
            // Natural generation is chance-rolled and budget-capped, so ranged sidearms
            // are a coin flip; force the SS generator until each raider has one. The
            // compat patch's ammo provisioning (the NPC-sidearm-ammo patch) fires on every generator call.
            foreach (Pawn raider in raiders)
            {
                ForceRangedSidearm(raider);
            }

            Pawn rocketeer = SpawnColonist(map, "Boomy", offset: new IntVec3(0, 0, 4));
            ThingWithComps launcher = Equip(rocketeer, "Gun_TripleRocket");
            if (launcher == null)
            {
                Equip(rocketeer, "Gun_DoomsdayRocket");
            }
            GiveSidearm(rocketeer, "Gun_Autopistol");
        }

        // ---- helpers -------------------------------------------------------

        /// <summary>
        /// Runs SS's own sidearm generator (chance forced to 1, full budget) until the
        /// pawn carries a ranged CE-ammo-using sidearm, so the NPC-sidearm-ammo patch has
        /// something deterministic to act on. Weapon pick stays weighted-random.
        /// </summary>
        public static bool ForceRangedSidearm(Pawn pawn)
        {
            if (pawn?.kindDef == null || pawn.inventory == null)
            {
                return false;
            }
            var request = new PawnGenerationRequest(pawn.kindDef, pawn.Faction);
            for (int i = 0; i < 10; i++)
            {
                if (HasRangedAmmoSidearm(pawn))
                {
                    return true;
                }
                PawnSidearmsGenerator.TryGenerateSidearmFor(pawn, 1f, 1f, request);
            }
            return HasRangedAmmoSidearm(pawn);
        }

        private static bool HasRangedAmmoSidearm(Pawn pawn)
        {
            return pawn.inventory.innerContainer.OfType<ThingWithComps>()
                   .Any(w => w.def.IsRangedWeapon && w.TryGetComp<CompAmmoUser>() != null);
        }

        private void SaveAndReset(string name)
        {
            GameDataSaveLoader.SaveGame(name);
            foreach (Thing thing in staged)
            {
                if (thing is Pawn pawn)
                {
                    // Remove cross-references that would dangle in the NEXT save
                    // once this pawn is destroyed.
                    LoadoutManager._current?._assignedLoadouts?.Remove(pawn);
                    LoadoutManager._current?._assignedTrackers?.Remove(pawn);
                }
                if (thing != null && !thing.Destroyed)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
            staged.Clear();
            // Drop now-empty lords so they don't leak warnings into the next save.
            Map map = Find.CurrentMap;
            if (map != null)
            {
                foreach (Lord lord in map.lordManager.lords.Where(l => l.ownedPawns.Count == 0).ToList())
                {
                    map.lordManager.RemoveLord(lord);
                }
            }
        }

        /// <summary>Standable, unfogged cell; survives mountainous quicktest map centers.</summary>
        private static IntVec3 ComputeAnchor(Map map)
        {
            bool Valid(IntVec3 c) => c.Standable(map) && !c.Fogged(map);
            if (CellFinder.TryFindRandomCellNear(map.Center, map, 30, Valid, out IntVec3 cell))
            {
                return cell;
            }
            if (CellFinderLoose.TryGetRandomCellWith(Valid, map, 1000, out cell))
            {
                return cell;
            }
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.Standable(map))
                {
                    return c;
                }
            }
            return map.Center; // pathological map; spawns will complain loudly
        }

        private IntVec3 FindCell(Map map, IntVec3 near)
        {
            IntVec3 root = near.ClampInsideMap(map);
            if (CellFinder.TryFindRandomCellNear(root, map, 20, c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
            {
                return cell;
            }
            return anchor;
        }

        private Pawn SpawnColonist(Map map, string nick, IntVec3 offset)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                          PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                          canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.Name = new NameTriple("Test", nick, "CETEST");
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            SkillRecord shooting = pawn.skills?.GetSkill(SkillDefOf.Shooting);
            if (shooting != null)
            {
                shooting.Level = 12;
            }
            SkillRecord melee = pawn.skills?.GetSkill(SkillDefOf.Melee);
            if (melee != null)
            {
                melee.Level = 12;
            }
            IntVec3 cell = FindCell(map, anchor + offset);
            GenSpawn.Spawn(pawn, cell, map);
            staged.Add(pawn);
            return pawn;
        }

        private ThingWithComps Make(string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Warning("[CE+SS TestStaging] Missing def: " + defName);
                return null;
            }
            return (ThingWithComps)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
        }

        private ThingWithComps Equip(Pawn pawn, string defName)
        {
            ThingWithComps weapon = Make(defName);
            if (weapon == null)
            {
                return null;
            }
            pawn.equipment.AddEquipment(weapon);
            LoadWithAmmo(pawn, weapon);
            return weapon;
        }

        private ThingWithComps GiveSidearm(Pawn pawn, string defName, bool loaded = true)
        {
            ThingWithComps weapon = Make(defName);
            if (weapon == null)
            {
                return null;
            }
            pawn.inventory.innerContainer.TryAdd(weapon, true);
            if (loaded)
            {
                LoadWithAmmo(pawn, weapon);
            }
            CompSidearmMemory.GetMemoryCompForPawn(pawn)?.InformOfAddedSidearm(weapon);
            return weapon;
        }

        private void LoadWithAmmo(Pawn pawn, ThingWithComps weapon)
        {
            CompAmmoUser ammoUser = weapon.TryGetComp<CompAmmoUser>();
            if (ammoUser == null || !ammoUser.UseAmmo)
            {
                return;
            }
            ammoUser.ResetAmmoCount(); // fill the magazine
            AmmoDef ammoDef = ammoUser.CurrentAmmo ?? ammoUser.SelectedAmmo;
            if (ammoDef == null)
            {
                return;
            }
            Thing spare = ThingMaker.MakeThing(ammoDef);
            spare.stackCount = Math.Max(1, ammoUser.MagSize) * 2;
            pawn.inventory.innerContainer.TryAdd(spare, true);
        }

        private void FillInventoryWithAmmo(Pawn pawn, ThingWithComps gun)
        {
            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            AmmoDef ammoDef = gun?.TryGetComp<CompAmmoUser>()?.CurrentAmmo;
            if (inventory == null || ammoDef == null)
            {
                return;
            }
            for (int i = 0; i < 40; i++)
            {
                Thing stack = ThingMaker.MakeThing(ammoDef);
                stack.stackCount = ammoDef.stackLimit;
                if (!inventory.CanFitInInventory(stack, out int fit) || fit <= 0)
                {
                    break;
                }
                stack.stackCount = Math.Min(stack.stackCount, fit);
                inventory.container.TryAdd(stack, true);
            }
            inventory.UpdateInventory();
        }

        private void SpawnNear(Map map, IntVec3 near, string defName)
        {
            ThingWithComps thing = Make(defName);
            if (thing == null)
            {
                return;
            }
            IntVec3 cell = FindCell(map, near + new IntVec3(2, 0, 2));
            GenSpawn.Spawn(thing, cell, map);
            staged.Add(thing);
        }

        private List<Pawn> SpawnHostiles(Map map, IntVec3 around, int count, int distance)
        {
            var result = new List<Pawn>();
            Faction pirates = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate);
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate_Gunner")
                               ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate")
                               ?? PawnKindDefOf.Drifter;
            if (pirates == null)
            {
                Log.Warning("[CE+SS TestStaging] No pirate faction on this world; skipping hostiles.");
                return result;
            }
            for (int i = 0; i < count; i++)
            {
                var request = new PawnGenerationRequest(kind, pirates, PawnGenerationContext.NonPlayer,
                              forceGenerateNewPawn: true, canGeneratePawnRelations: false);
                Pawn pawn = PawnGenerator.GeneratePawn(request);
                IntVec3 cell = FindCell(map, around + new IntVec3(distance, 0, i * 2));
                GenSpawn.Spawn(pawn, cell, map);
                staged.Add(pawn);
                result.Add(pawn);
            }
            // Without a Lord the pawns have no assault AI and immediately panic/leave the
            // map; flee/timeout disabled so the staged fight stays put after loading.
            if (result.Count > 0)
            {
                LordMaker.MakeNewLord(pirates,
                    new LordJob_AssaultColony(pirates, canKidnap: false, canTimeoutOrFlee: false, sappers: false,
                                              useAvoidGridSmart: false, canSteal: false), map, result);
            }
            return result;
        }

        private void SpawnMechanoid(Map map, IntVec3 around, int distance)
        {
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Scyther");
            Faction mechs = Faction.OfMechanoids;
            if (kind == null || mechs == null)
            {
                return;
            }
            var request = new PawnGenerationRequest(kind, mechs, PawnGenerationContext.NonPlayer,
                          forceGenerateNewPawn: true, canGeneratePawnRelations: false);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            IntVec3 cell = FindCell(map, around + new IntVec3(-distance, 0, distance));
            GenSpawn.Spawn(pawn, cell, map);
            staged.Add(pawn);
            LordMaker.MakeNewLord(mechs,
                new LordJob_AssaultColony(mechs, canKidnap: false, canTimeoutOrFlee: false, sappers: false,
                                          useAvoidGridSmart: false, canSteal: false), map, new List<Pawn> { pawn });
        }
    }
}
