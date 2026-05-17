using System.Collections.Generic;
using DOL.GS.Realm;

namespace DOL.GS.PlayerClass
{
	[CharacterClass((int)eCharacterClass.Warlock, "Warlock", "Mystic")]
	public class ClassWarlock : ClassMystic
	{
		private static readonly string[] AutotrainableSkills = new[] { Specs.Hexing, Specs.Witchcraft };

		public ClassWarlock()
			: base()
		{
			m_profession = "PlayerClass.Profession.HouseofHel";
			m_specializationMultiplier = 20;
			m_primaryStat = eStat.PIE;
			m_secondaryStat = eStat.CON;
			m_tertiaryStat = eStat.DEX;
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
		/// Grant level-up abilities matching DAoC Live for Warlock:
		///   lvl 1: Staff, Sprint
		///   lvl 3: Quickcast
		/// </summary>
		public override void OnLevelUp(GamePlayer player, int previousLevel)
		{
			base.OnLevelUp(player, previousLevel);

			if (player.Level >= 1)
			{
				player.AddAbility(SkillBase.GetAbility(Abilities.Weapon_Staves));
				player.AddAbility(SkillBase.GetAbility(Abilities.Sprint));
			}

			if (player.Level >= 3)
				player.AddAbility(SkillBase.GetAbility(Abilities.Quickcast));
		}

		/// <summary>
		/// FIXME this has nothing to do here !
		/// </summary>
		/// <param name="line"></param>
		/// <param name="spell"></param>
		/// <returns></returns>
		public override bool CanChangeCastingSpeed(SpellLine line, Spell spell)
		{
			if (spell.SpellType == eSpellType.Chamber)
				return false;

			if ((line.KeyName == "Cursing"
				 || line.KeyName == "Cursing Spec"
				 || line.KeyName == "Hexing"
				 || line.KeyName == "Witchcraft")
				&& (spell.SpellType is not eSpellType.BaseArmorFactorBuff
					and not eSpellType.Bladeturn
					and not eSpellType.ArmorAbsorptionBuff
					and not eSpellType.MatterResistDebuff
					and not eSpellType.Uninterruptable
					and not eSpellType.Powerless
					and not eSpellType.Range
					&& spell.Name != "Lesser Twisting Curse"
					&& spell.Name != "Twisting Curse"
					&& spell.Name != "Lesser Winding Curse"
					&& spell.Name != "Winding Curse"
					&& spell.Name != "Lesser Wrenching Curse"
					&& spell.Name != "Wrenching Curse"
					&& spell.Name != "Lesser Warping Curse"
					&& spell.Name != "Warping Curse"))
			{
				return false;
			}

			return true;
		}

		public override List<PlayerRace> EligibleRaces => new List<PlayerRace>()
		{
			PlayerRace.Frostalf, PlayerRace.Kobold, PlayerRace.Norseman, PlayerRace.Valkyn,
		};
	}
}
