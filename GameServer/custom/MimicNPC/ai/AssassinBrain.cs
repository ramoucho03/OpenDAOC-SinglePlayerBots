using DOL.AI.Brain;
using DOL.Database;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts
{
    public class AssassinBrain : MimicBrain
    {
        private List<int> _envenomSpellIDs;

        // Detection radius used to decide whether it is safe to re-stealth
        // when leaving combat. Mirrors the player UncoverStealthAction radius
        // so the assassin re-cloaks the moment no immediate threat is around,
        // but doesn't spam Stealth() while a mob is still in melee range.
        private const int RESTEALTH_SAFETY_RADIUS = 1024;

        // Cooldown between proactive re-stealth attempts. Avoids per-tick
        // toggling when a pursuer briefly leaves and re-enters the radius.
        private const int RESTEALTH_RECHECK_MS = 1500;

        // Earliest game time at which we may attempt another Stealth(true)
        // after entering or attempting it. Gated to obey the perf guardrail
        // (no per-tick stealth toggling).
        private long _nextRestealthAttemptTime;

        public AssassinBrain()
        {
            _envenomSpellIDs = new List<int>();
        }

        private bool UsesAssassinProfile => MimicBody?.CombatProfile?.HasRole(eMimicCombatRole.Assassin) == true;

        public override void OnLeaderAggro()
        {
            if (UsesAssassinProfile)
                Body.Stealth(true);
        }

        public override void OnEnterAggro()
        {
            // Refresh poison charges at every combat entry. Without this the
            // Defensive cycle (which used to call PoisonWeapons) is never
            // invoked for non-healer assassins in AGGRO state — bots would
            // melee with bare weapons after the first opener.
            if (UsesAssassinProfile)
            {
                PoisonWeapons();

                // Stealth-opener guarantee: when AGGRO is entered from a
                // non-FollowLeader path (solo proximity aggro, group member
                // attacked while we were idle), OnLeaderAggro is *not* the
                // entry hook. Without re-asserting stealth here, the run-in
                // happens unstealthed and the first swing loses the CS
                // opener (Perforate Artery / Backstab line require
                // IsStealthed at swing time per StyleProcessor:118).
                //
                // Stealth() is a no-op once InCombat fires (see MimicNPC.cs
                // overload), so this is safe even if combat already started
                // — we'll simply skip on the first hit.
                if (!Body.InCombat)
                    Body.Stealth(true);
            }

            base.OnEnterAggro();
        }

        public override void OnExitAggro()
        {
            // Disengage via re-stealth: when the fight is over and nothing
            // hostile lingers in melee range, slip back into stealth so the
            // next engagement starts with a fresh CS opener. Gated on a
            // safety radius scan (uses Body.GetPlayersInRadius / GetNPCsInRadius
            // which are zone-bucket lookups, not world enumerations) to
            // honour the "no per-tick polling" guardrail — this fires once
            // per AGGRO exit, not on every Think.
            if (UsesAssassinProfile)
                TryRestealth();

            base.OnExitAggro();
        }

        public override void OnRefreshSpecDependantSkills()
        {
            SortEnvenomSpells();
        }

        public override bool CheckSpells(eCheckSpellType type)
        {
            if (type == eCheckSpellType.Defensive)
            {
                if (UsesAssassinProfile)
                {
                    // Re-stealth policy:
                    //  - Solo, or grouped on a camp where we're not the puller
                    //    -> stay stealthed (classic CS opener setup).
                    //  - PvP roam: stealth whenever we have no current target
                    //    so we don't run around exposed waiting for engagement.
                    //  - Otherwise (active pulling / engaged group movement):
                    //    drop stealth so we don't desync from group pace.
                    bool soloOrCampSupport = Body.Group == null
                        || (Body.Group.MimicGroup.CampPoint != null && !MimicBody.MimicBrain.IsMainPuller);
                    bool pvpIdle = PvPMode && !Body.InCombat && Body.TargetObject == null;

                    if (soloOrCampSupport || pvpIdle)
                        TryRestealth();
                    else
                        Body.Stealth(false);

                    PoisonWeapons();
                }

                return false;
            }

            return base.CheckSpells(type);
        }

        /// <summary>
        /// Safe re-stealth gate. Only stealths when:
        ///   - We are an assassin profile (caller already checks but kept defensive)
        ///   - Not already stealthed
        ///   - Not currently in combat (Stealth() short-circuits but checking
        ///     here avoids the radius scan when it can't succeed anyway)
        ///   - No detectable hostile in <see cref="RESTEALTH_SAFETY_RADIUS"/>
        /// Throttled by <see cref="RESTEALTH_RECHECK_MS"/> so we never poll
        /// stealth state per tick.
        /// </summary>
        private void TryRestealth()
        {
            if (Body.IsStealthed || Body.InCombat)
                return;

            if (GameLoop.GameLoopTime < _nextRestealthAttemptTime)
                return;

            _nextRestealthAttemptTime = GameLoop.GameLoopTime + RESTEALTH_RECHECK_MS;

            // Cheap zone-bucket scan — same primitives used by the player
            // UncoverStealthAction. Bail the moment we find one hostile so we
            // don't pay for the full radius enumeration on busy zones.
            foreach (GamePlayer p in Body.GetPlayersInRadius(RESTEALTH_SAFETY_RADIUS))
            {
                if (p == null || !p.IsAlive)
                    continue;
                if (GameServer.ServerRules.IsAllowedToAttack(p, Body, true))
                    return;
            }

            foreach (GameNPC n in Body.GetNPCsInRadius(RESTEALTH_SAFETY_RADIUS))
            {
                if (n == null || n == Body || !n.IsAlive)
                    continue;
                if (n is MimicNPC)
                    continue;
                // Pet owners count as their owner for hostility rules; the
                // ServerRules check below already handles that path.
                if (GameServer.ServerRules.IsAllowedToAttack(n, Body, true))
                    return;
            }

            Body.Stealth(true);
        }

        protected override bool CheckInstantOffensiveSpells(Spell spell)
        {
            if (UsesAssassinProfile && Body.IsStealthed)
                return false;

            return base.CheckInstantOffensiveSpells(spell);
        }

        private void PoisonWeapons()
        {
            if (_envenomSpellIDs.Count > 0)
            {
                int spellID = 0;

                for (int i = Slot.RIGHTHAND; i <= Slot.LEFTHAND; i++)
                {
                    DbInventoryItem item = Body.Inventory.GetItem((eInventorySlot)i);

                    if (item != null)
                    {
                        if (item.PoisonCharges > 0)
                        {
                            spellID = item.PoisonSpellID;
                            continue;
                        }

                        item.PoisonCharges = 1;
                        item.PoisonMaxCharges = 1;

                        List<int> duplicateList = _envenomSpellIDs.Where(id => id != spellID).ToList();

                        if (duplicateList.Count > 0)
                        {
                            item.PoisonSpellID = duplicateList[Util.Random(duplicateList.Count - 1)];
                            spellID = item.PoisonSpellID;
                        }
                        else
                            item.PoisonSpellID = _envenomSpellIDs[Util.Random(_envenomSpellIDs.Count - 1)];
                    }
                }
            }
        }

        private void SortEnvenomSpells()
        {
            int envenomSpecLevel = Body.GetModifiedSpecLevel(Specs.Envenom);

            if (envenomSpecLevel > 0)
            {
                SpellLine poisonLine = SkillBase.GetSpellLine(GlobalSpellsLines.Mundane_Poisons);

                if (poisonLine != null)
                {
                    _envenomSpellIDs.Clear();

                    List<Spell> highestPoisons = new List<Spell>();
                    List<Spell> poisons = SkillBase.GetSpellList(poisonLine.KeyName);

                    poisons = poisons.OrderByDescending(spell => spell.Level).ToList();

                    foreach (Spell poison in poisons)
                    {
                        if (poison.ID < 30000 || poison.ID > 30049 || poison.Level > envenomSpecLevel)
                            continue;

                        // Keep only the highest-level poison per SpellType (poisons
                        // are pre-sorted by descending level above).
                        if (!highestPoisons.Any(p => p.SpellType == poison.SpellType))
                            highestPoisons.Add(poison);
                    }

                    if (highestPoisons.Count != 0)
                    {
                        foreach (Spell poison in highestPoisons)
                            _envenomSpellIDs.Add(poison.ID);
                    }
                }
            }
        }
    }
}
