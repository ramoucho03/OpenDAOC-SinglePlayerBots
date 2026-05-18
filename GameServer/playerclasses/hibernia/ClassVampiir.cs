/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 * 
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */
using System.Collections.Generic;
using DOL.GS.Realm;

namespace DOL.GS.PlayerClass
{
    [CharacterClass((int)eCharacterClass.Vampiir, "Vampiir", "Stalker")]
    public class ClassVampiir : ClassStalker
    {
        private static readonly string[] AutotrainableSkills = new[] { Specs.Piercing, Specs.Blades };

        public ClassVampiir()
            : base()
        {
            m_profession = "PlayerClass.Profession.PathofAffinity";
            m_specializationMultiplier = 15;
            m_primaryStat = eStat.STR;
            m_secondaryStat = eStat.CON;
            m_tertiaryStat = eStat.DEX;
            //Vampiirs do not have a mana stat
            //Special handling is need in the power pool calculator
            //m_manaStat = eStat.STR;
            m_wsbase = 440;
            m_baseHP = 880;
        }

        public override IList<string> GetAutotrainableSkills()
        {
            return AutotrainableSkills;
        }

        public override eClassType ClassType
        {
            get { return eClassType.ListCaster; }
        }

        public override bool HasAdvancedFromBaseClass()
        {
            return true;
        }

        /// <summary>
        /// Grant level-up abilities matching DAoC Live for Vampiir:
        ///   lvl 1: Sprint, Evade I (Vampiirs evade by default)
        ///   Stealth is intentionally NOT granted.
        /// </summary>
        public override void OnLevelUp(GamePlayer player, int previousLevel)
        {
            base.OnLevelUp(player, previousLevel);

            if (player.Level >= 1)
            {
                player.AddAbility(SkillBase.GetAbility(Abilities.Sprint));
                player.AddAbility(SkillBase.GetAbility(Abilities.Evade, 1));
            }
        }

        public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
        {
            PlayerRace.Celt, PlayerRace.Lurikeen, PlayerRace.Shar,
        };
    }
}
