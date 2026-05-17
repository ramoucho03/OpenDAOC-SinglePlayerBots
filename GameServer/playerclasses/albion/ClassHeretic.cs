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
using DOL.GS.Realm;
using System.Collections.Generic;

namespace DOL.GS.PlayerClass
{
	[CharacterClass((int)eCharacterClass.Heretic, "Heretic", "Acolyte")]
	public class ClassHeretic : ClassAcolyte
	{
		private static readonly string[] AutotrainableSkills = new[] { Specs.Rejuvenation, Specs.Enhancement };

		public ClassHeretic()
			: base()
		{
			m_profession = "PlayerClass.Profession.TempleofArawn";
			m_specializationMultiplier = 20;
			m_primaryStat = eStat.PIE;
			m_secondaryStat = eStat.DEX;
			m_tertiaryStat = eStat.CON;
			m_manaStat = eStat.PIE;
			m_wsbase = 360;
			m_baseHP = 720;
		}

		public override IList<string> GetAutotrainableSkills()
		{
			return AutotrainableSkills;
		}

		public override bool HasAdvancedFromBaseClass()
		{
			return true;
		}

		/// <summary>
		/// Grant level-up abilities matching DAoC Live for Heretic:
		///   lvl 1: Flexible Weapon, Sprint
		///   lvl 3: Quickcast
		///   lvl 5: Rejuvenation evade (granted here as Evade I)
		/// </summary>
		public override void OnLevelUp(GamePlayer player, int previousLevel)
		{
			base.OnLevelUp(player, previousLevel);

			if (player.Level >= 1)
			{
				player.AddAbility(SkillBase.GetAbility(Abilities.Weapon_Flexible));
				player.AddAbility(SkillBase.GetAbility(Abilities.Sprint));
			}

			if (player.Level >= 3)
				player.AddAbility(SkillBase.GetAbility(Abilities.Quickcast));

			if (player.Level >= 5)
				player.AddAbility(SkillBase.GetAbility(Abilities.Evade, 1));
		}

		public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
		{
			PlayerRace.Avalonian, PlayerRace.Briton, PlayerRace.Inconnu, PlayerRace.Korazh,
		};
	}
}
