#!/bin/sh

# Default values for UID and GID if not provided
APP_UID=${UID:-1000}
APP_GID=${GID:-1000}

# Check if running as root
if [ "$(id -u)" != "0" ]; then
    >&2 echo "ERROR: Not running as root. Please run as root, and pass HOST_UID and HOST_GID."
    exit 120
elif [ "${APP_UID}" = "" ]; then
    >&2 echo "ERROR: HOST_UID is not set. Please run as root, and pass HOST_UID and HOST_GID."
    exit 121
elif [ "${APP_GID}" = "" ]; then
    >&2 echo "ERROR: HOST_GID is not set. Please run as root, and pass HOST_UID and HOST_GID."
    exit 122
fi

# Handle existing group with the same GID
EXISTING_GROUP_NAME=$(getent group "${APP_GID}" | cut -d: -f1)
if [ "${EXISTING_GROUP_NAME}" != "" ]; then
    delgroup "${EXISTING_GROUP_NAME}"
fi

# (Re)create the group 'appgroup'
delgroup appgroup 2>/dev/null || true
addgroup -g "${APP_GID}" appgroup

# (Re)create the user 'appuser'
deluser appuser 2>/dev/null || true
adduser -D -H -u "${APP_UID}" -G appgroup appuser

# Delete the existing serverconfig.xml
rm -f /app/config/serverconfig.xml

# Create serverconfig.xml with environment variables
cat << EOF > /app/config/serverconfig.xml
<?xml version="1.0" encoding="utf-8"?>
<root>
    <Server>
        <Port>${SERVER_PORT}</Port>
        <IP>${SERVER_IP}</IP>
        <RegionIP>${REGION_IP}</RegionIP>
        <RegionPort>${REGION_PORT}</RegionPort>
        <UdpIP>${UDP_IP}</UdpIP>
        <UdpPort>${UDP_PORT}</UdpPort>
        <EnableUPnP>${ENABLE_UPNP}</EnableUPnP>
        <DetectRegionIP>${DETECT_REGION_IP}</DetectRegionIP>
        <ServerName>${SERVER_NAME}</ServerName>
        <ServerNameShort>${SERVER_NAME_SHORT}</ServerNameShort>
        <LogConfigFile>${LOG_CONFIG_FILE}</LogConfigFile>
        <ScriptCompilationTarget>${SCRIPT_COMPILATION_TARGET}</ScriptCompilationTarget>
        <ScriptAssemblies>${SCRIPT_ASSEMBLIES}</ScriptAssemblies>
        <EnableCompilation>${ENABLE_COMPILATION}</EnableCompilation>
        <AutoAccountCreation>${AUTO_ACCOUNT_CREATION}</AutoAccountCreation>
        <GameType>${GAME_TYPE}</GameType>
        <CheatLoggerName>${CHEAT_LOGGER_NAME}</CheatLoggerName>
        <GMActionLoggerName>${GM_ACTION_LOGGER_NAME}</GMActionLoggerName>
        <InvalidNamesFile>${INVALID_NAMES_FILE}</InvalidNamesFile>
        <DBType>${DB_TYPE}</DBType>
        <DBConnectionString>${DB_CONNECTION_STRING}</DBConnectionString>
        <DBAutosave>${DB_AUTOSAVE}</DBAutosave>
        <DBAutosaveInterval>${DB_AUTOSAVE_INTERVAL}</DBAutosaveInterval>
        <CpuUse>${CPU_USE}</CpuUse>
    </Server>
</root>
EOF

# Apply idempotent SQL patches against the live DB on every container start.
# This handles existing DBs where docker-entrypoint-initdb.d won't fire again.
# Parse DB host/user/password from DB_CONNECTION_STRING once and re-use.
DB_HOST=$(echo "${DB_CONNECTION_STRING}" | sed -n 's/.*server=\([^;]*\).*/\1/p')
DB_PORT=$(echo "${DB_CONNECTION_STRING}" | sed -n 's/.*port=\([^;]*\).*/\1/p')
DB_NAME=$(echo "${DB_CONNECTION_STRING}" | sed -n 's/.*database=\([^;]*\).*/\1/p')
DB_USER=$(echo "${DB_CONNECTION_STRING}" | sed -n 's/.*userid=\([^;]*\).*/\1/p')
DB_PASS=$(echo "${DB_CONNECTION_STRING}" | sed -n 's/.*password=\([^;]*\).*/\1/p')
DB_HOST=${DB_HOST:-db}
DB_PORT=${DB_PORT:-3306}
DB_NAME=${DB_NAME:-opendaoc}
DB_USER=${DB_USER:-root}

apply_sql_patch() {
    patch_file="$1"
    patch_label="$2"
    if [ -f "${patch_file}" ]; then
        echo "[entrypoint] Applying ${patch_label} to ${DB_HOST}:${DB_PORT}/${DB_NAME}..."
        if mariadb -h "${DB_HOST}" -P "${DB_PORT}" -u "${DB_USER}" -p"${DB_PASS}" "${DB_NAME}" < "${patch_file}"; then
            echo "[entrypoint] ${patch_label} applied successfully."
        else
            echo "[entrypoint] WARN: ${patch_label} apply failed — gameserver will start anyway."
        fi
    fi
}

apply_sql_patch /app/sql/heretic_live.sql       "Heretic Live SQL patch"
apply_sql_patch /app/sql/battlegrounds_live.sql "Battlegrounds Live SQL patch"

# --- Larogoth + Eve-of-Darkness migrations (one-shot, checksum-tracked) ---
# 5 files applied IN ORDER (numbering = dependency order):
#   10_larogoth_items.sql        strategy 3 — INSERT IGNORE missing items
#                                             (must run before 20/30 so they
#                                              can match these by Name+Realm).
#   20_larogoth_ext.sql          strategy 1 — extended metadata (delve_text).
#   30_larogoth_loot.sql         strategy 2 — ItemLootSource (raw drop info).
#   40_larogoth_loot_wiring.sql  strategy 4 — ItemLootSource → LootTemplate +
#                                             MobXLootTemplate + LootOTD so the
#                                             loot generators actually drop.
#   50_eveofdarkness_fill.sql    strategy 5 — Eve-of-Darkness/db-public bulk
#                                             import in 'fill the gaps' mode
#                                             (INSERT IGNORE everywhere, our DB
#                                             stays authoritative). ~85 MB, first
#                                             apply can take 5-15 min.
# Marker table LarogothMigrationState stores sha256 of each applied file so the
# heavy INSERTs run exactly once per (DB, file content) — re-runs are skipped
# silently. Re-applies automatically if a file's checksum changes (image rebuild
# with a newer Larogoth or Eve-of-Darkness release).
MARIADB_LAROGOTH_OPTS="--default-character-set=utf8mb3 --max_allowed_packet=128M"

mariadb ${MARIADB_LAROGOTH_OPTS} -h "${DB_HOST}" -P "${DB_PORT}" -u "${DB_USER}" -p"${DB_PASS}" "${DB_NAME}" \
    -e "CREATE TABLE IF NOT EXISTS LarogothMigrationState (
            Name VARCHAR(64) NOT NULL PRIMARY KEY,
            Checksum CHAR(64) NOT NULL,
            AppliedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            RowsAffected INT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;" >/dev/null 2>&1 \
    || echo "[entrypoint] WARN: could not create LarogothMigrationState table — Larogoth migrations skipped." >&2

apply_larogoth_migration() {
    file="$1"
    # Optional 2nd arg: "force" -> pass --force to mariadb so it continues past
    # per-statement errors (used for Eve-of-Darkness fill where column-count
    # drift between vanilla DOL and OpenDAoC is expected on some tables).
    extra_flag=""
    if [ "$2" = "force" ]; then
        extra_flag="--force"
    fi
    name=$(basename "${file}")
    if [ ! -f "${file}" ]; then
        return 0
    fi
    new_sum=$(sha256sum "${file}" | awk '{print $1}')
    cur_sum=$(mariadb ${MARIADB_LAROGOTH_OPTS} -N -B -h "${DB_HOST}" -P "${DB_PORT}" -u "${DB_USER}" -p"${DB_PASS}" "${DB_NAME}" \
        -e "SELECT Checksum FROM LarogothMigrationState WHERE Name='${name}';" 2>/dev/null || true)
    if [ "${cur_sum}" = "${new_sum}" ]; then
        echo "[entrypoint] Larogoth ${name} already applied (sha256 match) — skipped."
        return 0
    fi
    echo "[entrypoint] Applying Larogoth ${name} (this may take a minute)..."
    if mariadb ${MARIADB_LAROGOTH_OPTS} ${extra_flag} -h "${DB_HOST}" -P "${DB_PORT}" -u "${DB_USER}" -p"${DB_PASS}" "${DB_NAME}" < "${file}"; then
        mariadb ${MARIADB_LAROGOTH_OPTS} -h "${DB_HOST}" -P "${DB_PORT}" -u "${DB_USER}" -p"${DB_PASS}" "${DB_NAME}" \
            -e "REPLACE INTO LarogothMigrationState (Name, Checksum, AppliedAt) VALUES ('${name}', '${new_sum}', NOW());" \
            >/dev/null 2>&1
        echo "[entrypoint] Larogoth ${name} applied (sha256=${new_sum})."
    else
        echo "[entrypoint] WARN: Larogoth ${name} apply failed — gameserver will start anyway." >&2
    fi
}

# Order matters: items first (so 20/30 can match the new rows by Name+Realm),
# then ext/loot (both join on ItemTemplate), then wiring (joins on ItemLootSource),
# finally Eve-of-Darkness bulk fill last so it doesn't overwrite anything above.
apply_larogoth_migration /app/sql/larogoth/10_larogoth_items.sql
apply_larogoth_migration /app/sql/larogoth/20_larogoth_ext.sql
apply_larogoth_migration /app/sql/larogoth/30_larogoth_loot.sql
apply_larogoth_migration /app/sql/larogoth/40_larogoth_loot_wiring.sql
apply_larogoth_migration /app/sql/larogoth/50_eveofdarkness_fill.sql force

# Change ownership of the /app directory
chown -R appuser:appgroup /app

# Switch to the non-root user and start the server
exec su-exec appuser sh -c "cd /app && dotnet CoreServer.dll"
