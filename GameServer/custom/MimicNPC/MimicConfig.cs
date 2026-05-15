using System;
using DOL.GS.ServerProperties;

namespace DOL.GS.Scripts
{
    public static class MimicConfig
    {
        public static readonly bool LFG_CLASS_BIAS = true;     // Not implemented
        public static readonly bool LFG_LEVEL_BIAS = true;     // Should bots take level difference into account when trying to group
        public static readonly bool WEAPON_ROG = false;        // Not implemented
        public static readonly bool ARMOR_ROG = true;          // Should bots recieve ROG items based on class, or random items from the DB
        public static readonly bool PLAYER_LOOTMASTER = false; // Should all loot go to the player leader or distribute to bots as normal

        // Strategy/trigger/action layer. When false the system stays inert
        // and the existing FSM keeps full control of bot behaviour. Active
        // par défaut sur ce fork : nouvelles stratégies disponibles via /mstrategy.
        public static bool USE_STRATEGY_SYSTEM = true;

        // ----------------------------------------------------------------
        // Bot AI v2 role-class whitelists.
        //
        // Each CSV lists the eCharacterClass names that should auto-enable
        // the matching role strategy at bot creation. Classes can appear in
        // multiple lists — strategies are composable, so a Druid runs the
        // healer AND caster_dps cycle, a Bard runs healer AND cc, a Reaver
        // runs tank AND melee_dps, a Friar runs healer AND caster_dps, etc.
        //
        // The defaults reflect a 1.65-era reading of each class. Operators
        // who want a Reaver to run the CC cycle (say) only need to edit the
        // server property at runtime; the role strategy is then enabled the
        // next time a bot of that class spawns.
        // ----------------------------------------------------------------

        [ServerProperty("npc", "bot_ai_v2_healer_classes",
            "CSV of eCharacterClass names that auto-enable the v2 healer strategy.",
            "Cleric,Friar,Heretic,Druid,Bard,Warden,Mentalist,Healer,Shaman")]
        public static string BOT_AI_V2_HEALER_CLASSES;

        [ServerProperty("npc", "bot_ai_v2_tank_classes",
            "CSV of eCharacterClass names that auto-enable the v2 tank strategy.",
            "Armsman,Paladin,Reaver,Hero,Warden,Champion,Warrior,Thane")]
        public static string BOT_AI_V2_TANK_CLASSES;

        [ServerProperty("npc", "bot_ai_v2_melee_dps_classes",
            "CSV of eCharacterClass names that auto-enable the v2 melee DPS strategy.",
            "Infiltrator,Mercenary,Minstrel,Blademaster,Nightshade,Vampiir,Valewalker,Berserker,Savage,Shadowblade,Skald,Valkyrie,MaulerAlb,MaulerMid,MaulerHib")]
        public static string BOT_AI_V2_MELEE_DPS_CLASSES;

        [ServerProperty("npc", "bot_ai_v2_ranged_dps_classes",
            "CSV of eCharacterClass names that auto-enable the v2 ranged DPS strategy.",
            "Scout,Ranger,Hunter")]
        public static string BOT_AI_V2_RANGED_DPS_CLASSES;

        [ServerProperty("npc", "bot_ai_v2_caster_dps_classes",
            "CSV of eCharacterClass names that auto-enable the v2 caster DPS strategy.",
            "Wizard,Theurgist,Cabalist,Sorcerer,Necromancer,Heretic,Eldritch,Enchanter,Mentalist,Animist,Bainshee,Valewalker,Runemaster,Spiritmaster,Bonedancer,Warlock,Thane")]
        public static string BOT_AI_V2_CASTER_DPS_CLASSES;

        [ServerProperty("npc", "bot_ai_v2_cc_classes",
            "CSV of eCharacterClass names that auto-enable the v2 crowd-control strategy.",
            "Sorcerer,Minstrel,Theurgist,Enchanter,Bard,Mentalist,Animist,Druid,Runemaster,Spiritmaster,Warlock,Healer,Vampiir")]
        public static string BOT_AI_V2_CC_CLASSES;

        // Heal thresholds specifically for mimic groups. Bumped above the
        // generic NPC default (75/37) so healers stay proactive. 0 = use
        // the hard-coded fallback inside MimicGroup.
        [ServerProperty("npc", "mimic_heal_threshold",
            "Heal % threshold below which mimic healers prioritise healing (default 85, vs npc_heal_threshold=75).", 85)]
        public static int MIMIC_HEAL_THRESHOLD;

        [ServerProperty("npc", "mimic_emergency_threshold",
            "Emergency heal % threshold for mimic healers (default 50, vs the generic 37).", 50)]
        public static int MIMIC_EMERGENCY_THRESHOLD;

        // Visual-only static item the bot drops at camp. Verified against
        // the OpenDAOC worldobject database where the canonical campfire
        // entries use model 2656 (primary, ~15 spawns) with 3460 as a
        // larger variant. Both are valid; 2656 is the safe default.
        [ServerProperty("npc", "mimic_campfire_model",
            "GameStaticItem model used as the camp fire visual (2656 = OpenDAOC standard campfire, 3460 = larger campfire variant).", 2656)]
        public static int MIMIC_CAMPFIRE_MODEL;

        public static bool IsHealerClass(int classId)    => MatchesCsv(BOT_AI_V2_HEALER_CLASSES, classId);
        public static bool IsTankClass(int classId)      => MatchesCsv(BOT_AI_V2_TANK_CLASSES, classId);
        public static bool IsMeleeDpsClass(int classId)  => MatchesCsv(BOT_AI_V2_MELEE_DPS_CLASSES, classId);
        public static bool IsRangedDpsClass(int classId) => MatchesCsv(BOT_AI_V2_RANGED_DPS_CLASSES, classId);
        public static bool IsCasterDpsClass(int classId) => MatchesCsv(BOT_AI_V2_CASTER_DPS_CLASSES, classId);
        public static bool IsCcClass(int classId)        => MatchesCsv(BOT_AI_V2_CC_CLASSES, classId);

        private static bool MatchesCsv(string csv, int classId)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return false;

            string current = Enum.GetName(typeof(eCharacterClass), classId);

            if (string.IsNullOrEmpty(current))
                return false;

            foreach (string token in csv.Split(','))
            {
                string trimmed = token.Trim();

                if (trimmed.Length == 0)
                    continue;

                if (string.Equals(trimmed, current, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
