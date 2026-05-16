using System;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.AutoMigrations
{
    /// <summary>
    /// Bumps ServerProperty rows that still carry a previous default to the new default
    /// shipped with the code. Mirrors the per-module migration used by EconomyManager
    /// but for the global ServerProperties.cs defaults we change so the operator never
    /// has to manually edit the DB after a code upgrade.
    ///
    /// Only rewrites a row whose current Value matches the OLD default exactly: any
    /// manual override the operator made stays untouched. Runs once at server start.
    /// </summary>
    public static class PropertyMigrations
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            // RA modernization
            TryBumpBool("use_new_passives_ras_scaling", oldDefault: false, newDefault: true);
            TryBumpBool("use_new_actives_ras_scaling",  oldDefault: false, newDefault: true);

            // Catacombs / PvE quality of life
            TryBumpBool("allow_cata_slash_level", oldDefault: false, newDefault: true);
            TryBumpBool("enable_pve_speed",       oldDefault: false, newDefault: true);
            TryBumpBool("enable_zone_bonuses",    oldDefault: false, newDefault: true);
            TryBumpBool("task_give_random_item",  oldDefault: false, newDefault: true);
        }

        private static void TryBumpBool(string key, bool oldDefault, bool newDefault)
        {
            if (oldDefault == newDefault)
                return;
            try
            {
                var row = GameServer.Database.FindObjectByKey<DbServerProperty>(key);
                if (row == null)
                    return;
                if (!bool.TryParse(row.Value, out bool current))
                    return;
                if (current != oldDefault)
                    return;
                row.Value = newDefault.ToString();
                row.DefaultValue = newDefault.ToString();
                GameServer.Database.SaveObject(row);
                if (log.IsInfoEnabled)
                    log.Info($"PropertyMigrations: auto-bumped {key} {oldDefault} -> {newDefault}.");
            }
            catch (Exception ex)
            {
                log.Warn($"PropertyMigrations: auto-bump of {key} failed: {ex.Message}");
            }
        }
    }
}
