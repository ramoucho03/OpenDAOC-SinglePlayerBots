using System;
using DOL.GS.Scripts;
using NUnit.Framework;

namespace DOL.GS.Tests
{
    [TestFixture]
    public class UT_MimicCombatProfile
    {
        [Test]
        public void Registry_EveryMimicClass_HasACombatProfileWithRole()
        {
            foreach (eMimicClass cls in Enum.GetValues(typeof(eMimicClass)))
            {
                if (cls == eMimicClass.None)
                    continue;

                MimicCombatProfile profile = MimicCombatProfileRegistry.Get(cls);

                Assert.That(profile, Is.Not.Null, $"{cls} has no combat profile.");
                Assert.That(profile.MimicClass, Is.EqualTo(cls));
                Assert.That(profile.Roles, Is.Not.EqualTo(eMimicCombatRole.None), $"{cls} has no combat role.");
                Assert.That(profile.PrimaryRole, Is.Not.EqualTo(eMimicCombatRole.None), $"{cls} has no primary role.");
            }
        }

        [Test]
        public void Registry_KeyClasses_HaveExpectedRoles()
        {
            Assert.That(MimicCombatProfileRegistry.HasRole(eMimicClass.Armsman, eMimicCombatRole.Tank), Is.True);
            Assert.That(MimicCombatProfileRegistry.HasRole(eMimicClass.Cleric, eMimicCombatRole.Healer), Is.True);
            Assert.That(MimicCombatProfileRegistry.HasRole(eMimicClass.Sorcerer, eMimicCombatRole.CrowdControl), Is.True);
            Assert.That(MimicCombatProfileRegistry.HasRole(eMimicClass.Wizard, eMimicCombatRole.CasterDps), Is.True);
            Assert.That(MimicCombatProfileRegistry.HasRole(eMimicClass.Scout, eMimicCombatRole.Archer), Is.True);
            Assert.That(MimicCombatProfileRegistry.HasRole(eMimicClass.Infiltrator, eMimicCombatRole.Assassin), Is.True);
        }

        [Test]
        public void ScoreTarget_PvpCc_PrioritizesHealerOverCaster()
        {
            MimicCombatProfile sorcerer = MimicCombatProfileRegistry.Get(eMimicClass.Sorcerer);
            MimicCombatProfile cleric = MimicCombatProfileRegistry.Get(eMimicClass.Cleric);
            MimicCombatProfile wizard = MimicCombatProfileRegistry.Get(eMimicClass.Wizard);

            int healerScore = sorcerer.ScoreTarget(cleric, eMimicCombatMode.PvP, false, false, false, false, false, 1000);
            int casterScore = sorcerer.ScoreTarget(wizard, eMimicCombatMode.PvP, false, false, false, false, false, 1000);

            Assert.That(healerScore, Is.LessThan(casterScore));
        }

        [Test]
        public void ScoreTarget_Tank_StronglyPrioritizesProtectedMemberThreat()
        {
            MimicCombatProfile armsman = MimicCombatProfileRegistry.Get(eMimicClass.Armsman);
            MimicCombatProfile enemyBerserker = MimicCombatProfileRegistry.Get(eMimicClass.Berserker);

            int normalScore = armsman.ScoreTarget(enemyBerserker, eMimicCombatMode.PvP, false, false, false, false, false, 500);
            int peelScore = armsman.ScoreTarget(enemyBerserker, eMimicCombatMode.PvP, false, false, true, false, false, 500);

            Assert.That(peelScore, Is.LessThan(normalScore));
        }

        [Test]
        public void ScoreTarget_Dps_PrioritizesLowHealthTarget()
        {
            MimicCombatProfile wizard = MimicCombatProfileRegistry.Get(eMimicClass.Wizard);
            MimicCombatProfile enemyWarrior = MimicCombatProfileRegistry.Get(eMimicClass.Warrior);

            int healthyScore = wizard.ScoreTarget(enemyWarrior, eMimicCombatMode.PvP, false, false, false, false, false, 800);
            int lowHealthScore = wizard.ScoreTarget(enemyWarrior, eMimicCombatMode.PvP, false, false, false, true, false, 800);

            Assert.That(lowHealthScore, Is.LessThan(healthyScore));
        }

        [Test]
        public void ShouldUseAoe_RespectsClusterAndCrowdControlSafety()
        {
            MimicCombatProfile wizard = MimicCombatProfileRegistry.Get(eMimicClass.Wizard);
            MimicCombatProfile reaver = MimicCombatProfileRegistry.Get(eMimicClass.Reaver);
            MimicCombatProfile cleric = MimicCombatProfileRegistry.Get(eMimicClass.Cleric);
            MimicCombatProfile bard = MimicCombatProfileRegistry.Get(eMimicClass.Bard);

            Assert.That(wizard.ShouldUseAoe(2, false, false), Is.True);
            Assert.That(wizard.ShouldUseAoe(1, false, false), Is.False);
            Assert.That(wizard.ShouldUseAoe(3, true, false), Is.False);
            Assert.That(reaver.ShouldUseAoe(2, false, false), Is.False);
            Assert.That(reaver.ShouldUseAoe(3, false, false), Is.True);
            Assert.That(cleric.ShouldUseAoe(4, false, false), Is.False);
            Assert.That(bard.ShouldUseAoe(2, false, true), Is.True);
            Assert.That(bard.ShouldUseAoe(2, true, true), Is.False);
        }
    }
}
