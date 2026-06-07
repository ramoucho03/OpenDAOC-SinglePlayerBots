using DOL.GS.RealmAbilities;
using System;
using System.Collections.Generic;

namespace DOL.GS.Scripts
{
    // Realm-ability loadout surface of MimicNPC.
    //
    // Mimics set a RealmLevel (see MimicNPC.cs SetLevel / PvPFrontierSystem
    // ApplyFrontierRealmRank) but historically never spent the matching realm
    // spec points — so a "RR10" frontier bot was mechanically RR0: no
    // Toughness, no Augmented stats, no Determination, no Mastery of Pain.
    // ApplyRealmAbilities() fixes that: it spends a budget equal to the bot's
    // RealmLevel on an archetype-appropriate template of PASSIVE Old-Frontier
    // realm abilities.
    //
    // Only passive RAs are granted. They auto-apply through the standard
    // Ability.Activate() -> living.AbilityBonus[...] path (RAPropertyEnhancer /
    // RAStatEnhancer), which the mimic's property calculators already read
    // (StatCalculator, MaxHealthCalculator/Of_Toughness, ResistCalculator,
    // MaxManaCalculator, ...). Active RAs (Purge, Ignore Pain, Mastery of
    // Concentration, the Rain/Volley actives, ...) are intentionally skipped:
    // the mimic AI never triggers them, so points spent there would be wasted.
    //
    // Budget mirrors AbstractServerRules.GetPlayerRealmPointsTotal — a player's
    // available realm spec points equal their RealmLevel (min 1 above level 19).
    public partial class MimicNPC
    {
        // Generous wishlists, ordered by priority. Each is filtered at apply
        // time against the class's own trainable RA list (SkillBase
        // .GetClassRealmAbilities), so listing an RA a class can't train is
        // harmless — it's simply skipped. Determination vs DeterminationHybrid:
        // a class only ever has one of the two in its trainable list, so both
        // are listed and the right one is picked automatically.

        private static readonly string[] _raTank =
        {
            "AtlasOF_AugStr", "AtlasOF_AugCon", "AtlasOF_Toughness", "AtlasOF_MasteryOfPain",
            "AtlasOF_DeterminationHybrid", "AtlasOF_Determination", "AtlasOF_AugDex",
            "AtlasOF_MasteryOfArms", "AtlasOF_AvoidanceOfMagic", "AtlasOF_AugQui",
            "AtlasOF_Regeneration",
        };

        private static readonly string[] _raMelee =
        {
            "AtlasOF_MasteryOfPain", "AtlasOF_AugStr", "AtlasOF_AugDex", "AtlasOF_AugQui",
            "AtlasOF_Toughness", "AtlasOF_AugCon", "AtlasOF_DeterminationHybrid",
            "AtlasOF_Determination", "AtlasOF_Dodger", "AtlasOF_DualistsReflexes",
            "AtlasOF_AvoidanceOfMagic",
        };

        private static readonly string[] _raAssassin =
        {
            "AtlasOF_MasteryOfPain", "AtlasOF_AugDex", "AtlasOF_AugQui", "AtlasOF_AugStr",
            "AtlasOF_Dodger", "AtlasOF_Toughness", "AtlasOF_AugCon",
            "AtlasOF_DualistsReflexes", "AtlasOF_AvoidanceOfMagic",
        };

        private static readonly string[] _raArcher =
        {
            "AtlasOF_MasteryOfArchery", "AtlasOF_FalconsEye", "AtlasOF_AugDex", "AtlasOF_AugQui",
            "AtlasOF_AugStr", "AtlasOF_Toughness", "AtlasOF_AugCon",
            "AtlasOF_DeterminationHybrid", "AtlasOF_Determination", "AtlasOF_AvoidanceOfMagic",
        };

        private static readonly string[] _raCaster =
        {
            "AtlasOF_AugAcuity", "AtlasOF_WildPower", "AtlasOF_MasteryOfMagery", "AtlasOF_Toughness",
            "AtlasOF_AugCon", "AtlasOF_AugDex", "AtlasOF_EtherealBond", "AtlasOF_Serenity",
            "AtlasOF_WildArcana", "AtlasOF_MasteryOfTheArt", "AtlasOF_MasteryOfTheArcane",
            "AtlasOF_AvoidanceOfMagic",
        };

        private static readonly string[] _raHealer =
        {
            "AtlasOF_AugAcuity", "AtlasOF_MasteryOfHealing", "AtlasOF_WildHealing", "AtlasOF_AugCon",
            "AtlasOF_Toughness", "AtlasOF_AugDex", "AtlasOF_Serenity", "AtlasOF_EtherealBond",
            "AtlasOF_DeterminationHybrid", "AtlasOF_Determination", "AtlasOF_AvoidanceOfMagic",
        };

        /// <summary>
        /// (Re)builds this mimic's passive realm-ability loadout from its
        /// current RealmLevel. Safe to call repeatedly — any previously granted
        /// RAs are wiped first, so frontier RR overrides and rank-ups don't
        /// stack duplicate bonuses. Does not touch current Health/Mana/Endurance
        /// (callers that want the new caps topped up do so themselves).
        /// </summary>
        public void ApplyRealmAbilities()
        {
            if (CharacterClass == null)
                return;

            // Wipe the previous loadout (deactivates AbilityBonus + clears the
            // realm list) without consuming a respec stone.
            RespecRealm(false);

            int points = Level > 19 ? Math.Max(1, RealmLevel) : RealmLevel;
            if (points <= 0)
                return;

            List<RealmAbility> classRAs = SkillBase.GetClassRealmAbilities(CharacterClass.ID);
            if (classRAs == null || classRAs.Count == 0)
                return;

            Dictionary<string, RealmAbility> byKey = new(StringComparer.OrdinalIgnoreCase);
            foreach (RealmAbility ra in classRAs)
            {
                if (ra != null && ra is not RR5RealmAbility && !byKey.ContainsKey(ra.KeyName))
                    byKey[ra.KeyName] = ra;
            }

            string[] wishlist = GetRealmAbilityWishlist();
            Dictionary<string, int> chosen = new(StringComparer.OrdinalIgnoreCase);

            // Breadth first (everything affordable up to L3), then depth (top
            // priorities up to L5). A mid-RR bot ends with a useful spread; a
            // high-RR frontier bot still maxes its mainline RAs.
            BuyPass(wishlist, byKey, chosen, ref points, 3);
            BuyPass(wishlist, byKey, chosen, ref points, 5);

            foreach (KeyValuePair<string, int> kv in chosen)
            {
                if (!byKey.TryGetValue(kv.Key, out RealmAbility ra))
                    continue;

                ra.Level = kv.Value;
                AddAbility(ra, false);      // registers in m_abilities + Activate() -> AbilityBonus
                AddRealmAbility(ra, false); // surfaces in GetRealmAbilities() / the inspector
            }
        }

        private static void BuyPass(string[] wishlist, IReadOnlyDictionary<string, RealmAbility> byKey,
            Dictionary<string, int> chosen, ref int points, int targetLevel)
        {
            foreach (string key in wishlist)
            {
                if (points <= 0)
                    break;

                if (!byKey.TryGetValue(key, out RealmAbility ra))
                    continue;

                chosen.TryGetValue(key, out int current);
                int cap = Math.Min(ra.MaxLevel, targetLevel);

                for (int lvl = current; lvl < cap; lvl++)
                {
                    int cost = ra.CostForUpgrade(lvl); // cost to go from lvl -> lvl+1
                    if (cost <= 0 || cost > points)
                        break;

                    points -= cost;
                    current = lvl + 1;
                }

                if (current > 0)
                    chosen[key] = current;
            }
        }

        private string[] GetRealmAbilityWishlist()
        {
            MimicCombatProfile p = CombatProfile;

            bool isHealer = p != null && p.HasRole(eMimicCombatRole.Healer);
            bool isArcher = p != null && p.HasRole(eMimicCombatRole.Archer);
            bool isAssassin = p != null && p.HasRole(eMimicCombatRole.Assassin);
            bool isTank = p != null && p.HasRole(eMimicCombatRole.Tank);
            bool isMelee = p != null && p.HasRole(eMimicCombatRole.MeleeDps);
            bool isCaster = (p != null && p.PrefersCasting)
                || CharacterClass?.ClassType == eClassType.ListCaster;

            // Order matters: melee-primary hybrids (Vampiir, Valewalker) carry a
            // CasterDps flag but want the melee template, so isMelee is checked
            // before isCaster. Pure casters have no MeleeDps flag and fall to
            // the caster template.
            if (isHealer) return _raHealer;
            if (isArcher) return _raArcher;
            if (isAssassin) return _raAssassin;
            if (isTank) return _raTank;
            if (isMelee) return _raMelee;
            if (isCaster) return _raCaster;

            return _raTank;
        }
    }
}
