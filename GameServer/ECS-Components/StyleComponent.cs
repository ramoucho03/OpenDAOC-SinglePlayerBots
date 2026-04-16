using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;
using DOL.GS.Styles;

namespace DOL.GS
{
    public class StyleComponent
    {
        public GameLiving Owner { get; }
        public Style NextCombatStyle { get; set; }
        public Style NextCombatBackupStyle { get; set; }
        public long NextCombatStyleTime { get; set; }

        public virtual bool CancelStyle
        {
            get => false;
            set { }
        }

        public virtual bool AwaitingBackupInput
        {
            get => false;
            set { }
        }

        public virtual Style AutomaticBackupStyle
        {
            get => null;
            set { }
        }

        protected StyleComponent(GameLiving owner)
        {
            Owner = owner;
        }

        public static StyleComponent Create(GameLiving owner)
        {
            if (owner is GameNPC npcOwner)
                return new NpcStyleComponent(npcOwner);
            else if (owner is GamePlayer playerOwner)
                return new PlayerStyleComponent(playerOwner);
            else
                return new StyleComponent(owner);
        }

        protected readonly Dictionary<int, Style> _styles = new();
        protected readonly Lock _stylesLock = new();

        public IList GetStyleList()
        {
            List<Style> list = new();

            lock (_stylesLock)
            {
                list = _styles.Values.OrderBy(x => x.SpecLevelRequirement).ThenBy(y => y.ID).ToList();
            }

            return list;
        }

        public void ExecuteWeaponStyle(Style style)
        {
            StyleProcessor.TryToUseStyle(Owner, style);
        }

        public virtual Style GetStyleToUse()
        {
            return null;
        }

        public virtual void DelveWeaponStyle(List<string> delveInfo, Style style)
        {
            return;
        }

        public void RemoveAllStyles()
        {
            lock (_stylesLock)
            {
                _styles.Clear();
            }
        }

        public virtual void AddStyle(Style style, bool notify)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Picks a style, prioritizing reactives and chains over positionals and anytimes
        /// </summary>
        /// <returns>Selected style</returns>
        public Style NPCGetStyleToUse()
        {
            var p = Owner as GameNPC;

            MimicNPC mimic = Owner as MimicNPC;

            if (mimic == null)
            {
                if (p.Styles == null || p.Styles.Count < 1 || p.TargetObject == null)
                    return null;
            }

            AttackData lastAttackData = p.attackComponent.attackAction.LastAttackData;

            if (p.StylesChain != null && p.StylesChain.Count > 0)
                foreach (Style s in p.StylesChain)
                    if (StyleProcessor.CanUseStyle(lastAttackData, p, s, p.ActiveWeapon))
                        return s;

            if (p.StylesDefensive != null && p.StylesDefensive.Count > 0)
                foreach (Style s in p.StylesDefensive)
                    if (StyleProcessor.CanUseStyle(lastAttackData, p, s, p.ActiveWeapon)
                        && p.CheckStyleStun(s)) // Make sure we don't spam stun styles like Brutalize
                        return s;

            if (mimic != null && !mimic.MimicBrain.PvPMode && mimic.MimicBrain.IsMainTank)
            {
                Style s = CheckTaunt(mimic, lastAttackData);

                if (s != null)
                    return s;
            }

            if (p.StylesBack != null && p.StylesBack.Count > 0)
            {
                Style s = p.StylesBack[Util.Random(0, p.StylesBack.Count - 1)];
                if (StyleProcessor.CanUseStyle(lastAttackData, p, s, p.ActiveWeapon))
                    return s;
            }

            if (p.StylesSide != null && p.StylesSide.Count > 0)
            {
                Style s = p.StylesSide[Util.Random(0, p.StylesSide.Count - 1)];
                if (StyleProcessor.CanUseStyle(lastAttackData, p, s, p.ActiveWeapon))
                    return s;
            }

            if (p.StylesFront != null && p.StylesFront.Count > 0)
            {
                Style s = p.StylesFront[Util.Random(0, p.StylesFront.Count - 1)];
                if (StyleProcessor.CanUseStyle(lastAttackData, p, s, p.ActiveWeapon))
                    return s;
            }

            if (p.StylesAnytime != null && p.StylesAnytime.Count > 0)
            {
                Style s = p.StylesAnytime[Util.Random(0, p.StylesAnytime.Count - 1)];
                if (StyleProcessor.CanUseStyle(lastAttackData, p, s, p.ActiveWeapon))
                    return s;
            }

            if (mimic != null && (mimic.MimicBrain.PvPMode || mimic.Group == null))
            {
                Style s = CheckTaunt(mimic, lastAttackData);

                if (s != null)
                    return s;
            }

            return null;
        }

        public Style MimicGetStyleToUse()
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
                return GetStyle(mimic.StylesBack, lastAttackData, mimic);

            if (mimic.StylesSide != null && mimic.StylesSide.Count > 0)
                return GetStyle(mimic.StylesSide, lastAttackData, mimic);

            if (mimic.StylesFront != null && mimic.StylesFront.Count > 0)
                return GetStyle(mimic.StylesFront, lastAttackData, mimic);

            if (mimic.StylesAnytime != null && mimic.StylesAnytime.Count > 0)
                return GetStyle(mimic.StylesAnytime, lastAttackData, mimic);

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
            Style style = styles.First();

            if (styles.Count > 1)
            {
                int startIndex = Util.Random(0, styles.Count - 1);

                for (int i = 0; i < styles.Count; i++)
                {
                    int index = startIndex + i;

                    if (index >= styles.Count)
                        index = i - (styles.Count - startIndex);

                    Style s = styles[index];

                    if (StyleProcessor.CanUseStyle(lastAttackData, mimic, s, mimic.ActiveWeapon))
                    {
                        style = s;
                        break;
                    }
                }
            }

            return style;
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
