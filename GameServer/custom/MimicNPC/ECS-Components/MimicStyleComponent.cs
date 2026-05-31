using DOL.Database;
using DOL.GS.Scripts;
using DOL.GS.Styles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DOL.GS
{
    public class MimicStyleComponent : StyleComponent
    {
        private MimicNPC _mimicOwner;

        public MimicStyleComponent(MimicNPC mimicOwner) : base(mimicOwner)
        {
            _mimicOwner = mimicOwner;
        }

        /// <summary>
        /// Builds the style-priority chain each swing.
        ///
        /// Priority order:
        ///   1. Chain styles (free guaranteed openers from previous hit)
        ///   2. Reactive defensive styles (after parry/evade/block — never wasted)
        ///   3. Tank-specific taunt (PvE only)
        ///   4. Positional openers (back > side > front) — sorted by DamageValue
        ///   5. Anytime / weapon-anytime styles — sorted by DamageValue
        ///   6. Taunt fallback (PvP / solo)
        ///
        /// Stealthers freshly out of stealth deserve their best back/side opener,
        /// so we sort the positional buckets by damage when no <see cref="lastAttackData"/>
        /// exists yet (first swing of the engagement).
        /// </summary>
        public override Style GetStyleToUse()
        {
            MimicNPC mimic = Owner as MimicNPC;

            // Owner-cast guard: StyleProcessor can call this on a teardown
            // path where Owner has been reassigned (component handover, body
            // swap on rez), in which case the cast returns null.
            if (mimic == null || mimic.Styles == null || mimic.Styles.Count < 1 || mimic.TargetObject == null)
                return null;

            // Reject styles queued on a dead/despawned target. Without this
            // guard, a swing that killed the mob would still let the next
            // tick pick a chain/follow-up style that AttackAction then
            // dispatches on the corpse — visible as the tank "swinging at
            // air" for a beat after the kill. The TargetDead path in
            // PrepareMeleeAttack only fires AFTER GetStyleToUse() has
            // already locked in a style, so we need the early bail here.
            if (mimic.TargetObject is GameLiving liveTarget
                && (!liveTarget.IsAlive
                    || liveTarget.ObjectState != GameObject.eObjectState.Active))
                return null;

            // attackComponent.attackAction is null until the first swing
            // initiates the AttackComponent's action. Reading LastAttackData
            // through a null chain NPE'd whenever GetStyleToUse was invoked
            // from a non-combat path (e.g. style preview). The downstream
            // logic already handles lastAttackData == null as "first swing".
            AttackData lastAttackData = mimic.attackComponent?.attackAction?.LastAttackData;
            bool isFirstSwing = lastAttackData == null;

            // 0. Stealth opener — Critical Strike / Backstab / Perforate Artery.
            //    If we're still stealthed at the moment of GetStyleToUse, we
            //    haven't swung yet and must spend this first swing on the
            //    biggest stealth-only style (they snapshot stealth status at
            //    selection time). Walk the back bucket first (CS lives there
            //    for most assassins), then anytime, picking the highest-damage
            //    style flagged as stealth-required.
            if (mimic.IsStealthed && isFirstSwing)
            {
                Style stealthBest = PickBestStealthStyle(mimic.StylesBack, lastAttackData, mimic)
                                  ?? PickBestStealthStyle(mimic.StylesAnytime, lastAttackData, mimic);
                if (stealthBest != null)
                    return stealthBest;
            }

            // 1. Chain styles. Always free, always best.
            if (mimic.StylesChain != null && mimic.StylesChain.Count > 0)
                foreach (Style s in mimic.StylesChain)
                    if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                        return s;

            // 2. Reactive defensives (after parry/evade/block, stun on bonus).
            if (mimic.StylesDefensive != null && mimic.StylesDefensive.Count > 0)
                foreach (Style s in mimic.StylesDefensive)
                    if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon)
                        && mimic.CheckStyleStun(s))
                        return s;

            // 2b. Peel snare (PvP). When acting as a tank and meleeing an enemy
            // that is beating on a fragile teammate, slow it instead of
            // maximising damage so it can't stay on the back-line — the
            // canonical RvR peel ("snare the attacker, move to the next").
            // Returns null (falls through) when there's no peel target, the
            // target is already snared, or no snare style is usable.
            if (mimic.MimicBrain != null)
            {
                Style peelSnare = mimic.MimicBrain.GetPeelSnareStyle();
                if (peelSnare != null)
                    return peelSnare;
            }

            // 3. Tank taunt rotation. Was PvE-only — but in PvP, taunts are
            // exactly the peel tool a real RvR tank uses to flip the player's
            // damage off the healer back onto the tank. Without this, frontier
            // mimic tanks tunneled DPS like generic melee classes and the
            // back-line healer became a free kill. PvP taunts still apply the
            // detaunt-resist mechanic (player can ignore the threat if RA'd
            // correctly), but they DO add real threat to the tank, which is
            // what we need for peel logic to actually take effect.
            // IsActingAsTank covers both the assigned MainTank case and the
            // player-led group case where a mimic tank is the de-facto tank
            // but MainTank still points at the player.
            if (mimic.MimicBrain != null && mimic.MimicBrain.IsActingAsTank)
            {
                Style s = CheckTaunt(mimic, lastAttackData);

                if (s != null)
                    return s;
            }

            // 4. Shield control for assigned tanks. If Slam or another shield
            // style is available, it is usually stronger than a generic weapon
            // swing because it peels or stuns the target.
            if (mimic.MimicBrain != null && mimic.MimicBrain.IsActingAsTank && mimic.StylesShield != null && mimic.StylesShield.Count > 0)
            {
                Style s = GetBestStyle(mimic.StylesShield, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            // 5. Positional openers. Back > Side > Front. On the first swing
            //    (especially fresh out of stealth) we want the highest-damage
            //    style; later swings can use any usable one to keep DPS steady.
            if (mimic.StylesBack != null && mimic.StylesBack.Count > 0)
            {
                Style s = isFirstSwing
                    ? GetBestStyle(mimic.StylesBack, lastAttackData, mimic)
                    : GetStyle(mimic.StylesBack, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            if (mimic.StylesSide != null && mimic.StylesSide.Count > 0)
            {
                Style s = isFirstSwing
                    ? GetBestStyle(mimic.StylesSide, lastAttackData, mimic)
                    : GetStyle(mimic.StylesSide, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            if (mimic.StylesFront != null && mimic.StylesFront.Count > 0)
            {
                Style s = GetStyle(mimic.StylesFront, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            // 6. Anytime styles. Score by DamageValue so the strongest is chosen
            //    when we have the endurance for it, falling back to weaker ones.
            if (mimic.StylesAnytime != null && mimic.StylesAnytime.Count > 0)
            {
                Style s = GetBestStyle(mimic.StylesAnytime, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            // Taunt fallback path — guard MimicBrain like the earlier
            // tank-style branches do (was the last unguarded read in this
            // method).
            if (mimic.MimicBrain == null || mimic.MimicBrain.PvPMode || mimic.Group == null)
            {
                Style s = CheckTaunt(mimic, lastAttackData);

                if (s != null)
                    return s;
            }

            return null;
        }

        /// <summary>Pick a usable style by random scan — cheap, keeps variety.</summary>
        private Style GetStyle(List<Style> styles, AttackData lastAttackData, GameLiving mimic)
        {
            int startIndex = Util.Random(0, styles.Count - 1);

            for (int i = 0; i < styles.Count; i++)
            {
                int index = (startIndex + i) % styles.Count;

                Style s = styles[index];

                if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                    return s;
            }

            return null;
        }

        /// <summary>
        /// Pick the highest-damage usable style. Use for openers and high-priority
        /// anytime swings. DamageValue is the canonical multiplier on Style; higher
        /// = bigger hit.
        /// </summary>
        private Style GetBestStyle(List<Style> styles, AttackData lastAttackData, GameLiving mimic)
        {
            Style best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < styles.Count; i++)
            {
                Style s = styles[i];

                if (!StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                    continue;

                int score = (int)(s.GrowthRate * 100) + (int)s.GrowthOffset;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = s;
                }
            }

            return best;
        }

        /// <summary>
        /// From a candidate list, pick the highest-damage style whose
        /// StealthRequirement is true. Returns null when no stealth-only
        /// style is usable — caller falls back to the normal priority
        /// ladder.
        /// </summary>
        private Style PickBestStealthStyle(List<Style> styles, AttackData lastAttackData, GameLiving mimic)
        {
            if (styles == null || styles.Count == 0)
                return null;

            Style best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < styles.Count; i++)
            {
                Style s = styles[i];

                if (!s.StealthRequirement)
                    continue;
                if (!StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                    continue;

                int score = (int)(s.GrowthRate * 100) + (int)s.GrowthOffset;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = s;
                }
            }

            return best;
        }

        private Style CheckTaunt(MimicNPC mimic, AttackData lastAttackData)
        {
            // ActiveWeapon can be null while the bot is mid-disarm or while
            // an instrument swap is in flight. Bail rather than NPE — taunt
            // styles need a weapon to match the WeaponTypeRequirement
            // anyway.
            DbInventoryItem weapon = mimic.ActiveWeapon;
            if (weapon == null)
                return null;

            if (mimic.StylesTaunt != null && mimic.StylesTaunt.Count > 0)
            {
                foreach (Style s in mimic.StylesTaunt)
                {
                    // WeaponTypeRequirement 0 means "any weapon" — those styles
                    // were silently skipped by the strict equality check.
                    // CanUseStyle does the authoritative weapon validation.
                    if (s.WeaponTypeRequirement == 0 || s.WeaponTypeRequirement == weapon.Object_Type)
                        if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, weapon))
                            return s;
                }
            }

            return null;
        }
    }
}
