using System;
using System.Collections.Generic;

namespace DOL.GS.Scripts
{
    [Flags]
    public enum eMimicCombatRole
    {
        None = 0,
        Tank = 1 << 0,
        Healer = 1 << 1,
        Support = 1 << 2,
        CrowdControl = 1 << 3,
        CasterDps = 1 << 4,
        MeleeDps = 1 << 5,
        Archer = 1 << 6,
        Assassin = 1 << 7,
        PetCaster = 1 << 8,
        Debuffer = 1 << 9,
        Puller = 1 << 10,
    }

    public enum eMimicCombatMode
    {
        PvE,
        PvP,
    }

    public enum eMimicTargetPriority
    {
        MainAssist,
        ImmediateThreat,
        ProtectedMemberThreat,
        Healer,
        CrowdControl,
        Caster,
        Support,
        LowHealth,
        MeleeDps,
        Tank,
        Nearest,
    }

    [Flags]
    public enum eMimicAoePolicy
    {
        Never = 0,
        DamageWhenClustered = 1 << 0,
        CrowdControlWhenClustered = 1 << 1,
        PbaoeWhenSafe = 1 << 2,
    }

    public sealed class MimicCombatProfile
    {
        public eMimicClass MimicClass { get; }
        public eMimicCombatRole Roles { get; }
        public eMimicCombatRole PrimaryRole { get; }
        public eMimicAoePolicy AoePolicy { get; }
        public IReadOnlyList<eMimicTargetPriority> PveTargetPriorities { get; }
        public IReadOnlyList<eMimicTargetPriority> PvpTargetPriorities { get; }
        public int DamageAoeMinTargets { get; }
        public int CrowdControlAoeMinTargets { get; }

        public bool PrefersMelee =>
            HasRole(eMimicCombatRole.Tank) ||
            HasRole(eMimicCombatRole.MeleeDps) ||
            HasRole(eMimicCombatRole.Assassin);

        public bool PrefersCasting =>
            HasRole(eMimicCombatRole.CasterDps) ||
            HasRole(eMimicCombatRole.PetCaster) ||
            HasRole(eMimicCombatRole.Debuffer);

        public MimicCombatProfile(
            eMimicClass mimicClass,
            eMimicCombatRole roles,
            eMimicCombatRole primaryRole,
            eMimicAoePolicy aoePolicy,
            IReadOnlyList<eMimicTargetPriority> pveTargetPriorities,
            IReadOnlyList<eMimicTargetPriority> pvpTargetPriorities,
            int damageAoeMinTargets = 2,
            int crowdControlAoeMinTargets = 2)
        {
            MimicClass = mimicClass;
            Roles = roles;
            PrimaryRole = primaryRole;
            AoePolicy = aoePolicy;
            PveTargetPriorities = pveTargetPriorities;
            PvpTargetPriorities = pvpTargetPriorities;
            DamageAoeMinTargets = Math.Max(2, damageAoeMinTargets);
            CrowdControlAoeMinTargets = Math.Max(2, crowdControlAoeMinTargets);
        }

        public bool HasRole(eMimicCombatRole role)
        {
            return (Roles & role) != 0;
        }

        public bool ShouldUseAoe(int hostileCount, bool wouldBreakCrowdControl, bool crowdControlSpell)
        {
            if (wouldBreakCrowdControl || hostileCount < 0)
                return false;

            if (crowdControlSpell)
                return (AoePolicy & eMimicAoePolicy.CrowdControlWhenClustered) != 0
                    && hostileCount >= CrowdControlAoeMinTargets;

            return (AoePolicy & eMimicAoePolicy.DamageWhenClustered) != 0
                && hostileCount >= DamageAoeMinTargets;
        }

        public bool ShouldUsePbaoe(int hostileCount, bool wouldBreakCrowdControl)
        {
            if (wouldBreakCrowdControl || hostileCount < 0)
                return false;

            bool allowed = (AoePolicy & eMimicAoePolicy.PbaoeWhenSafe) != 0
                || (AoePolicy & eMimicAoePolicy.DamageWhenClustered) != 0;

            return allowed && hostileCount >= DamageAoeMinTargets;
        }

        public int ScoreTarget(
            MimicCombatProfile targetProfile,
            eMimicCombatMode mode,
            bool isFocusTarget,
            bool isAttackingSelf,
            bool isAttackingProtectedMember,
            bool isLowHealth,
            bool isCrowdControlled,
            int distance)
        {
            if (isCrowdControlled && !HasRole(eMimicCombatRole.CrowdControl))
                return int.MaxValue / 4;

            IReadOnlyList<eMimicTargetPriority> priorities =
                mode == eMimicCombatMode.PvP ? PvpTargetPriorities : PveTargetPriorities;

            int score = priorities.Count * 100;
            for (int i = 0; i < priorities.Count; i++)
            {
                if (MatchesPriority(priorities[i], targetProfile, isFocusTarget, isAttackingSelf, isAttackingProtectedMember, isLowHealth))
                {
                    score = i * 100;
                    break;
                }
            }

            if (isAttackingProtectedMember && HasRole(eMimicCombatRole.Tank))
                score -= 90;
            else if (isAttackingSelf)
                score -= 35;

            if (isFocusTarget)
                score -= HasRole(eMimicCombatRole.CrowdControl) ? 5 : 30;

            if (isLowHealth)
                score -= 25;

            score += Math.Max(0, distance) / 100;
            return Math.Max(0, score);
        }

        private static bool MatchesPriority(
            eMimicTargetPriority priority,
            MimicCombatProfile targetProfile,
            bool isFocusTarget,
            bool isAttackingSelf,
            bool isAttackingProtectedMember,
            bool isLowHealth)
        {
            switch (priority)
            {
                case eMimicTargetPriority.MainAssist:
                    return isFocusTarget;
                case eMimicTargetPriority.ImmediateThreat:
                    return isAttackingSelf;
                case eMimicTargetPriority.ProtectedMemberThreat:
                    return isAttackingProtectedMember;
                case eMimicTargetPriority.Healer:
                    return targetProfile?.HasRole(eMimicCombatRole.Healer) == true;
                case eMimicTargetPriority.CrowdControl:
                    return targetProfile?.HasRole(eMimicCombatRole.CrowdControl) == true;
                case eMimicTargetPriority.Caster:
                    return targetProfile != null
                        && (targetProfile.HasRole(eMimicCombatRole.CasterDps)
                            || targetProfile.HasRole(eMimicCombatRole.PetCaster)
                            || targetProfile.HasRole(eMimicCombatRole.Debuffer));
                case eMimicTargetPriority.Support:
                    return targetProfile != null
                        && (targetProfile.HasRole(eMimicCombatRole.Support)
                            || targetProfile.HasRole(eMimicCombatRole.Debuffer));
                case eMimicTargetPriority.LowHealth:
                    return isLowHealth;
                case eMimicTargetPriority.MeleeDps:
                    return targetProfile != null
                        && (targetProfile.HasRole(eMimicCombatRole.MeleeDps)
                            || targetProfile.HasRole(eMimicCombatRole.Archer)
                            || targetProfile.HasRole(eMimicCombatRole.Assassin));
                case eMimicTargetPriority.Tank:
                    return targetProfile?.HasRole(eMimicCombatRole.Tank) == true;
                case eMimicTargetPriority.Nearest:
                    return true;
                default:
                    return false;
            }
        }
    }

    public static class MimicCombatProfileRegistry
    {
        private static readonly eMimicTargetPriority[] TankPve =
        {
            eMimicTargetPriority.ProtectedMemberThreat,
            eMimicTargetPriority.ImmediateThreat,
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] DpsPve =
        {
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Caster,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] CcPve =
        {
            eMimicTargetPriority.ProtectedMemberThreat,
            eMimicTargetPriority.ImmediateThreat,
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.Caster,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] HealerPve =
        {
            eMimicTargetPriority.ImmediateThreat,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] TankPvp =
        {
            eMimicTargetPriority.ProtectedMemberThreat,
            eMimicTargetPriority.Healer,
            eMimicTargetPriority.CrowdControl,
            eMimicTargetPriority.Caster,
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] DpsPvp =
        {
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Healer,
            eMimicTargetPriority.CrowdControl,
            eMimicTargetPriority.Caster,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] SupportHunterPvp =
        {
            eMimicTargetPriority.Healer,
            eMimicTargetPriority.CrowdControl,
            eMimicTargetPriority.Caster,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Support,
            eMimicTargetPriority.MainAssist,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] CcPvp =
        {
            eMimicTargetPriority.Healer,
            eMimicTargetPriority.CrowdControl,
            eMimicTargetPriority.Caster,
            eMimicTargetPriority.Support,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Nearest,
        };

        private static readonly eMimicTargetPriority[] HealerPvp =
        {
            eMimicTargetPriority.ProtectedMemberThreat,
            eMimicTargetPriority.LowHealth,
            eMimicTargetPriority.Nearest,
        };

        private static readonly Dictionary<eMimicClass, MimicCombatProfile> Profiles = BuildProfiles();

        public static IReadOnlyDictionary<eMimicClass, MimicCombatProfile> AllProfiles => Profiles;

        public static MimicCombatProfile Get(eMimicClass mimicClass, eSpecType specType = eSpecType.None)
        {
            if (Profiles.TryGetValue(mimicClass, out MimicCombatProfile profile))
                return profile;

            return null;
        }

        public static MimicCombatProfile GetForLiving(GameLiving living)
        {
            if (living is MimicNPC mimic && mimic.CombatProfile != null)
                return mimic.CombatProfile;

            if (living is not IGamePlayer player)
                return null;

            int classId = player.CharacterClass.ID;
            if (!Enum.IsDefined(typeof(eMimicClass), classId))
                return null;

            eMimicClass mimicClass = (eMimicClass)classId;
            return mimicClass == eMimicClass.None ? null : Get(mimicClass);
        }

        public static bool HasRole(eMimicClass mimicClass, eMimicCombatRole role)
        {
            return Get(mimicClass)?.HasRole(role) == true;
        }

        private static MimicCombatProfile Profile(
            eMimicClass cls,
            eMimicCombatRole roles,
            eMimicCombatRole primary,
            eMimicAoePolicy aoe,
            IReadOnlyList<eMimicTargetPriority> pve,
            IReadOnlyList<eMimicTargetPriority> pvp,
            int damageAoeMinTargets = 2,
            int ccAoeMinTargets = 2)
        {
            return new MimicCombatProfile(cls, roles, primary, aoe, pve, pvp, damageAoeMinTargets, ccAoeMinTargets);
        }

        private static Dictionary<eMimicClass, MimicCombatProfile> BuildProfiles()
        {
            Dictionary<eMimicClass, MimicCombatProfile> profiles = new();

            void Add(MimicCombatProfile profile) => profiles.Add(profile.MimicClass, profile);

            const eMimicCombatRole tankMelee = eMimicCombatRole.Tank | eMimicCombatRole.MeleeDps | eMimicCombatRole.Puller;
            const eMimicCombatRole melee = eMimicCombatRole.MeleeDps | eMimicCombatRole.Puller;
            const eMimicCombatRole archer = eMimicCombatRole.Archer | eMimicCombatRole.MeleeDps | eMimicCombatRole.Puller;
            const eMimicCombatRole assassin = eMimicCombatRole.Assassin | eMimicCombatRole.MeleeDps;
            const eMimicCombatRole caster = eMimicCombatRole.CasterDps | eMimicCombatRole.Puller;
            const eMimicCombatRole petCaster = eMimicCombatRole.CasterDps | eMimicCombatRole.PetCaster | eMimicCombatRole.Puller;
            const eMimicCombatRole healer = eMimicCombatRole.Healer | eMimicCombatRole.Support;
            const eMimicCombatRole supportCc = eMimicCombatRole.Support | eMimicCombatRole.CrowdControl | eMimicCombatRole.Puller;

            Add(Profile(eMimicClass.Armsman, tankMelee, eMimicCombatRole.Tank, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Cabalist, petCaster | eMimicCombatRole.Debuffer, eMimicCombatRole.CasterDps, eMimicAoePolicy.DamageWhenClustered, DpsPve, DpsPvp));
            Add(Profile(eMimicClass.Cleric, healer, eMimicCombatRole.Healer, eMimicAoePolicy.Never, HealerPve, HealerPvp));
            Add(Profile(eMimicClass.Friar, healer | eMimicCombatRole.MeleeDps, eMimicCombatRole.Healer, eMimicAoePolicy.Never, HealerPve, HealerPvp));
            Add(Profile(eMimicClass.Infiltrator, assassin, eMimicCombatRole.Assassin, eMimicAoePolicy.Never, DpsPve, SupportHunterPvp));
            Add(Profile(eMimicClass.Mercenary, tankMelee, eMimicCombatRole.MeleeDps, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Minstrel, supportCc | eMimicCombatRole.MeleeDps, eMimicCombatRole.CrowdControl, eMimicAoePolicy.CrowdControlWhenClustered, CcPve, CcPvp));
            Add(Profile(eMimicClass.Necromancer, petCaster | eMimicCombatRole.Debuffer, eMimicCombatRole.PetCaster, eMimicAoePolicy.DamageWhenClustered, DpsPve, DpsPvp, 3));
            Add(Profile(eMimicClass.Paladin, tankMelee | eMimicCombatRole.Support, eMimicCombatRole.Tank, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Reaver, tankMelee | eMimicCombatRole.Debuffer, eMimicCombatRole.Tank, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.PbaoeWhenSafe, TankPve, TankPvp, 3));
            Add(Profile(eMimicClass.Scout, archer, eMimicCombatRole.Archer, eMimicAoePolicy.Never, DpsPve, SupportHunterPvp));
            Add(Profile(eMimicClass.Sorcerer, supportCc | eMimicCombatRole.CasterDps | eMimicCombatRole.Debuffer, eMimicCombatRole.CrowdControl, eMimicAoePolicy.CrowdControlWhenClustered, CcPve, CcPvp));
            Add(Profile(eMimicClass.Theurgist, petCaster | eMimicCombatRole.CrowdControl, eMimicCombatRole.PetCaster, eMimicAoePolicy.DamageWhenClustered, DpsPve, DpsPvp, 3));
            Add(Profile(eMimicClass.Wizard, caster, eMimicCombatRole.CasterDps, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.PbaoeWhenSafe, DpsPve, DpsPvp));

            Add(Profile(eMimicClass.Animist, petCaster | eMimicCombatRole.CrowdControl, eMimicCombatRole.PetCaster, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.CrowdControlWhenClustered, CcPve, CcPvp, 3));
            Add(Profile(eMimicClass.Bainshee, caster | eMimicCombatRole.Debuffer, eMimicCombatRole.CasterDps, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.PbaoeWhenSafe, DpsPve, DpsPvp));
            Add(Profile(eMimicClass.Bard, healer | eMimicCombatRole.CrowdControl | eMimicCombatRole.Puller, eMimicCombatRole.CrowdControl, eMimicAoePolicy.CrowdControlWhenClustered, CcPve, CcPvp));
            Add(Profile(eMimicClass.Blademaster, tankMelee, eMimicCombatRole.MeleeDps, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Champion, tankMelee | eMimicCombatRole.Debuffer, eMimicCombatRole.Tank, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Druid, healer | eMimicCombatRole.PetCaster, eMimicCombatRole.Healer, eMimicAoePolicy.Never, HealerPve, HealerPvp));
            Add(Profile(eMimicClass.Eldritch, caster | eMimicCombatRole.Debuffer | eMimicCombatRole.CrowdControl, eMimicCombatRole.CasterDps, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.CrowdControlWhenClustered | eMimicAoePolicy.PbaoeWhenSafe, DpsPve, DpsPvp));
            Add(Profile(eMimicClass.Enchanter, petCaster | eMimicCombatRole.CrowdControl | eMimicCombatRole.Debuffer, eMimicCombatRole.PetCaster, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.CrowdControlWhenClustered | eMimicAoePolicy.PbaoeWhenSafe, CcPve, CcPvp));
            Add(Profile(eMimicClass.Hero, tankMelee, eMimicCombatRole.Tank, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Mentalist, supportCc | eMimicCombatRole.CasterDps | eMimicCombatRole.Support, eMimicCombatRole.CasterDps, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.CrowdControlWhenClustered, CcPve, CcPvp));
            Add(Profile(eMimicClass.Nightshade, assassin | eMimicCombatRole.CasterDps, eMimicCombatRole.Assassin, eMimicAoePolicy.Never, DpsPve, SupportHunterPvp));
            Add(Profile(eMimicClass.Ranger, archer, eMimicCombatRole.Archer, eMimicAoePolicy.Never, DpsPve, SupportHunterPvp));
            Add(Profile(eMimicClass.Valewalker, melee | eMimicCombatRole.CasterDps | eMimicCombatRole.Debuffer, eMimicCombatRole.MeleeDps, eMimicAoePolicy.DamageWhenClustered, DpsPve, DpsPvp, 3));
            Add(Profile(eMimicClass.Warden, healer | eMimicCombatRole.Tank | eMimicCombatRole.MeleeDps, eMimicCombatRole.Support, eMimicAoePolicy.Never, HealerPve, HealerPvp));

            Add(Profile(eMimicClass.Berserker, tankMelee, eMimicCombatRole.MeleeDps, eMimicAoePolicy.Never, TankPve, TankPvp));
            Add(Profile(eMimicClass.Bonedancer, petCaster | eMimicCombatRole.Debuffer, eMimicCombatRole.PetCaster, eMimicAoePolicy.DamageWhenClustered, DpsPve, DpsPvp, 3));
            Add(Profile(eMimicClass.Healer, healer | eMimicCombatRole.CrowdControl | eMimicCombatRole.Puller, eMimicCombatRole.Healer, eMimicAoePolicy.CrowdControlWhenClustered, HealerPve, HealerPvp));
            Add(Profile(eMimicClass.Hunter, archer | eMimicCombatRole.PetCaster, eMimicCombatRole.Archer, eMimicAoePolicy.Never, DpsPve, SupportHunterPvp));
            Add(Profile(eMimicClass.Runemaster, caster | eMimicCombatRole.CrowdControl, eMimicCombatRole.CasterDps, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.CrowdControlWhenClustered, DpsPve, DpsPvp));
            Add(Profile(eMimicClass.Savage, melee, eMimicCombatRole.MeleeDps, eMimicAoePolicy.Never, DpsPve, DpsPvp));
            Add(Profile(eMimicClass.Shadowblade, assassin, eMimicCombatRole.Assassin, eMimicAoePolicy.Never, DpsPve, SupportHunterPvp));
            Add(Profile(eMimicClass.Shaman, healer | eMimicCombatRole.Debuffer | eMimicCombatRole.CasterDps | eMimicCombatRole.Puller, eMimicCombatRole.Support, eMimicAoePolicy.DamageWhenClustered, HealerPve, HealerPvp, 3));
            Add(Profile(eMimicClass.Skald, supportCc | eMimicCombatRole.MeleeDps, eMimicCombatRole.Support, eMimicAoePolicy.Never, CcPve, CcPvp));
            Add(Profile(eMimicClass.Spiritmaster, petCaster | eMimicCombatRole.CrowdControl, eMimicCombatRole.PetCaster, eMimicAoePolicy.DamageWhenClustered | eMimicAoePolicy.CrowdControlWhenClustered | eMimicAoePolicy.PbaoeWhenSafe, CcPve, CcPvp));
            Add(Profile(eMimicClass.Thane, tankMelee | eMimicCombatRole.CasterDps, eMimicCombatRole.Tank, eMimicAoePolicy.DamageWhenClustered, TankPve, TankPvp, 3));
            Add(Profile(eMimicClass.Valkyrie, tankMelee | eMimicCombatRole.Support | eMimicCombatRole.Healer, eMimicCombatRole.Tank, eMimicAoePolicy.DamageWhenClustered, TankPve, TankPvp, 3));
            Add(Profile(eMimicClass.Warrior, tankMelee, eMimicCombatRole.Tank, eMimicAoePolicy.Never, TankPve, TankPvp));

            return profiles;
        }
    }
}
