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

        // Heal thresholds specifically for mimic groups. Bumped above the
        // generic NPC default (75/37) so healers stay proactive. 0 = use
        // the hard-coded fallback inside MimicGroup.
        [ServerProperty("npc", "mimic_heal_threshold",
            "Heal % threshold below which mimic healers prioritise healing (default 85, vs npc_heal_threshold=75).", 85)]
        public static int MIMIC_HEAL_THRESHOLD;

        [ServerProperty("npc", "mimic_emergency_threshold",
            "Emergency heal % threshold for mimic healers (default 50, vs the generic 37).", 50)]
        public static int MIMIC_EMERGENCY_THRESHOLD;
    }
}