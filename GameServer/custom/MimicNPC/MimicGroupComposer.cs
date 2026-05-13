using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// Builds balanced /mgroup compositions and assigns roles after creation.
    /// Role assignment is best-effort: if the comp lacks a class for a role,
    /// the role falls back to the next best available bot.
    /// </summary>
    public static class MimicGroupComposer
    {
        public enum eRole { Tank, Healer, Support, CC, Caster, MeleeDPS }

        // Class catalogs per realm and role. A class can appear in multiple
        // role lists (e.g. Bard is both Healer and Support/CC). Picking is
        // randomized within each role so successive /mgroup calls vary.
        private static readonly Dictionary<eRealm, Dictionary<eRole, eMimicClass[]>> _classesByRole = new()
        {
            [eRealm.Albion] = new()
            {
                [eRole.Tank]     = new[] { eMimicClass.Armsman, eMimicClass.Paladin, eMimicClass.Mercenary },
                [eRole.Healer]   = new[] { eMimicClass.Cleric, eMimicClass.Friar },
                [eRole.Support]  = new[] { eMimicClass.Friar, eMimicClass.Cleric, eMimicClass.Paladin },
                [eRole.CC]       = new[] { eMimicClass.Sorcerer, eMimicClass.Minstrel },
                [eRole.Caster]   = new[] { eMimicClass.Wizard, eMimicClass.Theurgist, eMimicClass.Cabalist, eMimicClass.Sorcerer },
                [eRole.MeleeDPS] = new[] { eMimicClass.Mercenary, eMimicClass.Reaver, eMimicClass.Armsman, eMimicClass.Infiltrator },
            },
            [eRealm.Hibernia] = new()
            {
                [eRole.Tank]     = new[] { eMimicClass.Hero, eMimicClass.Champion, eMimicClass.Blademaster },
                [eRole.Healer]   = new[] { eMimicClass.Druid, eMimicClass.Bard, eMimicClass.Warden },
                [eRole.Support]  = new[] { eMimicClass.Warden, eMimicClass.Bard, eMimicClass.Druid },
                [eRole.CC]       = new[] { eMimicClass.Bard, eMimicClass.Enchanter, eMimicClass.Mentalist },
                [eRole.Caster]   = new[] { eMimicClass.Eldritch, eMimicClass.Enchanter, eMimicClass.Mentalist, eMimicClass.Valewalker },
                [eRole.MeleeDPS] = new[] { eMimicClass.Blademaster, eMimicClass.Champion, eMimicClass.Hero, eMimicClass.Nightshade, eMimicClass.Valewalker },
            },
            [eRealm.Midgard] = new()
            {
                [eRole.Tank]     = new[] { eMimicClass.Warrior, eMimicClass.Thane, eMimicClass.Berserker },
                [eRole.Healer]   = new[] { eMimicClass.Healer, eMimicClass.Shaman },
                [eRole.Support]  = new[] { eMimicClass.Shaman, eMimicClass.Skald, eMimicClass.Thane },
                [eRole.CC]       = new[] { eMimicClass.Healer, eMimicClass.Runemaster, eMimicClass.Spiritmaster },
                [eRole.Caster]   = new[] { eMimicClass.Runemaster, eMimicClass.Spiritmaster, eMimicClass.Bonedancer },
                [eRole.MeleeDPS] = new[] { eMimicClass.Berserker, eMimicClass.Savage, eMimicClass.Warrior, eMimicClass.Shadowblade, eMimicClass.Hunter },
            },
        };

        // Ordered template: as groupSize grows, fill roles in this order.
        // Slots 1..N from this list make a coherent N-man group.
        private static readonly eRole[] _template =
        {
            eRole.Tank,      // 1
            eRole.Healer,    // 2
            eRole.Healer,    // 3 (second healer)
            eRole.CC,        // 4
            eRole.Caster,    // 5
            eRole.MeleeDPS,  // 6
            eRole.Support,   // 7
            eRole.MeleeDPS,  // 8
        };

        /// <summary>
        /// Picks an ordered list of mimic classes for the given realm and group size.
        /// The returned list preserves order so role assignment can use it as a parallel array.
        /// </summary>
        public static List<eMimicClass> BuildComposition(eRealm realm, int groupSize)
        {
            List<eMimicClass> result = new(groupSize);

            if (!_classesByRole.TryGetValue(realm, out Dictionary<eRole, eMimicClass[]> rolesForRealm))
                return result;

            int slots = System.Math.Clamp(groupSize, 1, _template.Length);

            for (int i = 0; i < slots; i++)
            {
                eRole role = _template[i];

                if (!rolesForRealm.TryGetValue(role, out eMimicClass[] candidates) || candidates.Length == 0)
                    continue;

                result.Add(candidates[Util.Random(candidates.Length - 1)]);
            }

            return result;
        }

        /// <summary>
        /// Walks the created mimics and assigns leader, main-assist, tank, CC, puller and healer roles
        /// based on each bot's class. The first qualified bot for each role wins; later candidates are skipped.
        /// </summary>
        public static void AutoAssignRoles(List<MimicNPC> mimics)
        {
            if (mimics == null || mimics.Count == 0)
                return;

            MimicGroup mg = mimics[0].Group?.MimicGroup;

            if (mg == null)
                return;

            MimicNPC tank = mimics.FirstOrDefault(m => IsTankClass(m));
            MimicNPC healer = mimics.FirstOrDefault(m => IsHealerClass(m));
            MimicNPC cc = mimics.FirstOrDefault(m => IsCCClass(m));
            MimicNPC puller = mimics.FirstOrDefault(m => MimicGroup.CanPull(m));

            // Leader & main assist default to the tank; the player remains the group leader
            // if they own the group, but inside the MimicGroup we still want a tank-focused assist.
            MimicNPC leader = tank ?? mimics[0];
            MimicNPC assist = tank ?? mimics[0];

            mg.SetLeader(leader);
            mg.SetMainAssist(assist);

            if (tank != null)   mg.SetMainTank(tank);
            if (cc != null)     mg.SetMainCC(cc);
            if (puller != null) mg.SetMainPuller(puller);
            if (healer != null && healer.MimicBrain != null)
                healer.MimicBrain.IsHealer = true;
        }

        public static bool IsTankClass(MimicNPC m)
        {
            if (m == null) return false;
            return (eMimicClass)m.CharacterClass.ID is
                eMimicClass.Armsman or eMimicClass.Paladin or eMimicClass.Mercenary or
                eMimicClass.Hero or eMimicClass.Champion or eMimicClass.Blademaster or
                eMimicClass.Warrior or eMimicClass.Thane or eMimicClass.Berserker;
        }

        public static bool IsHealerClass(MimicNPC m)
        {
            if (m == null) return false;
            return (eMimicClass)m.CharacterClass.ID is
                eMimicClass.Cleric or eMimicClass.Friar or
                eMimicClass.Druid or eMimicClass.Bard or eMimicClass.Warden or
                eMimicClass.Healer or eMimicClass.Shaman;
        }

        public static bool IsCCClass(MimicNPC m)
        {
            if (m == null) return false;
            return (eMimicClass)m.CharacterClass.ID is
                eMimicClass.Sorcerer or eMimicClass.Minstrel or
                eMimicClass.Bard or eMimicClass.Enchanter or eMimicClass.Mentalist or
                eMimicClass.Runemaster or eMimicClass.Spiritmaster;
        }
    }
}
