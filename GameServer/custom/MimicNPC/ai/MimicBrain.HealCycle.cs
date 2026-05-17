using DOL.Database;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;
using DOL.GS.Spells;
using System;
using System.Collections.Generic;
using System.Linq;
using static DOL.AI.Brain.MimicBrain;

namespace DOL.AI.Brain
{
    // Heal-cycle controller of MimicBrain, hosting the ~700-line CheckHeals
    // pipeline. Extracted from MimicBrain.cs into a partial file so the main
    // brain stays under control. State (AlreadyCheckedHeals, nextCureTime)
    // and helpers live with the cycle, not the navigation logic.
    public partial class MimicBrain
    {
        /// <summary>Have we already checked heals this loop?</summary>
        public bool AlreadyCheckedHeals;
        private long nextCureTime = 0;

        /// <summary>Check for healing and cure spells</summary>
        /// <returns>True if trying to heal, including moving to get into range</returns>
        public bool CheckHeals()
        {
            /* Summary of priorities — picks the spell that matches the
               *situation* (small / fast / group), not just availability.

                EMERGENCY (someone below EmergencyThreshold):
                  - Multi-emergency : instant group → instant single → group
                                      cast → HealBig (fast) → HealEfficient
                  - Single emergency: instant single → instant group → HealBig
                                      → HealEfficient

                Proactive tank HoT (dedicated healers only): refresh the
                MainTank's HoT/regen while the group is in combat.

                CURES: mezz / disease / poison (shared 5s timer for d/p).

                NON-EMERGENCY (someone below HealThreshold):
                  Multi-target (≥2 wounded):
                    - Instant group HoT (low cooldown, free uptime)
                    - Group HoT if not already running
                    - HealGroup when 3+ are below threshold OR per-mana value
                      beats the single-target efficient heal
                  Single-target:
                    - Instant HoT (no cast cost)
                    - HoT if not already running
                    - HealBig (fast/heavy) when target.HP < HealThreshold AND
                      missing HP ≥ 60% of the big heal value AND mana ≥ 30%
                    - HealEfficient (small/economic) — but skipped on trivial
                      damage (<40% of its value) unless the target is the
                      MainTank or we're in emergency

                Notes:
                  - Dedicated healers will heal members above threshold too
                    and are more likely to fire group heals efficiently.
                  - Spread heals are not considered.
                  - Single-instance-per-tick spell types (instant heal, HoT,
                    regen, cure mezz/disease/poison) are deduped via the
                    MimicGroup AlreadyCasting* flags.
                  - Cure d/p share a 5s timer to avoid spamming and to leave
                    room for secondary healers.
            */

            const long CureDelay = 5000;

            if (AlreadyCheckedHeals || !Body.CanCastHealSpells || Body.IsStunned || Body.IsMezzed || Body.IsSilenced)
                return false;

            AlreadyCheckedHeals = true;

            #region Instant Spell Local Functions

            bool? m_canCastInstantHeal = null;
            bool CanCastInstantHeal() => m_canCastInstantHeal ??= CheckHealSpell(MimicBody.HealInstant);

            bool? m_canCastInstantGroupHeal = null;
            bool CanCastInstantGroupHeal() => m_canCastInstantGroupHeal ??= CheckHealSpell(MimicBody.HealInstantGroup);

            bool? m_canCastInstantHot = null;
            bool CanCastInstantHot() => m_canCastInstantHot ??= CheckHealSpell(MimicBody.HealOverTimeInstant);

            bool? m_canCastInstantGroupHot = null;
            bool CanCastInstantGroupHot() => m_canCastInstantGroupHot ??= CheckHealSpell(MimicBody.HealOverTimeInstantGroup);

            // Instant cure spells are incredibly rare, so it's faster to check if instant before the general spell check
            bool? m_canCastCureDisease = null;
            bool CanCastCureDisease() => m_canCastCureDisease ??= CheckHealSpell(MimicBody.CureDisease) 
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CureDisease.IsInstantCast);
            bool CanCastCureDiseaseInstant() => MimicBody.CureDisease != null && MimicBody.CureDisease.IsInstantCast 
                && CanCastCureDisease();

            bool? m_canCastCureDiseaseGroup = null;
            bool CanCastCureDiseaseGroup() => m_canCastCureDiseaseGroup ??= CheckHealSpell(MimicBody.CureDiseaseGroup)
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CureDiseaseGroup.IsInstantCast);
            bool CanCastCureDiseaseGroupInstant() => MimicBody.CureDiseaseGroup != null && MimicBody.CureDiseaseGroup.IsInstantCast
                && CanCastCureDiseaseGroup();

            bool? m_canCastCurePoison = null;
            bool CanCastCurePoison() => m_canCastCurePoison ??= CheckHealSpell(MimicBody.CurePoison)
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CurePoison.IsInstantCast);
            bool CanCastCurePoisonInstant() => MimicBody.CurePoison != null && MimicBody.CurePoison.IsInstantCast
                && CanCastCurePoison();

            bool? m_canCastCurePoisonGroup = null;
            bool CanCastCurePoisonGroup() => m_canCastCurePoisonGroup ??= CheckHealSpell(MimicBody.CurePoisonGroup)
                && (!MimicBody.IsBeingSelfInterrupted || MimicBody.CurePoisonGroup.IsInstantCast);
            bool CanCastCurePoisonGroupInstant() => MimicBody.CurePoisonGroup != null && MimicBody.CurePoisonGroup.IsInstantCast
                && CanCastCurePoisonGroup();

            bool CanCastInstant() => CanCastInstantHeal() || CanCastInstantGroupHeal() 
                || CanCastInstantHot() || CanCastInstantGroupHot()
                || CanCastCureDiseaseInstant() || CanCastCureDiseaseGroupInstant()
                || CanCastCurePoisonInstant() || CanCastCurePoisonGroupInstant();

            #endregion

            if (MimicBody.IsBeingSelfInterrupted && !CanCastInstant())
                return false;

            bool isCastingHeal = MimicBody.IsCasting && MimicBody.castingComponent.SpellHandler.Spell.IsHealing;

            if (isCastingHeal && !CanCastInstant())
                return true;

            // Working variables
            int amountToHeal;
            int numEmergency = 0;
            int numNeedHealing = 0;
            Spell spellToCast = null;
            GameLiving spellTarget = null;
            GameObject oldTarget;
            bool startedCasting = false;

            #region Local Functions

            bool? m_canCastGroupHeal = null;
            bool CanCastGroupHeal() => m_canCastGroupHeal ??= CheckHealSpell(MimicBody.HealGroup);

            bool? m_canCastBigHeal = null;
            bool CanCastBigHeal() => m_canCastBigHeal ??= CheckHealSpell(MimicBody.HealBig);

            bool? m_canCastEfficientHeal = null;
            bool CanCastEfficientHeal() => m_canCastEfficientHeal ??= CheckHealSpell(MimicBody.HealEfficient);

            bool? m_canCastHot = null;
            bool CanCastHot() => m_canCastHot ??= CheckHealSpell(MimicBody.HealOverTime);

            bool? m_canCastHotGroup = null;
            bool CanCastHotGroup() => m_canCastHotGroup ??= CheckHealSpell(MimicBody.HealOverTimeGroup);

            bool CheckHealSpell(Spell spell, bool checkGroup = true)
            {
                return spell != null
                    && (!MimicBody.IsBeingSelfInterrupted || spell.IsInstantCast)
                    && (!spell.HasRecastDelay || MimicBody.GetSkillDisabledDuration(spell) <= 0)
                    && MimicBody.Mana >= MimicBody.PowerCost(spell);
            }

            double m_groupHealVal = double.MinValue;
            double GetGroupHealVal()
            {
                if (m_groupHealVal < 0)
                {
                    m_groupHealVal = MimicBody.HealGroup.Value >= 0
                        ? numNeedHealing * MimicBody.HealGroup.Value
                        : amountToHeal * MimicBody.HealGroup.Value * -0.01d;
                }
                return m_groupHealVal;
            }

            double m_effectHoT = double.MinValue;
            double m_effectRegen = double.MinValue;
            double GetHotEffect(Spell spell)
            {
                switch (spell.SpellType)
                {
                    case eSpellType.HealOverTime:
                        if (m_effectHoT < 0d)
                        {
                            List<ECSGameEffect> effects = spellTarget.effectListComponent.GetEffects(eEffect.HealOverTime);

                            if (effects != null)
                            {
                                foreach (ECSGameEffect effect in effects)
                                    if (effect is ECSGameSpellEffect)
                                    {
                                        double newHoT = MimicNPC.HealAmount(effect.SpellHandler.Spell, spellTarget);
                                        if (newHoT > m_effectHoT)
                                            m_effectHoT = newHoT;
                                    }
                            }
                            else
                                m_effectHoT = 0d;
                        }
                        return m_effectHoT;
                    case eSpellType.HealthRegenBuff:
                        if (m_effectRegen < 0d)
                        {
                            List<ECSGameEffect> effects = spellTarget.effectListComponent.GetEffects(eEffect.HealthRegenBuff);

                            if (effects != null)
                            {
                                foreach (ECSGameEffect effect in effects)
                                    if (effect is ECSGameSpellEffect)
                                    {
                                        double newRegen = MimicNPC.HealAmount(effect.SpellHandler.Spell, spellTarget);
                                        if (newRegen > m_effectRegen)
                                            m_effectRegen = newRegen;
                                    }
                            }
                            else
                                m_effectRegen = 0d;
                        }
                        return m_effectRegen;
                }

                return 0d;
            }

            #endregion

            MimicGroup mGroup = MimicBody.Group?.MimicGroup;

            lock (mGroup?.HealLock ?? new object())
            {
                #region Check Health

                if (mGroup == null)
                {
                    amountToHeal = MimicBody.MaxHealth - MimicBody.Health;

                    if (amountToHeal > 0)
                    {
                        spellTarget = MimicBody;

                        if (MimicBody.HealthPercent < MimicGroup.HealThreshold)
                        {
                            numNeedHealing = 1;

                            if (MimicBody.HealthPercent < MimicGroup.EmergencyThreshold)
                                numEmergency = 1;
                        }
                    }
                }
                else
                {
                    mGroup.CheckGroupHealth(MimicBody);

                    amountToHeal = mGroup.AmountToHeal;
                    numEmergency = mGroup.NumNeedEmergencyHealing;
                    numNeedHealing = IsHealer 
                        ? mGroup.NumInjured 
                        : mGroup.NumNeedHealing;
                    spellTarget = mGroup.MemberToHeal;

                    if (mGroup.AlreadyCastInstantHeal)
                        m_canCastInstantHeal = m_canCastInstantGroupHeal = false;

                    if (mGroup.AlreadyCastingHoT)
                    {
                        if (MimicBody.HealOverTimeInstant == null || MimicBody.HealOverTimeInstant.SpellType == eSpellType.HealOverTime)
                            m_canCastInstantHot = false;
                        if (MimicBody.HealOverTimeInstantGroup == null || MimicBody.HealOverTimeInstantGroup.SpellType == eSpellType.HealOverTime)
                            m_canCastInstantGroupHot = false;
                        if (MimicBody.HealOverTime == null || MimicBody.HealOverTime.SpellType == eSpellType.HealOverTime)
                            m_canCastHot = false;
                        if (MimicBody.HealOverTimeGroup == null || MimicBody.HealOverTimeGroup.SpellType == eSpellType.HealOverTime)
                            m_canCastHotGroup = false;
                    }

                    if (mGroup.AlreadyCastingRegen)
                    {
                        if (MimicBody.HealOverTimeInstant == null || MimicBody.HealOverTimeInstant.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastInstantHot = false;
                        if (MimicBody.HealOverTimeInstantGroup == null || MimicBody.HealOverTimeInstantGroup.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastInstantGroupHot = false;
                        if (MimicBody.HealOverTime == null || MimicBody.HealOverTime.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastHot = false;
                        if (MimicBody.HealOverTimeGroup == null || MimicBody.HealOverTimeGroup.SpellType == eSpellType.HealthRegenBuff)
                            m_canCastHotGroup = false;
                    }

                    if (mGroup.AlreadyCastingCureDisease)
                        m_canCastCureDisease = m_canCastCureDiseaseGroup = false;

                    if (mGroup.AlreadyCastingCurePoison)
                        m_canCastCurePoison = m_canCastCurePoisonGroup = false;

                    // Multi-healer coordination: if another healer already has
                    // a cast-time single heal landing on the selected target,
                    // move this healer to another wounded member. Emergency
                    // targets stay exempt; double-casting there is often correct.
                    if (spellTarget != null
                        && numEmergency == 0
                        && mGroup.AlreadyCastingSingleHeal
                        && mGroup.IsSingleHealReserved(spellTarget))
                    {
                        GameLiving alternate = mGroup.PickAlternateHealTarget(
                            MimicBody,
                            spellTarget,
                            IsHealer,
                            avoidSingleHealReservation: true);

                        if (alternate != null)
                            spellTarget = alternate;
                        else
                        {
                            numNeedHealing = 0;
                            amountToHeal = 0;
                        }
                    }
                }

                #endregion
 
                #region Emergency Heal

                if (numEmergency > 0)
                {
                    if (numEmergency > 1)
                    {
                        if (CanCastInstantGroupHeal())
                            spellToCast = MimicBody.HealInstantGroup;
                        else if (CanCastInstantHeal())
                            spellToCast = MimicBody.HealInstant;
                        else if (!isCastingHeal && CanCastGroupHeal())
                        {
                            if (MimicNPC.HealAmount(MimicBody.HealBig, spellTarget) > GetGroupHealVal() && CanCastBigHeal())
                                spellToCast = MimicBody.HealBig;
                            else if (MimicNPC.HealAmount(MimicBody.HealEfficient, spellTarget) > GetGroupHealVal() && CanCastEfficientHeal())
                                spellToCast = MimicBody.HealEfficient;
                            else
                                spellToCast = MimicBody.HealGroup;
                        }
                    }

                    if (spellToCast == null)
                    {
                        if (CanCastInstantHeal())
                            spellToCast = MimicBody.HealInstant;
                        else if (CanCastInstantGroupHeal())
                            spellToCast = MimicBody.HealInstantGroup;
                        else if (!isCastingHeal)
                        {
                            if (CanCastBigHeal())
                                spellToCast = MimicBody.HealBig;
                            else if (CanCastEfficientHeal())
                                spellToCast = MimicBody.HealEfficient;
                        }
                    }
                }

                #endregion

                #region Proactive Tank HoT
                // Keep the MainTank topped with a HoT/regen whenever an
                // encounter is starting or already underway, even if the tank
                // is at full HP. A real healer pre-stacks the HoT before the
                // pull lands so the first incoming hits are buffered. We treat
                // the following as "encounter in progress":
                //  - tank actually in combat (legacy condition)
                //  - the group is in the Pulling or Engaging camp phase
                //  - the group has an IncomingPullTarget set (puller is staging)
                // The MimicGroup tracker (AlreadyCastingHoT) still prevents two
                // healers from spamming the same HoT every tick, and CheckHealSpell
                // handles the recast delay.
                bool encounterImminent = mGroup != null
                    && (mGroup.MainTank?.InCombat == true
                        || mGroup.IncomingPullTarget != null
                        || mGroup.CampPhase == MimicGroup.eCampPhase.Pulling
                        || mGroup.CampPhase == MimicGroup.eCampPhase.Engaging
                        || mGroup.CampPhase == MimicGroup.eCampPhase.Combat);
                if (spellToCast == null
                    && IsHealer
                    && mGroup != null
                    && mGroup.MainTank != null
                    && mGroup.MainTank.IsAlive
                    && encounterImminent
                    && !mGroup.AlreadyCastingHoT)
                {
                    GameLiving tank = mGroup.MainTank;

                    // Only refresh when the HoT effect isn't already running on the tank.
                    bool tankHasHoT = tank.effectListComponent.ContainsEffectForEffectType(eEffect.HealOverTime);

                    if (!tankHasHoT)
                    {
                        if (CanCastInstantHot())
                        {
                            spellToCast = MimicBody.HealOverTimeInstant;
                            spellTarget = tank;
                        }
                        else if (!MimicBody.IsCasting && CanCastHot())
                        {
                            spellToCast = MimicBody.HealOverTime;
                            spellTarget = tank;
                        }
                    }
                }
                #endregion

                #region Cure Mess/Disease/Poison

                if (spellToCast == null)
                {
                    if (mGroup != null && mGroup.MemberToCureMezz != null && !mGroup.AlreadyCastingCureMezz
                        && !MimicBody.IsCasting && CheckHealSpell(MimicBody.CureMezz))
                    {
                        spellToCast = MimicBody.CureMezz;
                        spellTarget = mGroup.MemberToCureMezz;
                    }
                    else if (mGroup == null)
                    {
                        if (MimicBody.IsDiseased && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCureDisease() && (!MimicBody.IsCasting || CanCastCureDiseaseInstant()))
                            {
                                spellToCast = MimicBody.CureDisease;
                                spellTarget = MimicBody;
                            }
                            else if (CanCastCureDiseaseGroup() && (!MimicBody.IsCasting) || CanCastCureDiseaseGroupInstant())
                            {
                                spellToCast = MimicBody.CureDiseaseGroup;
                                spellTarget = MimicBody;
                            }
                        }
                        else if (MimicBody.IsPoisoned && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCurePoison() && (!MimicBody.IsCasting || CanCastCurePoisonInstant()))
                            {
                                spellToCast = MimicBody.CurePoison;
                                spellTarget = MimicBody;
                            }
                            else if (CanCastCurePoisonGroup() && (!MimicBody.IsCasting || CanCastCurePoisonGroupInstant()))
                            {
                                spellToCast = MimicBody.CurePoisonGroup;
                                spellTarget = MimicBody;
                            }
                        }
                    }
                    else
                    {
                        if (mGroup.MemberToCureDisease != null && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCureDiseaseGroup()
                                && (mGroup.NumNeedCureDisease > 1 || !CanCastCureDisease())
                                && (!MimicBody.IsCasting || CanCastCureDiseaseGroupInstant()))
                            {
                                spellToCast = MimicBody.CureDiseaseGroup;
                                spellTarget = mGroup.MemberToCureDisease;
                            }
                            else if (CanCastCureDisease()
                                && (!MimicBody.IsCasting || CanCastCureDiseaseInstant()))
                            {
                                spellToCast = MimicBody.CureDisease;
                                spellTarget = mGroup.MemberToCureDisease;
                            }
                        }
                        else if (mGroup.MemberToCurePoison != null && nextCureTime < GameLoop.GameLoopTime)
                        {
                            if (CanCastCurePoisonGroup()
                                && (mGroup.NumNeedCurePoison > 1 || !CanCastCurePoison())
                                && (!MimicBody.IsCasting || CanCastCurePoisonGroupInstant()))
                            {
                                spellToCast = MimicBody.CurePoisonGroup;
                                spellTarget = mGroup.MemberToCurePoison;
                            }
                            else if (CanCastCurePoison()
                                && (!MimicBody.IsCasting || CanCastCurePoisonInstant()))
                            {
                                spellToCast = MimicBody.CurePoison;
                                spellTarget = mGroup.MemberToCurePoison;
                            }
                        }
                    }
                }

                #endregion
 
                #region Non-Emergency Heal

                if (spellToCast == null && numNeedHealing > 0)
                {
                    // -------- Multi-target: prefer GROUP heal/HoT --------
                    // Group heals are situational: they win when several
                    // members are actually below the heal threshold, OR when
                    // their mana-efficiency vs the single-target option is
                    // genuinely better (the historical check). The 3-wounded
                    // floor avoids AoE-spamming when only one or two members
                    // are tagged — a single wounded body wastes most of the
                    // group heal's healing on already-full members.
                    if (numNeedHealing > 1)
                    {
                        // Instant HoTs usually have low cooldowns, so spam them whenever possible
                        if (CanCastInstantGroupHot()
                            && MimicNPC.HealAmount(MimicBody.HealOverTimeInstantGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeInstantGroup))
                                spellToCast = MimicBody.HealOverTimeInstantGroup;
                        else if (!MimicBody.IsCasting || (numEmergency > 0 && !isCastingHeal))
                        {
                            if (CanCastHotGroup()
                                && MimicNPC.HealAmount(MimicBody.HealOverTimeGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeGroup))
                                    spellToCast = MimicBody.HealOverTimeGroup;
                            else if (CanCastGroupHeal())
                            {
                                // Two conditions accept the AoE heal:
                                //   - 3+ are below heal threshold (broad spread of damage), or
                                //   - the per-mana value still beats single-target efficient
                                //     (historical heuristic, kept for sustained heal economy).
                                bool manyWounded = numNeedHealing >= 3;
                                bool moreEfficientThanSingle = !CanCastEfficientHeal()
                                    || (GetGroupHealVal() / MimicBody.PowerCost(MimicBody.HealGroup))
                                       > (MimicNPC.HealAmount(MimicBody.HealEfficient, spellTarget) / MimicBody.PowerCost(MimicBody.HealEfficient));

                                if (manyWounded || moreEfficientThanSingle)
                                    spellToCast = MimicBody.HealGroup;
                            }
                        }
                    }

                    // -------- Single-target: HoT → BIG vs SMALL choice --------
                    if (spellToCast == null)
                    {
                        if (CanCastInstantHot()
                            && MimicNPC.HealAmount(MimicBody.HealOverTimeInstant, spellTarget) > GetHotEffect(MimicBody.HealOverTimeInstant))
                                spellToCast = MimicBody.HealOverTimeInstant;
                        else if (CanCastInstantGroupHot()
                            && MimicNPC.HealAmount(MimicBody.HealOverTimeInstantGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeInstantGroup))
                                spellToCast = MimicBody.HealOverTimeInstantGroup;
                        else if (!MimicBody.IsCasting || (numEmergency > 0 && !isCastingHeal))
                        {
                            if (CanCastHot()
                                && MimicNPC.HealAmount(MimicBody.HealOverTime, spellTarget) > GetHotEffect(MimicBody.HealOverTime))
                                    spellToCast = MimicBody.HealOverTime;
                            else if (CanCastHotGroup()
                                && MimicNPC.HealAmount(MimicBody.HealOverTimeGroup, spellTarget) > GetHotEffect(MimicBody.HealOverTimeGroup))
                                    spellToCast = MimicBody.HealOverTimeGroup;
                            else
                            {
                                // Pick the cast-time heal whose magnitude best
                                // matches the target's missing HP. The previous
                                // logic required mana ≥ 90% to even consider
                                // HealBig, so a tank that lost 60% of HP would
                                // get patched up with the small HealEfficient
                                // forever. We now decide by *damage taken*, not
                                // mana headroom, and protect against overheal
                                // on barely-scratched targets.
                                int missing = spellTarget.MaxHealth - spellTarget.Health;
                                double bigAmount = MimicBody.HealBig != null
                                    ? MimicNPC.HealAmount(MimicBody.HealBig, spellTarget)
                                    : 0d;
                                double effAmount = MimicBody.HealEfficient != null
                                    ? MimicNPC.HealAmount(MimicBody.HealEfficient, spellTarget)
                                    : 0d;
                                bool targetIsTank = mGroup != null && spellTarget == mGroup.MainTank;

                                // BIG (fast/heavy) heal — target is significantly
                                // hurt AND ≥60% of the big heal's value will land
                                // without overheal. The 30% mana floor keeps the
                                // healer from blowing the bar on a single cast.
                                bool canUseBigHeal = CanCastBigHeal()
                                    && bigAmount > 0
                                    && missing >= bigAmount * 0.6d
                                    && spellTarget.HealthPercent < MimicGroup.HealThreshold
                                    && MimicBody.ManaPercent >= 30;

                                if (canUseBigHeal)
                                    spellToCast = MimicBody.HealBig;
                                else if (CanCastEfficientHeal())
                                {
                                    // SMALL/efficient heal — but skip on trivial
                                    // scratches (< 40% of the efficient heal value)
                                    // to avoid wasted mana. The MainTank always
                                    // gets topped regardless: keeping aggro on a
                                    // full-HP tank is worth a small overheal.
                                    bool worthCasting = effAmount <= 0d
                                        || missing >= effAmount * 0.4d
                                        || numEmergency > 0
                                        || targetIsTank;

                                    if (worthCasting)
                                        spellToCast = MimicBody.HealEfficient;
                                }
                                else if (CanCastGroupHeal())
                                    // We don't have a single target heal, but we might have a CL group heal
                                    spellToCast = MimicBody.HealGroup;
                            }
                        }
                    }
                }

                #endregion
 
                #region Cast Spell

                if (spellToCast != null)
                {
                    if (!MimicBody.IsWithinRadius(spellTarget, spellToCast.CalculateEffectiveRange(spellTarget)))
                    {
                        MimicBody.PathTo(new Point3D(spellTarget.X, spellTarget.Y, spellTarget.Z), MimicBody.MaxSpeed);
                        return true;
                    }

                    if (!spellToCast.IsInstantCast)
                    {
                        if (MimicBody.IsCasting)
                            MimicBody.StopCurrentSpellcast();
                        else if (MimicBody.IsAttacking)
                            MimicBody.StopAttack();
                    }

                    oldTarget = MimicBody.TargetObject;
                    MimicBody.TargetObject = spellTarget;
                    startedCasting = MimicBody.CastSpell(spellToCast, MimicBody.GetSpellLineForSpell(spellToCast), false);

                    if (!startedCasting)
                        MimicBody.TargetObject = oldTarget;
                    else
                    {
                        if (spellToCast.IsInstantCast)
                        {
                            MimicBody.TargetObject = oldTarget;
                            startedCasting = false;
                        }
                        else if (spellToCast.SpellType == eSpellType.CureDisease || spellToCast.SpellType == eSpellType.CurePoison)
                            nextCureTime = GameLoop.GameLoopTime + CureDelay;

                        if (mGroup != null)
                            switch (spellToCast.SpellType)
                            {
                                case eSpellType.Heal:
                                case eSpellType.CombatHeal:
                                case eSpellType.MercHeal:
                                case eSpellType.OmniHeal:
                                    if (spellToCast.IsInstantCast)
                                        mGroup.AlreadyCastInstantHeal = true;
                                    else
                                        mGroup.MarkSingleHealInProgress(spellTarget);
                                    break;
                                case eSpellType.HealOverTime: mGroup.AlreadyCastingHoT = true; break;
                                case eSpellType.HealthRegenBuff: mGroup.AlreadyCastingRegen = true; break;
                                case eSpellType.CureMezz: mGroup.AlreadyCastingCureMezz = true; break;
                                case eSpellType.CureDisease: mGroup.AlreadyCastingCureDisease = true; break;
                                case eSpellType.CurePoison: mGroup.AlreadyCastingCurePoison = true; break;
                            }
                    }
                }
            } // lock

            #endregion

            return startedCasting || isCastingHeal;
        }

        bool CheckDefensiveSpells(List<Spell> spells)
        {
            // Contrary to offensive spells, we don't start with a valid target.
            // So the idea here is to find a target, switch before calling `CastDefensiveSpell`, then retrieve our previous target.
            List<(Spell, GameLiving)> spellsToCast = new(spells.Count);

            foreach (Spell spell in spells)
            {
                if (CanCastDefensiveSpell(spell, out GameLiving target))
                    spellsToCast.Add((spell, target));
            }

            if (spellsToCast.Count == 0)
                return false;

            GameObject oldTarget = Body.TargetObject;
            (Spell spell, GameLiving target) spellToCast = spellsToCast[0];
            Body.TargetObject = spellToCast.target;
            bool cast = Body.CastSpell(spellToCast.spell, MimicBody.GetSpellLineForSpell(spellToCast.spell));

            if (Debug)
            {
                if (cast)
                    log.Info(Body.Name + " tried to cast " + spellToCast.spell.Name + " on " + spellToCast.target.Name + " and cast == true");
                else
                    log.Info(Body.Name + " tried to cast " + spellToCast.spell.Name + " on " + spellToCast.target.Name + " and cast == false");

                if (LivingHasEffect(spellToCast.target, spellToCast.spell))
                    log.Info(spellToCast.target.Name + " has the effect already.");
            }

            Body.TargetObject = oldTarget;
            return cast;

            bool CanCastDefensiveSpell(Spell spell, out GameLiving target)
            {
                target = null;

                // TODO: Handle instrument spells
                if (spell.NeedInstrument || (!spell.Uninterruptible && Body.IsBeingInterrupted) ||
                    (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0))
                {
                    return false;
                }

                target = FindTargetForDefensiveSpell(spell);
                return target != null;
            }
        }
    }
}
