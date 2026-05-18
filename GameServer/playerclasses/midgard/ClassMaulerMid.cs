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
	[CharacterClass((int)eCharacterClass.MaulerMid, "Mauler", "Viking")]
	public class ClassMaulerMid : ClassViking
	{
		private static readonly string[] AutotrainableSkills = new[] { Specs.Fist_Wraps, Specs.Mauler_Staff, Specs.Power_Strikes };

		public ClassMaulerMid()
			: base()
		{
			m_profession = "PlayerClass.Profession.TempleofIronFist";
			m_specializationMultiplier = 18;
			m_wsbase = 440;
			m_baseHP = 880;
			m_primaryStat = eStat.STR;
			m_secondaryStat = eStat.CON;
			m_tertiaryStat = eStat.DEX;
			m_manaStat = eStat.STR;
		}

		public override IList<string> GetAutotrainableSkills()
		{
			return AutotrainableSkills;
		}

		public override bool CanUseLefthandedWeapon
		{
			get { return true; }
		}

		public override eClassType ClassType
		{
			get { return eClassType.Hybrid; }
		}

		public override GameTrainer.eChampionTrainerType ChampionTrainerType()
		{
			return GameTrainer.eChampionTrainerType.Viking;
		}

		public override bool HasAdvancedFromBaseClass()
		{
			return true;
		}

		/// <summary>
		/// Grant level-up abilities matching DAoC Live for Mauler:
		///   lvl 1: Fist Wraps mastery, Sprint
		///   lvl 5: Evade I
		///   lvl 10: Stoicism
		/// </summary>
		public override void OnLevelUp(GamePlayer player, int previousLevel)
		{
			base.OnLevelUp(player, previousLevel);

			if (player.Level >= 1)
			{
				player.AddAbility(SkillBase.GetAbility(Abilities.Weapon_FistWraps));
				player.AddAbility(SkillBase.GetAbility(Abilities.Sprint));
			}

			if (player.Level >= 5)
				player.AddAbility(SkillBase.GetAbility(Abilities.Evade, 1));

			if (player.Level >= 10)
				player.AddAbility(SkillBase.GetAbility(Abilities.Stoicism));
		}

		public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
		{
			PlayerRace.Deifrang, PlayerRace.Kobold, PlayerRace.Norseman,
		};
	}
}
