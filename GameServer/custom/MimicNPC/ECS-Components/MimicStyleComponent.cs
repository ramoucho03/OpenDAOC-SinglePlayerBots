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

            if (mimic.Styles == null || mimic.Styles.Count < 1 || mimic.TargetObject == null)
                return null;

            AttackData lastAttackData = mimic.attackComponent.attackAction.LastAttackData;
            bool isFirstSwing = lastAttackData == null;

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

            // 3. Tank taunt rotation (PvE only — PvP would waste the style).
            if (!mimic.MimicBrain.PvPMode && mimic.MimicBrain.IsMainTank)
            {
                Style s = CheckTaunt(mimic, lastAttackData);

                if (s != null)
                    return s;
            }

            // 4. Shield control for assigned tanks. If Slam or another shield
            // style is available, it is usually stronger than a generic weapon
            // swing because it peels or stuns the target.
            if (mimic.MimicBrain.IsMainTank && mimic.StylesShield != null && mimic.StylesShield.Count > 0)
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

            if (mimic.MimicBrain.PvPMode || mimic.Group == null)
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

        private Style CheckTaunt(MimicNPC mimic, AttackData lastAttackData)
        {
            if (mimic.StylesTaunt != null && mimic.StylesTaunt.Count > 0)
            {
                foreach (Style s in mimic.StylesTaunt)
                {
                    if (s.WeaponTypeRequirement == mimic.ActiveWeapon.Object_Type)
                        if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                            return s;
                }
            }

            return null;
        }
    }
}
