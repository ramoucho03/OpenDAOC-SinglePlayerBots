using DOL.GS.Scripts;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    /// <summary>
    /// Regression coverage for MimicConfig.MatchesCsv and the IsXxxClass
    /// helpers that drive Bot AI v2 role-strategy enrolment at bot spawn.
    /// Those CSVs are read on every /mcreate, and a silent miss (typo,
    /// wrong casing, empty value) used to leave a bot without the role
    /// strategy the operator expected — which is exactly the failure mode
    /// commit bd02702 fixed by sourcing the defaults from validated 1.65
    /// references, and commit afdc560 made visible by adding a one-time
    /// warning. These tests pin both behaviours.
    /// </summary>
    [TestFixture]
    public class UT_MimicConfig
    {
        // Snapshot the CSV values before each test and restore after, so
        // a test that overrides a list doesn't bleed into another test
        // and so the suite leaves the static state untouched at exit.
        private string _savedHealer;
        private string _savedTank;
        private string _savedMeleeDps;

        [SetUp]
        public void SetUp()
        {
            _savedHealer = MimicConfig.BOT_AI_V2_HEALER_CLASSES;
            _savedTank = MimicConfig.BOT_AI_V2_TANK_CLASSES;
            _savedMeleeDps = MimicConfig.BOT_AI_V2_MELEE_DPS_CLASSES;
        }

        [TearDown]
        public void TearDown()
        {
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = _savedHealer;
            MimicConfig.BOT_AI_V2_TANK_CLASSES = _savedTank;
            MimicConfig.BOT_AI_V2_MELEE_DPS_CLASSES = _savedMeleeDps;
        }

        [Test]
        public void IsHealerClass_KnownHealer_ReturnsTrue()
        {
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "Cleric,Friar,Heretic,Druid,Bard,Warden,Mentalist,Healer,Shaman";

            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Druid), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Healer), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Warden), Is.True);
        }

        [Test]
        public void IsHealerClass_NonHealer_ReturnsFalse()
        {
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "Cleric,Friar,Heretic,Druid,Bard,Warden,Mentalist,Healer,Shaman";

            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Armsman), Is.False);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Scout), Is.False);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Wizard), Is.False);
        }

        [Test]
        public void IsTankClass_KnownTank_ReturnsTrue()
        {
            MimicConfig.BOT_AI_V2_TANK_CLASSES = "Armsman,Paladin,Reaver,Hero,Warden,Champion,Warrior,Thane";

            Assert.That(MimicConfig.IsTankClass((int)eCharacterClass.Armsman), Is.True);
            Assert.That(MimicConfig.IsTankClass((int)eCharacterClass.Paladin), Is.True);
            Assert.That(MimicConfig.IsTankClass((int)eCharacterClass.Hero), Is.True);
            Assert.That(MimicConfig.IsTankClass((int)eCharacterClass.Warrior), Is.True);
        }

        [Test]
        public void IsTankClass_PaladinIsTankOnly_NotHealer()
        {
            // Regression for commit 6a0dc1d: the early docs called the
            // Paladin a tank+healer hybrid, but live DAoC 1.65 Paladins
            // have no real heal spells. Tank yes, healer no.
            MimicConfig.BOT_AI_V2_TANK_CLASSES = "Armsman,Paladin,Reaver,Hero,Warden,Champion,Warrior,Thane";
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "Cleric,Friar,Heretic,Druid,Bard,Warden,Mentalist,Healer,Shaman";

            Assert.That(MimicConfig.IsTankClass((int)eCharacterClass.Paladin), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Paladin), Is.False);
        }

        [Test]
        public void IsMeleeDpsClass_ReaverIsTankOnly_NotMeleeDps()
        {
            // Regression for the bd02702 audit: Reaver was double-listed
            // in tank AND melee_dps before, leading to a confused composer.
            // Keep him tank-only.
            MimicConfig.BOT_AI_V2_TANK_CLASSES = "Armsman,Paladin,Reaver,Hero,Warden,Champion,Warrior,Thane";
            MimicConfig.BOT_AI_V2_MELEE_DPS_CLASSES = "Infiltrator,Mercenary,Minstrel,Blademaster,Nightshade,Vampiir,Valewalker,Berserker,Savage,Shadowblade,Skald,Valkyrie,MaulerAlb,MaulerMid,MaulerHib";

            Assert.That(MimicConfig.IsTankClass((int)eCharacterClass.Reaver), Is.True);
            Assert.That(MimicConfig.IsMeleeDpsClass((int)eCharacterClass.Reaver), Is.False);
        }

        [Test]
        public void IsHealerClass_EmptyCsv_ReturnsFalse()
        {
            // An empty / null CSV must NOT match any class — opt-in
            // disabled is a legitimate config (operator wants no Bot
            // AI v2 healer strategy on anyone).
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = string.Empty;
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.False);

            MimicConfig.BOT_AI_V2_HEALER_CLASSES = null;
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.False);

            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "   ,  ,  ";
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.False);
        }

        [Test]
        public void IsHealerClass_MixedCase_StillMatches()
        {
            // The CSV matcher is case-insensitive (StringComparison.
            // OrdinalIgnoreCase). An operator typing "cleric,FRIAR"
            // should still get the matching bots flagged.
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "cleric,FRIAR,DrUiD";

            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Friar), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Druid), Is.True);
        }

        [Test]
        public void IsHealerClass_InvalidToken_DoesNotMatchValidEntries()
        {
            // afdc560 added a warn-once log on tokens that don't parse
            // as a known eCharacterClass. The behaviour we pin here is
            // the OTHER guarantee from that commit: a typo doesn't
            // poison the rest of the list. "Frair" is ignored, "Cleric"
            // still matches.
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "Cleric,Frair,Druid";

            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Druid), Is.True);
            // The Friar (the intended token) does NOT auto-resolve from
            // a typo — the operator has to fix the CSV.
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Friar), Is.False);
        }

        [Test]
        public void IsHealerClass_WhitespaceAroundTokens_StillMatches()
        {
            // Operators routinely add spaces after commas in long lists.
            // The matcher trims; verify the trim still applies.
            MimicConfig.BOT_AI_V2_HEALER_CLASSES = "Cleric, Friar , Druid";

            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Cleric), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Friar), Is.True);
            Assert.That(MimicConfig.IsHealerClass((int)eCharacterClass.Druid), Is.True);
        }
    }
}
