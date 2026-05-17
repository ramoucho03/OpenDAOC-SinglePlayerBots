using System;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.AutoMigrations
{
    /// <summary>
    /// Auto-migrates ServerProperty DB rows whose value still equals an outdated default
    /// shipped by the OpenDAOC base, when this single-player fork wants a different default.
    ///
    /// Pattern mirrors EconomyManager.AutoMigrateDefaults: each entry lists the prior
    /// default(s) we want to catch, plus the new default. Rows that any operator has
    /// manually set to a non-default value are preserved untouched. Runs once at server
    /// start, no-op after the first successful migration because the row no longer matches
    /// any listed old default.
    ///
    /// Also pushes the new value into the in-memory static so the running process picks
    /// it up immediately without a property reload.
    /// </summary>
    public static class PropertyMigrations
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                AutoMigrateDefaults();
            }
            catch (Exception ex)
            {
                log.Error("PropertyMigrations: auto-migration pass failed.", ex);
            }
        }

        private static void AutoMigrateDefaults()
        {
            // TOA activation: Atlantis teleporter gate. Base OpenDAOC ships plvl=2 which
            // blocks regular players (priv 1) from any Atlantis teleporter destination.
            // Single-player fork wants this open to everyone, so flip 2 -> 1.
            TryMigrateIntMulti("atlantis_teleport_plvl", newDefault: 1, 2);

            // BG activation: bounty points were suppressed inside battleground zones.
            // Single-player fork wants BG kills to grant BPs so progression matters.
            TryMigrateBool("allow_bps_in_bgs", newDefault: true, oldDefault: false);

            // BG activation: allow solo/bot players to claim BG keeps (Killaloe,
            // Thidranki, Caledonia, etc.) the same way they can in NF.
            TryMigrateBool("allow_bg_claim", newDefault: true, oldDefault: false);

            // Housing rent: base OpenDAOC ships rent_due_days=7. Solo-player fork wants
            // rent disabled by default so unattended houses don't get repossessed during
            // long single-player breaks. Flip 7 -> 0 ("never due"); operators who set
            // any other value (e.g. 1, 3, 14) are preserved untouched.
            TryMigrateIntMulti("rent_due_days", newDefault: 0, 7);

            // Player vault: base OpenDAOC ships allow_vault_command=false, which forces
            // the player to physically visit a vault keeper / house vault. Solo-player
            // fork wants /vault accessible anywhere. Flip false -> true.
            TryMigrateBool("allow_vault_command", newDefault: true, oldDefault: false);

            // NF (New Frontiers) activation: base OpenDAOC ships these RvR/keep/relic
            // gates disabled. Single-player fork wants the full NF experience, so flip
            // each prior-default 'false' row to 'true'. Operators who set 'true' or any
            // other value are preserved.

            // NF keep upgrade timer: slowly raises keep level while a guild holds it.
            TryMigrateBool("enable_keep_upgrade_timer", newDefault: true, oldDefault: false);
            // NF towers can be climbed via the ladder hookpoint.
            TryMigrateBool("allow_tower_climb", newDefault: true, oldDefault: false);
            // WarMap manager: powers /warmap RvR display and kill icons.
            TryMigrateBool("enable_warmapmgr", newDefault: true, oldDefault: false);
            // Minotaur relic system: post-DR relic content.
            TryMigrateBool("enable_minotaur_relics", newDefault: true, oldDefault: false);

            // Solo claim: drop claim group-size from 8 -> 1 so a single player (the use
            // case of this fork) can claim/own a keep or tower.
            TryMigrateIntMulti("claim_num", newDefault: 1, 8);

            // Crafting expansion activation: this single-player fork wants the full
            // crafting ecosystem usable across all realms by a single account, since the
            // player owns characters in every realm.

            // Cross-realm crafted items: lets a crafter produce realm=0 items usable by
            // any realm. Base OpenDAOC default is false; flip false -> true.
            TryMigrateBool("allow_craft_norealm_items", newDefault: true, oldDefault: false);

            // Salvage per realm: when enabled, salvaged materials come back as the
            // realm-specific material useful to the salvager's realm instead of the
            // original raw. Base default false; flip false -> true.
            TryMigrateBool("use_salvage_per_realm", newDefault: true, oldDefault: false);

            // DR (Darkness Rising) activation: open Darkness Falls to all three realms
            // simultaneously instead of gating entry on tower/keep ownership. This
            // mirrors the DR-era live behavior so the single-player fork's bot
            // population can always reach DF regardless of relic state. Flip
            // false -> true; operators who have explicitly disabled it stay untouched.
            TryMigrateBool("allow_all_realms_df", newDefault: true, oldDefault: false);

            // Realm Abilities full ladder activation: enable the post-1.108 passive
            // 9-tier scaling (Toughness, MoP, Avoidance of Magic, Aug-stat lines, etc.)
            // and the post-1.108 active 5-tier scaling (Purge, Ignore Pain, MoC,
            // Speed of Sound, Charge, Vanish, etc.). Base OpenDAOC ships both as
            // false (pre-1.108 scaling, shorter ladder). The SP-bot fork wants the
            // full modern RA ladder so RR progression matters; flip false -> true.
            // Operators who explicitly set 'true' or any other value are preserved.
            TryMigrateBool("use_new_passives_ras_scaling", newDefault: true, oldDefault: false);
            TryMigrateBool("use_new_actives_ras_scaling", newDefault: true, oldDefault: false);
        }

        private static void TryMigrateIntMulti(string key, int newDefault, params int[] oldDefaults)
        {
            try
            {
                var row = GameServer.Database.FindObjectByKey<DbServerProperty>(key);
                if (row == null)
                    return;
                if (!int.TryParse(row.Value, out int currentValue))
                    return;

                bool matchesOldDefault = false;
                for (int i = 0; i < oldDefaults.Length; i++)
                {
                    if (currentValue == oldDefaults[i])
                    {
                        matchesOldDefault = true;
                        break;
                    }
                }
                if (!matchesOldDefault || currentValue == newDefault)
                    return;

                row.Value = newDefault.ToString();
                row.DefaultValue = newDefault.ToString();
                GameServer.Database.SaveObject(row);
                ApplyToStatic(key, newDefault);
                if (log.IsInfoEnabled)
                    log.Info($"PropertyMigrations: auto-migrated server property {key} from {currentValue} to {newDefault}.");
            }
            catch (Exception ex)
            {
                log.Warn($"PropertyMigrations: auto-migration of {key} failed: {ex.Message}");
            }
        }

        private static void TryMigrateBool(string key, bool newDefault, bool oldDefault)
        {
            try
            {
                var row = GameServer.Database.FindObjectByKey<DbServerProperty>(key);
                if (row == null)
                    return;
                if (!bool.TryParse(row.Value, out bool currentValue))
                    return;

                if (currentValue != oldDefault || currentValue == newDefault)
                    return;

                row.Value = newDefault.ToString();
                row.DefaultValue = newDefault.ToString();
                GameServer.Database.SaveObject(row);
                ApplyBoolToStatic(key, newDefault);
                if (log.IsInfoEnabled)
                    log.Info($"PropertyMigrations: auto-migrated server property {key} from {currentValue} to {newDefault}.");
            }
            catch (Exception ex)
            {
                log.Warn($"PropertyMigrations: auto-migration of {key} failed: {ex.Message}");
            }
        }

        // Push migrated value into the live static so the running process sees it without
        // a property reload. Switch-by-name keeps this colocated with the migration list.
        private static void ApplyToStatic(string key, int value)
        {
            switch (key)
            {
                case "atlantis_teleport_plvl":
                    ServerProperties.Properties.ATLANTIS_TELEPORT_PLVL = value;
                    break;
                case "rent_due_days":
                    ServerProperties.Properties.RENT_DUE_DAYS = value;
                    break;
                case "claim_num":
                    ServerProperties.Properties.CLAIM_NUM = value;
                    break;
            }
        }

        private static void ApplyBoolToStatic(string key, bool value)
        {
            switch (key)
            {
                case "allow_bps_in_bgs":
                    ServerProperties.Properties.ALLOW_BPS_IN_BGS = value;
                    break;
                case "allow_bg_claim":
                    ServerProperties.Properties.ALLOW_BG_CLAIM = value;
                    break;
                case "allow_vault_command":
                    ServerProperties.Properties.ALLOW_VAULT_COMMAND = value;
                    break;
                case "enable_keep_upgrade_timer":
                    ServerProperties.Properties.ENABLE_KEEP_UPGRADE_TIMER = value;
                    break;
                case "allow_tower_climb":
                    ServerProperties.Properties.ALLOW_TOWER_CLIMB = value;
                    break;
                case "enable_warmapmgr":
                    ServerProperties.Properties.ENABLE_WARMAPMGR = value;
                    break;
                case "enable_minotaur_relics":
                    ServerProperties.Properties.ENABLE_MINOTAUR_RELICS = value;
                    break;
                case "allow_craft_norealm_items":
                    ServerProperties.Properties.ALLOW_CRAFT_NOREALM_ITEMS = value;
                    break;
                case "use_salvage_per_realm":
                    ServerProperties.Properties.USE_SALVAGE_PER_REALM = value;
                    break;
                case "allow_all_realms_df":
                    ServerProperties.Properties.ALLOW_ALL_REALMS_DF = value;
                    break;
                case "use_new_passives_ras_scaling":
                    ServerProperties.Properties.USE_NEW_PASSIVES_RAS_SCALING = value;
                    break;
                case "use_new_actives_ras_scaling":
                    ServerProperties.Properties.USE_NEW_ACTIVES_RAS_SCALING = value;
                    break;
            }
        }
    }
}
