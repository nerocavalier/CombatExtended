﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;
using UnityEngine;

namespace CombatExtended
{
    /* Class is cloned from Building_TurretGun with various changes made to support fire modes and ammo
     * 
     * Unmodified methods should be kept up-to-date with vanilla class so long as they don't conflict with changes made. Please mark any changes you make from vanilla.
     * -NIA
     */
    [StaticConstructorOnStartup]
    public class Building_TurretGunCE : Building_Turret
    {
        private const int minTicksBeforeAutoReload = 1800;              // This much time must pass before haulers will try to automatically reload an auto-turret

        #region Fields

        protected int burstCooldownTicksLeft;
        public int burstWarmupTicksLeft;                                // Need this public so aim mode can modify it
        protected LocalTargetInfo currentTargetInt = LocalTargetInfo.Invalid;
        private bool holdFire;
        private Thing gunInt;                                           // Better to be private, because Gun is used for access, instead
        protected TurretTop top;
        protected CompPowerTrader powerComp;
        protected CompCanBeDormant dormantComp;
        protected CompMannable mannableComp;

        public static Material ForcedTargetLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, new Color(1f, 0.5f, 0.5f));

        // New fields
        private CompAmmoUser compAmmo = null;
        private CompFireModes compFireModes = null;
        private CompChangeableProjectile compChangeable = null;
        public bool isReloading = false;
        private int ticksUntilAutoReload = 0;

        #endregion

        #region Properties
        // Core properties
        public bool Active => (powerComp == null || powerComp.PowerOn) && (dormantComp == null || dormantComp.Awake);
        public CompEquippable GunCompEq => Gun.TryGetComp<CompEquippable>();
        public override LocalTargetInfo CurrentTarget => currentTargetInt;
        private bool WarmingUp => burstWarmupTicksLeft > 0;
        public override Verb AttackVerb => Gun == null ? null : GunCompEq.verbTracker.PrimaryVerb;
        public bool IsMannable => mannableComp != null;
        public bool PlayerControlled => (Faction == Faction.OfPlayer || MannedByColonist) && !MannedByNonColonist;
        private bool CanSetForcedTarget => mannableComp != null && PlayerControlled;
        private bool CanToggleHoldFire => PlayerControlled;
        private bool IsMortar => def.building.IsMortar;
        private bool IsMortarOrProjectileFliesOverhead => Projectile.projectile.flyOverhead || IsMortar;
        //Not included: CanExtractShell
        private bool MannedByColonist => mannableComp?.ManningPawn?.Faction == Faction.OfPlayer;
        private bool MannedByNonColonist => mannableComp?.ManningPawn?.Faction != Faction.OfPlayer;

        // New properties
        public Thing Gun
        {
            get
            {
                if (this.gunInt == null && Map != null)
                {
                    this.gunInt = ThingMaker.MakeThing(this.def.building.turretGunDef, null);
                    this.compAmmo = gunInt.TryGetComp<CompAmmoUser>();
                    
                    InitGun();
                    
                    // FIXME: Hack to make player-crafted turrets spawn unloaded
                    if (//Map != null && !Map.IsPlayerHome
                        //!Faction.IsPlayer
                        (!Map.IsPlayerHome || Faction != Faction.OfPlayer) && compAmmo != null)
                    {
                        compAmmo.ResetAmmoCount();
                    }
                }
                return this.gunInt;
            }
        }
        public ThingDef Projectile
        {
            get
            {
                if (CompAmmo != null && CompAmmo.CurrentAmmo != null)
                {
                	return CompAmmo.CurAmmoProjectile;
                }
                if (CompChangeable != null && CompChangeable.Loaded)
                {
                	return CompChangeable.Projectile;
                }
                return this.GunCompEq.PrimaryVerb.verbProps.defaultProjectile;
            }
        }
        public CompChangeableProjectile CompChangeable
        {
        	get
        	{
	            if (compChangeable == null && Gun != null) compChangeable = Gun.TryGetComp<CompChangeableProjectile>();
	            return compChangeable;
        	}
        }
        public CompAmmoUser CompAmmo
        {
            get
            {
                if (compAmmo == null && Gun != null) compAmmo = Gun.TryGetComp<CompAmmoUser>();
                return compAmmo;
            }
        }
        public CompFireModes CompFireModes
        {
            get
            {
                if (compFireModes == null && Gun != null) compFireModes = Gun.TryGetComp<CompFireModes>();
                return compFireModes;
            }
        }
        public bool NeedsReload => (mannableComp == null || !mannableComp.MannedNow)
                    && CompAmmo != null && CompAmmo.HasMagazine
                    && (CompAmmo.CurMagCount < CompAmmo.Props.magazineSize || CompAmmo.SelectedAmmo != CompAmmo.CurrentAmmo);
        public bool AllowAutomaticReload => (mannableComp == null || (!mannableComp.MannedNow && ticksUntilAutoReload == 0))     //suppress manned turret auto-reload for a short time after spawning
                    && CompAmmo != null && CompAmmo.HasMagazine
                    && (ticksUntilAutoReload == 0 || CompAmmo.CurMagCount <= Mathf.CeilToInt(CompAmmo.Props.magazineSize / 6));
        public CompMannable MannableComp => mannableComp;
        #endregion

        #region Constructors
        // Core constructor
        public Building_TurretGunCE()
        {
            top = new TurretTop(this);
        }
        #endregion

        #region Methods
        
        public override void SpawnSetup(Map map, bool respawningAfterLoad)      //Add mannableComp, ticksUntilAutoReload stuff
        {
            base.SpawnSetup(map, respawningAfterLoad);
            dormantComp = GetComp<CompCanBeDormant>();
            powerComp = GetComp<CompPowerTrader>();
            mannableComp = GetComp<CompMannable>();
            if (!respawningAfterLoad)
            {
                top.SetRotationFromOrientation();
                burstCooldownTicksLeft = def.building.turretInitialCooldownTime.SecondsToTicks();

                //Delay auto-reload for a few seconds after spawn, so player can operate the turret right after placing it, before other colonists start reserving it for reload jobs
                if (mannableComp != null)
                    ticksUntilAutoReload = minTicksBeforeAutoReload;
            }
        }

        //PostMake not added -- MakeGun-like code is run whenever Gun is called

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)    // Core method
        {
            base.DeSpawn(mode);
            ResetCurrentTarget();
        }

        
        public override void ExposeData()           // Added new variables, removed bool loaded (not used in CE)
        {
            base.ExposeData();

            // New variables
            Scribe_Deep.Look(ref gunInt, "gunInt");
            InitGun();
            Scribe_Values.Look(ref isReloading, "isReloading", false);
            Scribe_Values.Look(ref ticksUntilAutoReload, "ticksUntilAutoReload", 0);

            Scribe_Values.Look<int>(ref this.burstCooldownTicksLeft, "burstCooldownTicksLeft", 0, false);
            Scribe_Values.Look<int>(ref this.burstWarmupTicksLeft, "burstWarmupTicksLeft", 0, false);
            Scribe_TargetInfo.Look(ref this.currentTargetInt, "currentTarget");
            Scribe_Values.Look<bool>(ref this.holdFire, "holdFire", false, false);
            BackCompatibility.PostExposeData(this);
        }

        public override bool ClaimableBy(Faction by)        // Core method
        {
            return base.ClaimableBy(by) && (this.mannableComp == null || this.mannableComp.ManningPawn == null) && (!this.Active || this.mannableComp != null) && (this.dormantComp == null || this.dormantComp.Awake || (this.powerComp != null && !this.powerComp.PowerOn));
        }

        public override void OrderAttack(LocalTargetInfo targ)      // Core method
        {
            if (!targ.IsValid)
            {
                if (this.forcedTarget.IsValid)
                {
                    this.ResetForcedTarget();
                }
                return;
            }
            if ((targ.Cell - base.Position).LengthHorizontal < this.GunCompEq.PrimaryVerb.verbProps.minRange)
            {
                Messages.Message("MessageTargetBelowMinimumRange".Translate(), this, MessageTypeDefOf.RejectInput);
                return;
            }
            if ((targ.Cell - base.Position).LengthHorizontal > this.GunCompEq.PrimaryVerb.verbProps.range)
            {
                Messages.Message("MessageTargetBeyondMaximumRange".Translate(), this, MessageTypeDefOf.RejectInput);
                return;
            }
            if (this.forcedTarget != targ)
            {
                this.forcedTarget = targ;
                if (this.burstCooldownTicksLeft <= 0)
                {
                    this.TryStartShootSomething(false);
                }
            }
            if (this.holdFire)
            {
                Messages.Message("MessageTurretWontFireBecauseHoldFire".Translate(this.def.label), this, MessageTypeDefOf.RejectInput, false);
            }
        }

        public override void Tick()     //Autoreload code and IsReloading check
        {
            base.Tick();
            if (ticksUntilAutoReload > 0) ticksUntilAutoReload--;   // Reduce time until we can auto-reload
            if (CompAmmo?.CurMagCount == 0 && !isReloading && (MannableComp?.MannedNow ?? false)) TryOrderReload();
            if (!CanSetForcedTarget && !isReloading && forcedTarget.IsValid && burstCooldownTicksLeft <= 0)
            {
                ResetForcedTarget();
            }
            if (!CanToggleHoldFire)
            {
                holdFire = false;
            }
            if (forcedTarget.ThingDestroyed)
            {
                ResetForcedTarget();
            }
            if (Active && (this.mannableComp == null || this.mannableComp.MannedNow) && base.Spawned)
            {
                this.GunCompEq.verbTracker.VerbsTick();
                if (!this.stunner.Stunned && this.GunCompEq.PrimaryVerb.state != VerbState.Bursting)
                {
                    if (this.WarmingUp)
                    {
                        this.burstWarmupTicksLeft--;
                        if (this.burstWarmupTicksLeft == 0)
                        {
                            this.BeginBurst();
                        }
                    }
                    else
                    {
                        if (this.burstCooldownTicksLeft > 0)
                        {
                            this.burstCooldownTicksLeft--;
                        }
                        if (this.burstCooldownTicksLeft <= 0)
                        {
                            this.TryStartShootSomething(true);
                        }
                    }
                    this.top.TurretTopTick();
                    return;
                }
            }
            else
            {
                this.ResetCurrentTarget();
            }
        }
        
        protected void TryStartShootSomething(bool canBeginBurstImmediately)    // Added ammo check and use verb warmup time instead of turret's
        {
            // Check for ammo first
            if (!Spawned
                || (holdFire && CanToggleHoldFire)
                || (Projectile.projectile.flyOverhead && Map.roofGrid.Roofed(Position))
              //|| !AttackVerb.Available()  -- Check replaced by the following:
                || (CompAmmo != null && (isReloading || (mannableComp == null && CompAmmo.CurMagCount <= 0))))
            {
                ResetCurrentTarget();
                return;
            }
            bool isValid = currentTargetInt.IsValid;
            currentTargetInt = forcedTarget.IsValid ? forcedTarget : TryFindNewTarget();
            if (!isValid && currentTargetInt.IsValid)
            {
                SoundDefOf.TurretAcquireTarget.PlayOneShot(new TargetInfo(Position, Map, false));
            }
            if (!currentTargetInt.IsValid)
            {
                ResetCurrentTarget();
                return;
            }
            // Use verb warmup time instead of turret's
            if (AttackVerb.verbProps.warmupTime > 0f)
            {
                burstWarmupTicksLeft = AttackVerb.verbProps.warmupTime.SecondsToTicks();
                return;
            }
            if (canBeginBurstImmediately)
            {
                BeginBurst();
                return;
            }
            burstWarmupTicksLeft = 1;
        }
        
        protected LocalTargetInfo TryFindNewTarget()    // Core method
        {
            IAttackTargetSearcher attackTargetSearcher = this.TargSearcher();
            Faction faction = attackTargetSearcher.Thing.Faction;
            float range = this.AttackVerb.verbProps.range;
            Building t;
            if (Rand.Value < 0.5f && this.AttackVerb.ProjectileFliesOverhead() && faction.HostileTo(Faction.OfPlayer) && base.Map.listerBuildings.allBuildingsColonist.Where(delegate (Building x)
            {
                float num = this.AttackVerb.verbProps.EffectiveMinRange(x, this);
                float num2 = (float)x.Position.DistanceToSquared(this.Position);
                return num2 > num * num && num2 < range * range;
            }).TryRandomElement(out t))
            {
                return t;
            }
            TargetScanFlags targetScanFlags = TargetScanFlags.NeedThreat;
            if (!this.AttackVerb.ProjectileFliesOverhead())
            {
                targetScanFlags |= TargetScanFlags.NeedLOSToAll;
                targetScanFlags |= TargetScanFlags.LOSBlockableByGas;
            }
            if (this.AttackVerb.IsIncendiary())
            {
                targetScanFlags |= TargetScanFlags.NeedNonBurning;
            }
            return (Thing)AttackTargetFinder.BestShootTargetFromCurrentPosition(attackTargetSearcher, targetScanFlags, new Predicate<Thing>(this.IsValidTarget), 0f, 9999f);
        }

        private IAttackTargetSearcher TargSearcher()    // Core method
        {
            if (mannableComp != null && mannableComp.MannedNow)
            {
                return mannableComp.ManningPawn;
            }
            return this;
        }

        private bool IsValidTarget(Thing t)             // Projectile flyoverhead check instead of verb
        {
            Pawn pawn = t as Pawn;
            if (pawn != null)
            {
                //if (this.GunCompEq.PrimaryVerb.verbProps.projectileDef.projectile.flyOverhead)
                if (Projectile.projectile.flyOverhead)
                {
                    RoofDef roofDef = base.Map.roofGrid.RoofAt(t.Position);
                    if (roofDef != null && roofDef.isThickRoof)
                    {
                        return false;
                    }
                }
                if (this.mannableComp == null)
                {
                    return !GenAI.MachinesLike(base.Faction, pawn);
                }
                if (pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer)
                {
                    return false;
                }
            }
            return true;
        }
        
        protected void BeginBurst()                     // Added handling for ticksUntilAutoReload
        {
            ticksUntilAutoReload = minTicksBeforeAutoReload;
            AttackVerb.TryStartCastOn(CurrentTarget, false, true);
            OnAttackedTarget(CurrentTarget);
        }
        
        protected void BurstComplete()                  // Added CompAmmo reload check
        {
            burstCooldownTicksLeft = BurstCooldownTime().SecondsToTicks();
            if (CompAmmo != null && CompAmmo.CurMagCount <= 0)
                TryOrderReload();
        }

        protected float BurstCooldownTime()             // Core method
        {
            if (def.building.turretBurstCooldownTime >= 0f)
            {
                return def.building.turretBurstCooldownTime;
            }
            return AttackVerb.verbProps.defaultCooldownTime;
        }
        
        public override string GetInspectString()       // Replaced vanilla loaded text with CE reloading
        {
            StringBuilder stringBuilder = new StringBuilder();
            string inspectString = base.GetInspectString();
            if (!inspectString.NullOrEmpty())
            {
                stringBuilder.AppendLine(inspectString);
            }
            
            stringBuilder.AppendLine("GunInstalled".Translate() + ": " + this.Gun.LabelCap);    // New code

            if (this.GunCompEq.PrimaryVerb.verbProps.minRange > 0f)
            {
                stringBuilder.AppendLine("MinimumRange".Translate() + ": " + this.GunCompEq.PrimaryVerb.verbProps.minRange.ToString("F0"));
            }

            if (isReloading)        // New code
            {
                stringBuilder.AppendLine("CE_TurretReloading".Translate());
            }

            else if (Spawned && IsMortarOrProjectileFliesOverhead && Position.Roofed(Map))
            {
                stringBuilder.AppendLine("CannotFire".Translate() + ": " + "Roofed".Translate().CapitalizeFirst());
            }
            else if (Spawned && burstCooldownTicksLeft > 0)
            {
                stringBuilder.AppendLine("CanFireIn".Translate() + ": " + this.burstCooldownTicksLeft.ToStringSecondsFromTicks());
            }
            /*
            if (this.def.building.turretShellDef != null)
            {
                if (this.loaded)
                {
                    stringBuilder.AppendLine("ShellLoaded".Translate());
                }
                else
                {
                    stringBuilder.AppendLine("ShellNotLoaded".Translate());
                }
            }
            */
            return stringBuilder.ToString().TrimEndNewlines();
        }


        public override void Draw()                     // Core method
        {
            top.DrawTurret();
            base.Draw();
        }

        public override void DrawExtraSelectionOverlays()           // Draw at range less than 1.42 tiles
        {
            float range = this.GunCompEq.PrimaryVerb.verbProps.range;
            if (range < 90f)
            {
                GenDraw.DrawRadiusRing(base.Position, range);
            }
            float minRange = AttackVerb.verbProps.minRange;     // Changed to minRange instead of EffectiveMinRange
            if (minRange < 90f && minRange > 0.1f)
            {
                GenDraw.DrawRadiusRing(base.Position, minRange);
            }
            if (this.WarmingUp)
            {
                int degreesWide = (int)((float)this.burstWarmupTicksLeft * 0.5f);
                GenDraw.DrawAimPie(this, this.CurrentTarget, degreesWide, def.size.x * 0.5f);
            }
            if (this.forcedTarget.IsValid && (!this.forcedTarget.HasThing || this.forcedTarget.Thing.Spawned))
            {
                Vector3 b;
                if (this.forcedTarget.HasThing)
                {
                    b = this.forcedTarget.Thing.TrueCenter();
                }
                else
                {
                    b = this.forcedTarget.Cell.ToVector3Shifted();
                }
                Vector3 a = this.TrueCenter();
                b.y = Altitudes.AltitudeFor(AltitudeLayer.MetaOverlays);
                a.y = b.y;
                GenDraw.DrawLineBetween(a, b, Building_TurretGun.ForcedTargetLineMat);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()              // Modified
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }
            // Ammo gizmos
            if (CompAmmo != null && (PlayerControlled || Prefs.DevMode))
            {
                foreach (Command com in CompAmmo.CompGetGizmosExtra())
                {
                    if (!PlayerControlled && Prefs.DevMode && com is GizmoAmmoStatus)
                        (com as GizmoAmmoStatus).prefix = "DEV: ";

                    yield return com;
                }
            }
            // Don't show CONTROL gizmos on enemy turrets (even with dev mode enabled)
            if (PlayerControlled)
            {
                // Fire mode gizmos
                if (CompFireModes != null)
                {
                    foreach (Command com in CompFireModes.GenerateGizmos())
                    {
                        yield return com;
                    }
                }
                // Set forced target gizmo
                if (CanSetForcedTarget)
                {
                    var vt = new Command_VerbTarget
                    {
                        defaultLabel = "CommandSetForceAttackTarget".Translate(),
                        defaultDesc = "CommandSetForceAttackTargetDesc".Translate(),
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", true),
                        verb = GunCompEq.PrimaryVerb,
                        hotKey = KeyBindingDefOf.Misc4
                    };
                    if (Spawned && IsMortarOrProjectileFliesOverhead && Position.Roofed(Map))
                    {
                        vt.Disable("CannotFire".Translate() + ": " + "Roofed".Translate().CapitalizeFirst());
                    }
                    yield return vt;
                }
                // Stop forced attack gizmo
                if (forcedTarget.IsValid)
                {
                    Command_Action stop = new Command_Action();
                    stop.defaultLabel = "CommandStopForceAttack".Translate();
                    stop.defaultDesc = "CommandStopForceAttackDesc".Translate();
                    stop.icon = ContentFinder<Texture2D>.Get("UI/Commands/Halt", true);
                    stop.action = delegate
                    {
                        ResetForcedTarget();
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera(null);
                    };
                    if (!this.forcedTarget.IsValid)
                    {
                        stop.Disable("CommandStopAttackFailNotForceAttacking".Translate());
                    }
                    stop.hotKey = KeyBindingDefOf.Misc5;
                    yield return stop;
                }
                // Toggle fire gizmo
                if (CanToggleHoldFire)
                {
                    yield return new Command_Toggle
                    {
                        defaultLabel = "CommandHoldFire".Translate(),
                        defaultDesc = "CommandHoldFireDesc".Translate(),
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/HoldFire", true),
                        hotKey = KeyBindingDefOf.Misc6,
                        toggleAction = delegate
                        {
                            holdFire = !holdFire;
                            if (holdFire)
                            {
                                ResetForcedTarget();
                            }
                        },
                        isActive = (() => holdFire)
                    };
                }
            }
        }

        // ExtractShell not added

        private void ResetForcedTarget()                // Core method
        {
            this.forcedTarget = LocalTargetInfo.Invalid;
            this.burstWarmupTicksLeft = 0;
            if (this.burstCooldownTicksLeft <= 0)
            {
                this.TryStartShootSomething(false);
            }
        }

        private void ResetCurrentTarget()               // Core method
        {
            this.currentTargetInt = LocalTargetInfo.Invalid;
            this.burstWarmupTicksLeft = 0;
        }

        //MakeGun not added -- MakeGun-like code is run whenever Gun is called
        //UpdateGunVerbs not added

        // New methods
        private void InitGun()
        {
            // Callback for ammo comp
            if (CompAmmo != null)
            {
                CompAmmo.turret = this;
                //if (def.building.turretShellDef != null && def.building.turretShellDef is AmmoDef) CompAmmo.selectedAmmo = (AmmoDef)def.building.turretShellDef;
            }
            List<Verb> allVerbs = this.gunInt.TryGetComp<CompEquippable>().AllVerbs;
            for (int i = 0; i < allVerbs.Count; i++)
            {
                Verb verb = allVerbs[i];
                verb.caster = this;
                verb.castCompleteCallback = new Action(this.BurstComplete);
            }
        }
        
        public void TryOrderReload()
        {
            /*
            if (mannableComp == null)
            {
                if (!CompAmmo.useAmmo) CompAmmo.LoadAmmo();
                return;
            }
            */

            if ((!mannableComp?.MannedNow ?? true) || (CompAmmo.CurrentAmmo == CompAmmo.SelectedAmmo && CompAmmo.CurMagCount == CompAmmo.Props.magazineSize)) return;
            Job reloadJob = null;
            if (CompAmmo.UseAmmo)
            {
                CompInventory inventory = mannableComp.ManningPawn.TryGetComp<CompInventory>();
                if (inventory != null)
                {
                    Thing ammo = inventory.container.FirstOrDefault(x => x.def == CompAmmo.SelectedAmmo);

                    // NPC's switch ammo types
                    if (ammo == null)
                    {
                        ammo = inventory.container.FirstOrDefault(x => CompAmmo.Props.ammoSet.ammoTypes.Any(a => a.ammo == x.def));
                    }
                    if (ammo != null)
                    {
                        if (ammo.def != CompAmmo.SelectedAmmo)
                        {
                            CompAmmo.SelectedAmmo = ammo.def as AmmoDef;
                        }
                        Thing droppedAmmo;
                        int amount = CompAmmo.Props.magazineSize;
                        if (CompAmmo.CurrentAmmo == CompAmmo.SelectedAmmo) amount -= CompAmmo.CurMagCount;
                        if (inventory.container.TryDrop(ammo, this.Position, this.Map, ThingPlaceMode.Direct, Mathf.Min(ammo.stackCount, amount), out droppedAmmo))
                        {
                            reloadJob = new Job(CE_JobDefOf.ReloadTurret, this, droppedAmmo) { count = droppedAmmo.stackCount };
                        }
                    }
                }
            }
            if (reloadJob == null)
            {
                reloadJob = new WorkGiver_ReloadTurret().JobOnThing(mannableComp.ManningPawn, this);
            }
            if (reloadJob != null)
            {
                var pawn = mannableComp.ManningPawn;
                pawn.jobs.StartJob(reloadJob, JobCondition.Ongoing, null, pawn.CurJob?.def != reloadJob.def);
            }
        }
        #endregion
    }
}
