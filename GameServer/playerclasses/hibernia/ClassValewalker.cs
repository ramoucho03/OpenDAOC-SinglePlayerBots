using System.Collections.Generic;
using DOL.GS.Realm;

namespace DOL.GS.PlayerClass
{
	[CharacterClass((int)eCharacterClass.Valewalker, "Valewalker", "Forester")]
	public class ClassValewalker : ClassForester
	{
		private static readonly string[] AutotrainableSkills = new[] { Specs.Arboreal_Path, Specs.Scythe };

		public ClassValewalker()
			: base()
		{
			m_profession = "PlayerClass.Profession.PathofAffinity";
			m_specializationMultiplier = 20;
			m_primaryStat = eStat.STR;
			m_secondaryStat = eStat.PIE;
			m_tertiaryStat = eStat.CON;
			m_manaStat = eStat.STR;
			m_wsbase = 440;
			m_baseHP = 720;
		}

		public override IList<string> GetAutotrainableSkills()
		{
			return AutotrainableSkills;
		}

		// It's suspected that Valewalker was originally (until early ToA at least) bugged and was treated as a focus caster,
		// because the current power pool formula appears to give them too much power when compared to what happens on videos.
		public override bool IsFocusCaster => false;

		public override bool HasAdvancedFromBaseClass()
		{
			return true;
		}

		/// <summary>
		/// Grant level-up abilities matching DAoC Live for Valewalker:
		///   lvl 1: Sprint, Scythe mastery
		///   Critical Strike is intentionally NOT granted.
		/// </summary>
		public override void OnLevelUp(GamePlayer player, int previousLevel)
		{
			base.OnLevelUp(player, previousLevel);

			if (player.Level >= 1)
			{
				player.AddAbility(SkillBase.GetAbility(Abilities.Sprint));
				player.AddAbility(SkillBase.GetAbility(Abilities.Weapon_Scythe));
			}
		}

		public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
		{
			 PlayerRace.Celt, PlayerRace.Firbolg, PlayerRace.Sylvan,
		};
	}
}
