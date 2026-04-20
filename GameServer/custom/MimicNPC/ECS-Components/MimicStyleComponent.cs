using DOL.GS.Scripts;
using DOL.GS.Styles;
using System;
using System.Collections.Generic;
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

        public override Style GetStyleToUse()
        {
            MimicNPC mimic = Owner as MimicNPC;

            if (mimic.Styles == null || mimic.Styles.Count < 1 || mimic.TargetObject == null)
                return null;

            AttackData lastAttackData = mimic.attackComponent.attackAction.LastAttackData;

            if (mimic.StylesChain != null && mimic.StylesChain.Count > 0)
                foreach (Style s in mimic.StylesChain)
                    if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                        return s;

            if (mimic.StylesDefensive != null && mimic.StylesDefensive.Count > 0)
                foreach (Style s in mimic.StylesDefensive)
                    if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon)
                        && mimic.CheckStyleStun(s)) // Make sure we don't spam stun styles like Brutalize
                        return s;

            if (!mimic.MimicBrain.PvPMode && mimic.MimicBrain.IsMainTank)
            {
                Style s = CheckTaunt(mimic, lastAttackData);

                if (s != null)
                    return s;
            }

            if (mimic.StylesBack != null && mimic.StylesBack.Count > 0)
            {
                Style s = GetStyle(mimic.StylesBack, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            if (mimic.StylesSide != null && mimic.StylesSide.Count > 0)
            {
                Style s = GetStyle(mimic.StylesSide, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            if (mimic.StylesFront != null && mimic.StylesFront.Count > 0)
            {
                Style s = GetStyle(mimic.StylesFront, lastAttackData, mimic);

                if (s != null)
                    return s;
            }

            if (mimic.StylesAnytime != null && mimic.StylesAnytime.Count > 0)
            {
                Style s = GetStyle(mimic.StylesAnytime, lastAttackData, mimic);

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
