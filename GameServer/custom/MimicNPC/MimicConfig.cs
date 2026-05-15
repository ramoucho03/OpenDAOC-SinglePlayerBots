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

        /// <summary>
        /// Comma-separated list of MimicNPC class names (matching
        /// <c>eCharacterClass</c>) for which role-specific Bot AI v2
        /// strategies should be enabled at bot creation. Empty disables v2
        /// everywhere — bots still get the meta strategies (Survival,
        /// Awareness, Assist, Support, Camp) but no combat strategy is
        /// auto-attached.
        ///
        /// Default targets pure healers only: Cleric/Friar (Albion),
        /// Druid (Hibernia), Healer/Shaman (Midgard). Tank/DPS strategies
        /// land in a follow-up PR.
        /// </summary>
        [ServerProperty("npc", "bot_ai_v2_classes",
            "CSV of eCharacterClass names that should auto-enable role-specific Bot AI v2 strategies (default: pure healers).",
            "Cleric,Friar,Druid,Healer,Shaman")]
        public static string BOT_AI_V2_CLASSES;

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

        /// <summary>
        /// Returns true when <paramref name="charClassId"/> matches one of
        /// the class names listed in <see cref="BOT_AI_V2_CLASSES"/>. Names
        /// are compared case-insensitively against <see cref="eCharacterClass"/>
        /// enum names; unknown entries in the CSV are ignored.
        /// </summary>
        public static bool IsAiV2Class(int charClassId)
        {
            string csv = BOT_AI_V2_CLASSES;

            if (string.IsNullOrWhiteSpace(csv))
                return false;

            string current = Enum.GetName(typeof(eCharacterClass), charClassId);

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