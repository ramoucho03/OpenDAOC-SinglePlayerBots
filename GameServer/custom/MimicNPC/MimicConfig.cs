using System;
using System.Collections.Generic;
using System.Reflection;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.Scripts
{
    public static class MimicConfig
    {
        private static readonly Logger _log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
        // Operator typo log de-dup: a misspelled CSV entry is read on every
        // bot spawn, but we only want to whine about it once per server run.
        private static readonly HashSet<string> _warnedInvalidTokens = new(StringComparer.OrdinalIgnoreCase);

        public static readonly bool LFG_CLASS_BIAS = true;     // Not implemented
        public static readonly bool LFG_LEVEL_BIAS = true;     // Should bots take level difference into account when trying to group
        public static readonly bool WEAPON_ROG = false;        // Not implemented
        public static readonly bool ARMOR_ROG = true;          // Should bots recieve ROG items based on class, or random items from the DB
        public static readonly bool PLAYER_LOOTMASTER = false; // Should all loot go to the player leader or distribute to bots as normal

        // Strategy/trigger/action layer. When false the system stays inert
        // and the existing FSM keeps full control of bot behaviour.
        //
        // Disabled by default after a 10-agent audit found two compounded
        // bugs in the strategy layer that break basic combat:
        //   - Healer triggers (GroupMemberHealthLowTrigger,
        //     GroupMemberCriticalTrigger, etc.) read `mg.MemberToHeal`,
        //     which is populated by CheckGroupHealth(), which only runs
        //     from CheckHeals(), which only fires when a trigger matches.
        //     Circular: trigger never fires until MemberToHeal is set,
        //     MemberToHeal never sets until trigger fires.
        //   - Caster triggers gate on HasAggro/InCombat, which don't apply
        //     to a caster that hasn't been directly damaged yet — the FSM
        //     does the right thing already via ScanGroupCombat + AGGRO
        //     state + CheckSpells(Offensive). The strategy layer doubles
        //     and conflicts via cooldowns.
        // KDS-KDS reference fork has no strategy layer at all and combat
        // works correctly through the FSM alone. Flip to true to re-enable
        // for testing / future repair.
        [ServerProperty("npc", "mimic_use_strategy_system",
            "Master switch for the Bot AI v2 strategy/trigger/action layer. When false, only the legacy FSM drives bots.", false)]
        public static bool USE_STRATEGY_SYSTEM;

        // ----------------------------------------------------------------
        // Tunable AI constants. Previously hard-coded across MimicBrain.
        // Each one is read once at bot construction / camp creation, so a
        // /serverproperty change is picked up the next time the condition
        // is evaluated rather than retroactively for already-pulled groups.
        // ----------------------------------------------------------------

        [ServerProperty("npc", "mimic_pull_timeout_ms",
            "Soft cap (ms) for an in-flight pull before the puller is recovered. Default 6000.", 6000)]
        public static int MIMIC_PULL_TIMEOUT_MS;

        [ServerProperty("npc", "mimic_max_mana_throttle_ms",
            "Maximum time (ms) the puller stays mana-throttled before forcibly resuming. Default 90000.", 90000)]
        public static int MIMIC_MAX_MANA_THROTTLE_MS;

        [ServerProperty("npc", "mimic_pull_mana_stop_pct",
            "Puller stops chain-pulling when ANY caster in the group drops below this mana %. Default 30 — the chain breaks here and the group rests back up to the resume %.", 30)]
        public static int MIMIC_PULL_MANA_STOP_PCT;

        [ServerProperty("npc", "mimic_pull_mana_resume_pct",
            "Puller resumes pulling only when EVERY caster is back above this mana %. Must be >= stop. Default 85 — the group rests to near-full before starting a new pull cycle.", 85)]
        public static int MIMIC_PULL_MANA_RESUME_PCT;

        [ServerProperty("npc", "mimic_pull_scan_radius",
            "Maximum distance (units) the puller scans for a pull target. Default 3600.", 3600)]
        public static int MIMIC_PULL_SCAN_RADIUS;

        [ServerProperty("npc", "mimic_group_safety_health_pct",
            "Group safety floor — no mimic starts a new pull or proactive engagement while any group member's HP % is below this threshold. Bots already in combat (HasAggro) keep fighting. Default 35.", 35)]
        public static int MIMIC_GROUP_SAFETY_HEALTH_PCT;

        [ServerProperty("npc", "mimic_pull_pack_radius",
            "Approximate BAF radius (units) used to estimate pack size around a pull candidate. Default 500.", 500)]
        public static int MIMIC_PULL_PACK_RADIUS;

        [ServerProperty("npc", "mimic_healer_flee_health_pct",
            "Health % below which a healer auto-flees from a melee threat. Default 60.", 60)]
        public static int MIMIC_HEALER_FLEE_HEALTH_PCT;

        [ServerProperty("npc", "mimic_healer_combat_reposition",
            "When true (default), healers actively reposition during combat to seat behind the group's tank and out of the enemy front line, without leaving heal range. Movement only — the heal/cure logic is unchanged.", true)]
        public static bool MIMIC_HEALER_COMBAT_REPOSITION;

        [ServerProperty("npc", "mimic_healer_backline_range",
            "Desired distance (units) a repositioning healer keeps between itself and the enemy front line. Default 600.", 600)]
        public static int MIMIC_HEALER_BACKLINE_RANGE;

        // ----------------------------------------------------------------
        // RvR DPS assist train. In PvP, DPS mimics (melee / caster / archer /
        // assassin) focus-fire the target "called" by their group's assist:
        // the human player's target in a player group, otherwise the group's
        // MainAssist (a tank/MA that already prioritises the enemy healer).
        // This concentrates damage and dynamises fights. To avoid a perfect,
        // balance-breaking robotic burst, each bot models a real player's
        // imperfection: a staggered reaction delay before swapping to a newly
        // called target, plus a per-call chance to simply NOT assist (it keeps
        // working its own best-scored target that cycle).
        // ----------------------------------------------------------------

        [ServerProperty("npc", "mimic_assist_error_pct",
            "Chance (0-100) that a DPS mimic does NOT follow a freshly called assist target and instead keeps working its own best-scored target. Models real players who don't assist perfectly. 0 = a perfect (strong) assist train; higher = looser. Default 18.", 18)]
        public static int MIMIC_ASSIST_ERROR_PCT;

        [ServerProperty("npc", "mimic_assist_reaction_min_ms",
            "Minimum reaction delay (ms) before a DPS mimic swaps onto a newly called assist target. Staggers the group so they don't all snap on the same frame. Default 300.", 300)]
        public static int MIMIC_ASSIST_REACTION_MIN_MS;

        [ServerProperty("npc", "mimic_assist_reaction_max_ms",
            "Maximum reaction delay (ms) before a DPS mimic swaps onto a newly called assist target; the actual delay rolls uniformly in [min, max]. Higher = looser/slower train. Default 1200.", 1200)]
        public static int MIMIC_ASSIST_REACTION_MAX_MS;

        [ServerProperty("npc", "mimic_healer_danger_radius",
            "If a hostile comes within this many units of a healer, the healer steps back to its safe seat even when otherwise idle. Default 350.", 350)]
        public static int MIMIC_HEALER_DANGER_RADIUS;

        [ServerProperty("npc", "mimic_group_player_siege",
            "When true (default), grouped mimics join the assault when their human group leader attacks an enemy keep/tower door — they help break the door, but always prioritise live enemies that show up (PvP) and resume the door once clear. Movement/target only.", true)]
        public static bool MIMIC_GROUP_PLAYER_SIEGE;

        [ServerProperty("npc", "mimic_group_chat_dedup_ms",
            "Cooldown (ms) during which the same chat topic stays silent for the rest of the group after one bot says it. Default 8000.", 8000)]
        public static int MIMIC_GROUP_CHAT_DEDUP_MS;

        [ServerProperty("npc", "mimic_linkdeath_grace_seconds",
            "Seconds an owner-bot is hibernated (kept alive but inactive) after the owner link-deaths before being deleted. 0 to delete immediately. Default 60.", 60)]
        public static int MIMIC_LINKDEATH_GRACE_SECONDS;

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
            "Heal % threshold below which mimic healers prioritise healing (default 80; previously 75 caused groups to die on normal mobs because heals started too late).", 80)]
        public static int MIMIC_HEAL_THRESHOLD;

        [ServerProperty("npc", "mimic_emergency_threshold",
            "Emergency heal % threshold for mimic healers (default 50, vs the generic 37).", 50)]
        public static int MIMIC_EMERGENCY_THRESHOLD;

        // Mana-conservation gate for healers. When the bot's own ManaPercent
        // drops below MIMIC_HEAL_MANA_STOP_PERCENT, the heal cycle suppresses
        // non-emergency cast-time heals so the healer can regen. It resumes
        // normal cadence only once ManaPercent climbs back above
        // MIMIC_HEAL_MANA_RESUME_PERCENT. Emergency (sub-EmergencyThreshold)
        // heals, instant heals, and cure mezz/disease/poison are NEVER gated:
        // letting the tank die at 5% to save 30 mana is never the right call.
        [ServerProperty("npc", "mimic_heal_mana_stop_percent",
            "Healer suppresses non-emergency cast-time heals when ManaPercent drops below this value. Default 25.", 25)]
        public static int MIMIC_HEAL_MANA_STOP_PERCENT;

        [ServerProperty("npc", "mimic_heal_mana_resume_percent",
            "Healer resumes normal heal cadence once ManaPercent climbs back above this value. Must be >= stop. Default 30.", 30)]
        public static int MIMIC_HEAL_MANA_RESUME_PERCENT;

        // Visual-only entity the bot drops at camp. The campfire is spawned
        // as a GameStaticItem (the historical OpenDAoC worldobject path)
        // using model 3460 — confirmed on-server to render as a proper
        // animated wood campfire on the live client. Model 2656 (the other
        // worldobject "Campfire" entry) shows up as a stone obelisk on at
        // least some client/world-data combos, so we avoid it by default.
        //
        // When mimic_campfire_use_npc=true, we spawn an inert GameNPC and
        // read the model against the MOB model space instead — useful for
        // animated mob fire effects (1686 "vte5 fire effect", 1822 "fire
        // effect", 911 "Bright Flame", 907 "torchlight").
        [ServerProperty("npc", "mimic_campfire_model",
            "Model used for the camp fire. Default 3460 = OpenDAoC large wood campfire (rendered as GameStaticItem). Alternatives: 2656 (small campfire, may render as obelisk on some clients), or mob models 1686/1822/911 (require mimic_campfire_use_npc=true).", 3460)]
        public static int MIMIC_CAMPFIRE_MODEL;

        [ServerProperty("npc", "mimic_campfire_use_npc",
            "When false (default), the camp fire is a GameStaticItem using a worldobject model (3460). When true, falls back to an inert GameNPC and the model is read against the mob model space — required for animated fire mob models (1686 etc.).", false)]
        public static bool MIMIC_CAMPFIRE_USE_NPC;

        // ----------------------------------------------------------------
        // Death and resurrection tuning.
        //
        // When a bot dies while grouped with a player, the corpse lingers
        // as a rez-able body. A group rezzer (Cleric/Druid/Friar/Bard/
        // Warden/Healer/Heretic/etc.) can target the corpse and cast
        // Resurrect on it like any player corpse — the bot auto-accepts.
        // If no rez arrives in time the bot is released "to bind", which
        // here means it leaves the group and despawns: bots have no
        // bindstone so the realistic equivalent of /release is to drop
        // the group, exactly like a player going to their bind point.
        // ----------------------------------------------------------------

        [ServerProperty("npc", "bot_rez_wait_seconds",
            "Seconds a dead bot lingers as a rez-able corpse when a group rezzer is alive (default 60).", 60)]
        public static int BOT_REZ_WAIT_SECONDS;

        [ServerProperty("npc", "bot_rez_wait_no_healer_seconds",
            "Seconds a dead bot lingers as a corpse when no group rezzer is available (default 30, was 15 — too short for distant rezzer).", 30)]
        public static int BOT_REZ_WAIT_NO_HEALER_SECONDS;

        [ServerProperty("npc", "bot_rez_timeout_behavior",
            "What happens to a bot whose rez timeout expired: 'release' (leave the group and despawn, like a player without a bind — default) or 'revive' (return at 50% next to the owner, pre-existing behaviour).",
            "release")]
        public static string BOT_REZ_TIMEOUT_BEHAVIOR;

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

                // Warn-once on tokens that don't parse as any known
                // eCharacterClass name. Without this, an operator who
                // types a typo (e.g. "Cleric,Frair,Druid") would never
                // know why their bot Friar doesn't get the healer
                // strategy — the matcher just silently skips the bad
                // entry on every spawn.
                if (!Enum.TryParse<eCharacterClass>(trimmed, ignoreCase: true, out _))
                {
                    if (_log.IsWarnEnabled && _warnedInvalidTokens.Add(trimmed))
                        _log.Warn($"MimicConfig: CSV token \"{trimmed}\" is not a known eCharacterClass name — check your bot_ai_v2_* server properties.");
                    continue;
                }

                if (string.Equals(trimmed, current, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
