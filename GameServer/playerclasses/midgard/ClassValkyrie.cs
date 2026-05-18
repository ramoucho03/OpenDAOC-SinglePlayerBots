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
	[CharacterClass((int)eCharacterClass.Valkyrie, "Valkyrie", "Viking")]
	public class ClassValkyrie : ClassViking
	{
		private static readonly string[] AutotrainableSkills = new[] { Specs.Mending, Specs.OdinsWill };

		public ClassValkyrie()
			: base()
		{
			m_profession = "PlayerClass.Profession.HouseofOdin";
			m_specializationMultiplier = 22;
			m_primaryStat = eStat.STR;
			m_secondaryStat = eStat.CON;
			m_tertiaryStat = eStat.DEX;
			m_manaStat = eStat.STR;
			m_wsbase = 440;
			m_baseHP = 720;
		}

		public override IList<string> GetAutotrainableSkills()
		{
			return AutotrainableSkills;
		}

		public override eClassType ClassType
		{
			get { return eClassType.Hybrid; }
		}

		public override bool HasAdvancedFromBaseClass()
		{
			return true;
		}

		/// <summary>
		/// Grant level-up abilities matching DAoC Live for Valkyrie:
		///   lvl 1: Sword, Spear, Shield mastery, Sprint
		///   lvl 5: Evade I (Determination is a Realm Ability, not granted here)
		/// </summary>
		public override void OnLevelUp(GamePlayer player, int previousLevel)
		{
			base.OnLevelUp(player, previousLevel);

			if (player.Level >= 1)
			{
				player.AddAbility(SkillBase.GetAbility(Abilities.Weapon_Swords));
				player.AddAbility(SkillBase.GetAbility(Abilities.Weapon_Spears));
				player.AddAbility(SkillBase.GetAbility(Abilities.Shield, 2));
				player.AddAbility(SkillBase.GetAbility(Abilities.Sprint));
			}

			if (player.Level >= 5)
				player.AddAbility(SkillBase.GetAbility(Abilities.Evade, 1));
		}

		public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
		{
			PlayerRace.Dwarf, PlayerRace.Frostalf, PlayerRace.Norseman, PlayerRace.Valkyn,
		};
	}
}
