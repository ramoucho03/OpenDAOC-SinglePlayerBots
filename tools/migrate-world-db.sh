#!/usr/bin/env bash
# ===============================================================================
# migrate-world-db.sh
#
# Replace the world-content tables of an OpenDAOC database with the latest
# OpenDAoC-Database (master), while preserving all player-side data
# (characters, inventories, accounts, guilds, houses, server properties, ...).
#
# Strategy
#   1. Sanity checks (tools, connection, server stopped).
#   2. Full mysqldump of the current database (timestamped, kept forever).
#   3. Dump of preserved tables only (data-only, INSERT statements).
#   4. git clone of OpenDAoC/OpenDAoC-Database into a temp dir.
#   5. DROP + CREATE of the database.
#   6. Import of the full world dump (all .sql in opendaoc-db-core/).
#   7. TRUNCATE + re-import of preserved tables from step 3.
#   8. Orphan report: Inventory rows whose ITemplate_Id / UTemplate_Id no
#      longer resolve. Items are NOT deleted, only logged.
#
# Requirements: bash, git, mysql, mysqldump in PATH.
# ===============================================================================

set -Eeuo pipefail

# ------------------------------- Defaults --------------------------------------
#
# WARNING: credentials are baked into this file for convenience. Do NOT commit
# this script to a public repo as-is. If you do, override with --pass / --user
# / --container and clear the defaults below, or move them to an env file
# sourced before running.

DB_NAME="opendaoc"
DB_USER="root"
DB_PASS="my-secret-pw"
DB_HOST="localhost"          # only used when --no-docker
DB_PORT="3306"               # only used when --no-docker

# Docker mode is the default since the DB runs inside a container.
DOCKER_CONTAINER="opendaoc-db"
DOCKER_CMD="sudo docker"     # set to "docker" if your user is in the docker group
USE_DOCKER="yes"

# Resolved at runtime by detect_container_binaries() (modern vs legacy MariaDB).
CONTAINER_CLIENT=""          # "mariadb" or "mysql"
CONTAINER_DUMP=""            # "mariadb-dump" or "mysqldump"

ASSUME_YES="no"

BACKUP_DIR="${BACKUP_DIR:-./db-backups}"
REPO_URL="https://github.com/OpenDAoC/OpenDAoC-Database.git"
REPO_BRANCH="master"
REPO_SUBDIR="opendaoc-db-core"

TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
WORK_DIR=""

# Tables to PRESERVE across the migration (player & operator data).
# Anything NOT in this list is considered world content and will be replaced
# wholesale by the OpenDAoC-Database dump.
PRESERVED_TABLES=(
  Account
  AccountXCrafting
  AccountXCustomParam
  AccountXMoney
  DOLCharacters
  DOLCharactersBackup
  DOLCharactersXCustomParam
  DOLCharactersBackupXCustomParam
  Inventory
  ItemUnique
  CharacterXMasterLevel
  CharacterXDataQuest
  CharacterXOneTimeDrop
  Guild
  GuildAlliance
  GuildRank
  DbHouse
  DbHouseCharsXPerms
  DbHousePermissions
  DbIndoorItem
  DbOutdoorItem
  HouseConsignmentMerchant
  HouseHookpointItem
  PlayerBoats
  PlayerXEffect
  PlayerInfo
  Appeal
  BugReport
  AuditEntry
  ServerProperty
)

# ------------------------------ Helpers ----------------------------------------

color()    { printf '\033[%sm%s\033[0m\n' "$1" "$2"; }
# Logs always go to stderr so functions that return values via stdout
# (e.g. `backup_full`, `clone_new_db`) can be captured cleanly with $(...).
log()      { printf '\033[1;36m[migrate] %s\033[0m\n' "$*" >&2; }
warn()     { printf '\033[1;33m[migrate] WARN: %s\033[0m\n' "$*" >&2; }
err()      { printf '\033[1;31m[migrate] ERROR: %s\033[0m\n' "$*" >&2; }
die()      { err "$*"; exit 1; }

usage() {
  cat <<EOF
Usage: $0 [options]

Defaults assume Docker mode against container '$DOCKER_CONTAINER' with the
credentials hardcoded at the top of this script.

Options:
  --db NAME          Database name              (default: $DB_NAME)
  --user USER        MySQL user                 (default: $DB_USER)
  --pass PASS        MySQL password             (default: hardcoded)
  --container NAME   Docker container name      (default: $DOCKER_CONTAINER)
  --docker-cmd CMD   Docker command prefix      (default: "$DOCKER_CMD")
  --no-docker        Use mysql/mysqldump on host instead of docker exec
  --host HOST        MySQL host (no-docker)     (default: $DB_HOST)
  --port PORT        MySQL port (no-docker)     (default: $DB_PORT)
  --backup-dir D     Backup directory           (default: $BACKUP_DIR)
  --branch B         OpenDAoC-Database branch   (default: $REPO_BRANCH)
  -y, --yes          Skip all confirmations     (DANGEROUS)
  -h, --help         Show this help

Examples:
  # Docker mode, all defaults (typical case)
  $0

  # Host mode (mysql client on host)
  $0 --no-docker --host 127.0.0.1
EOF
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --db)          DB_NAME="$2";          shift 2 ;;
      --user)        DB_USER="$2";          shift 2 ;;
      --pass)        DB_PASS="$2";          shift 2 ;;
      --container)   DOCKER_CONTAINER="$2"; shift 2 ;;
      --docker-cmd)  DOCKER_CMD="$2";       shift 2 ;;
      --no-docker)   USE_DOCKER="no";       shift ;;
      --host)        DB_HOST="$2";          shift 2 ;;
      --port)        DB_PORT="$2";          shift 2 ;;
      --backup-dir)  BACKUP_DIR="$2";       shift 2 ;;
      --branch)      REPO_BRANCH="$2";      shift 2 ;;
      -y|--yes)      ASSUME_YES="yes";      shift ;;
      -h|--help)     usage; exit 0 ;;
      *) die "Unknown argument: $1 (use --help)" ;;
    esac
  done
}

confirm() {
  # confirm "Prompt question"
  local prompt="$1"
  if [[ "$ASSUME_YES" == "yes" ]]; then
    log "$prompt -> auto-yes"
    return 0
  fi
  read -r -p "$(color '1;35' "$prompt [y/N] ") " ans
  [[ "$ans" =~ ^[yY]$ ]]
}

cleanup() {
  if [[ -n "$WORK_DIR" && -d "$WORK_DIR" ]]; then
    log "Cleaning temp dir $WORK_DIR"
    rm -rf "$WORK_DIR"
  fi
}
trap cleanup EXIT

# mysql / mysqldump wrappers.
# Docker mode: shell out to `docker exec`, pass password via -e MYSQL_PWD so it
# never appears in argv / ps. Container is expected to have `mariadb` and
# `mariadb-dump` (modern MariaDB) or `mysql` / `mysqldump` (legacy) binaries.
#
# Host mode: same env-var trick, against mysql/mysqldump on the host.
my_mysql() {
  if [[ "$USE_DOCKER" == "yes" ]]; then
    $DOCKER_CMD exec -i -e MYSQL_PWD="$DB_PASS" "$DOCKER_CONTAINER" \
      "$CONTAINER_CLIENT" -u "$DB_USER" "$@"
  else
    MYSQL_PWD="$DB_PASS" mysql -h "$DB_HOST" -P "$DB_PORT" -u "$DB_USER" "$@"
  fi
}

my_mysqldump() {
  if [[ "$USE_DOCKER" == "yes" ]]; then
    $DOCKER_CMD exec -e MYSQL_PWD="$DB_PASS" "$DOCKER_CONTAINER" \
      "$CONTAINER_DUMP" -u "$DB_USER" "$@"
  else
    MYSQL_PWD="$DB_PASS" mysqldump -h "$DB_HOST" -P "$DB_PORT" -u "$DB_USER" "$@"
  fi
}

detect_container_binaries() {
  # Pick whichever client / dump binary exists in the container, modern first.
  for c in mariadb mysql; do
    if $DOCKER_CMD exec "$DOCKER_CONTAINER" sh -c "command -v $c" >/dev/null 2>&1; then
      CONTAINER_CLIENT="$c"; break
    fi
  done
  for d in mariadb-dump mysqldump; do
    if $DOCKER_CMD exec "$DOCKER_CONTAINER" sh -c "command -v $d" >/dev/null 2>&1; then
      CONTAINER_DUMP="$d"; break
    fi
  done
  [[ -n "$CONTAINER_CLIENT" ]] || die "Neither 'mariadb' nor 'mysql' found in container '$DOCKER_CONTAINER'."
  [[ -n "$CONTAINER_DUMP" ]]   || die "Neither 'mariadb-dump' nor 'mysqldump' found in container '$DOCKER_CONTAINER'."
  log "Container binaries: client=$CONTAINER_CLIENT, dump=$CONTAINER_DUMP"
}

# ------------------------------ Steps ------------------------------------------

require_tools() {
  command -v git >/dev/null 2>&1 || die "Required tool not in PATH: git"

  if [[ "$USE_DOCKER" == "yes" ]]; then
    # Probe the docker command (which may be "sudo docker", "docker", etc.).
    $DOCKER_CMD version >/dev/null 2>&1 \
      || die "Cannot run '$DOCKER_CMD version'. Adjust --docker-cmd or install Docker."
    $DOCKER_CMD inspect "$DOCKER_CONTAINER" >/dev/null 2>&1 \
      || die "Docker container '$DOCKER_CONTAINER' not found. Is it running?"
  else
    for t in mysql mysqldump; do
      command -v "$t" >/dev/null 2>&1 || die "Required tool not in PATH: $t"
    done
  fi
}

prompt_password() {
  # Password is hardcoded at the top of the script. Only prompt if the user
  # explicitly cleared it via --pass "".
  if [[ -z "$DB_PASS" ]]; then
    read -r -s -p "$(color '1;35' "MySQL password for $DB_USER: ")" DB_PASS
    echo
  fi
}

test_connection() {
  if [[ "$USE_DOCKER" == "yes" ]]; then
    log "Testing connection: $DOCKER_CMD exec $DOCKER_CONTAINER mariadb -u$DB_USER -> $DB_NAME"
  else
    log "Testing connection to $DB_USER@$DB_HOST:$DB_PORT/$DB_NAME"
  fi
  my_mysql -e "USE \`$DB_NAME\`; SELECT 1;" >/dev/null \
    || die "Cannot connect or database '$DB_NAME' does not exist."
}

confirm_server_stopped() {
  warn "The OpenDAOC GameServer MUST be stopped before continuing."
  warn "Otherwise its 10-min autosave will overwrite this migration on next logout."
  confirm "Is the GameServer process stopped?" || die "Aborted. Stop the server then retry."
}

backup_full() {
  mkdir -p "$BACKUP_DIR"
  local f="$BACKUP_DIR/${DB_NAME}-full-${TIMESTAMP}.sql"
  log "Full backup -> $f"
  my_mysqldump \
    --single-transaction --routines --triggers --events \
    --default-character-set=utf8mb4 \
    "$DB_NAME" > "$f"
  log "Backup size: $(du -h "$f" | awk '{print $1}')"
  echo "$f"
}

list_existing_tables() {
  my_mysql -N -B -e "SHOW TABLES FROM \`$DB_NAME\`;" | tr -d '\r'
}

dump_preserved_tables() {
  # Dump only data (no DROP/CREATE) for tables that exist in the current DB.
  local existing
  existing="$(list_existing_tables)"

  local to_dump=()
  for t in "${PRESERVED_TABLES[@]}"; do
    if grep -qix -- "$t" <<<"$existing"; then
      to_dump+=("$t")
    else
      warn "Preserved table '$t' does not exist in current DB, skipping."
    fi
  done

  if [[ ${#to_dump[@]} -eq 0 ]]; then
    die "No preserved tables found in current DB. Is '$DB_NAME' really an OpenDAOC DB?"
  fi

  local f="$WORK_DIR/preserved-data.sql"
  log "Dumping ${#to_dump[@]} preserved tables -> $f"
  my_mysqldump \
    --single-transaction \
    --no-create-info \
    --skip-add-drop-table \
    --skip-comments \
    --default-character-set=utf8mb4 \
    --hex-blob \
    "$DB_NAME" "${to_dump[@]}" > "$f"

  # Store the list for later TRUNCATE step.
  printf '%s\n' "${to_dump[@]}" > "$WORK_DIR/preserved-tables.txt"
  log "Preserved dump size: $(du -h "$f" | awk '{print $1}')"
}

clone_new_db() {
  local dest="$WORK_DIR/OpenDAoC-Database"
  log "Cloning $REPO_URL (branch: $REPO_BRANCH)"
  git clone --depth 1 --branch "$REPO_BRANCH" "$REPO_URL" "$dest" >/dev/null

  local subdir="$dest/$REPO_SUBDIR"
  [[ -d "$subdir" ]] || die "Cloned repo missing '$REPO_SUBDIR' directory."

  local n
  n="$(find "$subdir" -maxdepth 1 -name '*.sql' | wc -l)"
  [[ "$n" -gt 50 ]] || die "Only $n .sql files in $subdir, expected 100+. Aborting."
  log "Cloned OK, $n SQL files."
  echo "$subdir"
}

final_confirmation() {
  local backup_file="$1"
  cat <<EOF

$(color '1;31' '!!! DESTRUCTIVE STEP !!!')

About to:
  - DROP DATABASE   \`$DB_NAME\`
  - CREATE DATABASE \`$DB_NAME\`
  - Import world content from OpenDAoC-Database master (~80 MB)
  - TRUNCATE then re-import these preserved tables:
$(printf '      - %s\n' "${PRESERVED_TABLES[@]}")

Full backup is at:
  $backup_file

To restore manually if anything goes wrong:
  mysql -u $DB_USER -p $DB_NAME < $backup_file

EOF
  confirm "Proceed with destructive migration?" \
    || die "Aborted by user. Backup kept at $backup_file"
}

drop_and_recreate() {
  log "Dropping and recreating database '$DB_NAME'"
  my_mysql -e "
    DROP DATABASE IF EXISTS \`$DB_NAME\`;
    CREATE DATABASE \`$DB_NAME\` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
  "
}

import_world() {
  local subdir="$1"
  local combined="$WORK_DIR/world-combined.sql"
  log "Combining world SQL files"
  # Cat in a deterministic order; tables are independent here (data-only files
  # mostly; CREATE TABLEs included). FK checks disabled to handle any order.
  {
    echo 'SET FOREIGN_KEY_CHECKS=0;'
    echo 'SET UNIQUE_CHECKS=0;'
    echo 'SET autocommit=0;'
    cat "$subdir"/*.sql
    echo 'COMMIT;'
    echo 'SET FOREIGN_KEY_CHECKS=1;'
    echo 'SET UNIQUE_CHECKS=1;'
  } > "$combined"
  log "World dump size: $(du -h "$combined" | awk '{print $1}')"

  log "Importing world dump into '$DB_NAME' (may take a few minutes)"
  my_mysql --default-character-set=utf8mb4 "$DB_NAME" < "$combined"
  log "World import complete."
}

restore_preserved() {
  local preserved_data="$WORK_DIR/preserved-data.sql"
  local preserved_tables="$WORK_DIR/preserved-tables.txt"

  log "Truncating preserved tables in new schema (to clear sample data from repo)"
  {
    echo 'SET FOREIGN_KEY_CHECKS=0;'
    while read -r t; do
      # Some preserved tables may not exist in the new schema (renames, removals).
      # Skip them silently here; the INSERT phase will surface real issues.
      echo "TRUNCATE TABLE \`$t\`;"
    done < "$preserved_tables"
    echo 'SET FOREIGN_KEY_CHECKS=1;'
  } | my_mysql --force "$DB_NAME" 2>&1 | grep -vE "^(mysql:|ERROR 1146)" || true

  log "Re-importing preserved player data"
  {
    echo 'SET FOREIGN_KEY_CHECKS=0;'
    echo 'SET UNIQUE_CHECKS=0;'
    cat "$preserved_data"
    echo 'SET FOREIGN_KEY_CHECKS=1;'
    echo 'SET UNIQUE_CHECKS=1;'
  } | my_mysql --default-character-set=utf8mb4 "$DB_NAME"
  log "Preserved data restored."
}

orphan_report() {
  log "Building orphan-items report"
  local report="$BACKUP_DIR/${DB_NAME}-orphan-items-${TIMESTAMP}.txt"
  {
    echo "=== Orphan-items report for $DB_NAME @ $TIMESTAMP ==="
    echo
    echo "## Inventory rows whose ITemplate_Id is missing from ItemTemplate"
    echo "## (ItemUnique-backed items are not counted here; they survive standalone.)"
    echo
    my_mysql -B -e "
      SELECT
        i.ITemplate_Id AS missing_template,
        COUNT(*)       AS count_in_inventory,
        GROUP_CONCAT(DISTINCT c.Name ORDER BY c.Name SEPARATOR ', ') AS affected_chars
      FROM Inventory i
      LEFT JOIN ItemTemplate it
        ON it.Id_nb = i.ITemplate_Id
      LEFT JOIN DOLCharacters c
        ON c.ObjectId = i.OwnerID
      WHERE i.ITemplate_Id IS NOT NULL
        AND i.ITemplate_Id <> ''
        AND it.Id_nb IS NULL
      GROUP BY i.ITemplate_Id
      ORDER BY count_in_inventory DESC, missing_template;
    " "$DB_NAME"
    echo
    echo "## Inventory rows whose UTemplate_Id is missing from ItemUnique"
    echo "## (should normally be 0 since ItemUnique is preserved.)"
    echo
    my_mysql -B -e "
      SELECT
        i.UTemplate_Id AS missing_unique,
        COUNT(*)       AS count_in_inventory
      FROM Inventory i
      LEFT JOIN ItemUnique iu
        ON iu.Id_nb = i.UTemplate_Id
      WHERE i.UTemplate_Id IS NOT NULL
        AND i.UTemplate_Id <> ''
        AND iu.Id_nb IS NULL
      GROUP BY i.UTemplate_Id
      ORDER BY count_in_inventory DESC, missing_unique;
    " "$DB_NAME"
  } > "$report"

  log "Orphan report: $report"

  local missing_tpl
  missing_tpl="$(grep -vE '^(missing_template|##|$|===)' "$report" | wc -l || true)"
  if [[ "$missing_tpl" -gt 0 ]]; then
    warn "$missing_tpl distinct ItemTemplate IDs missing from new world DB (items remain in inventory but will display as unknown)."
    warn "See $report for the full list."
  else
    log "No orphan items."
  fi
}

sanity_check() {
  log "Post-migration sanity check"
  my_mysql -t -e "
    SELECT 'characters' AS what, COUNT(*) AS n FROM \`$DB_NAME\`.DOLCharacters
    UNION ALL SELECT 'accounts',   COUNT(*) FROM \`$DB_NAME\`.Account
    UNION ALL SELECT 'inventory',  COUNT(*) FROM \`$DB_NAME\`.Inventory
    UNION ALL SELECT 'itemtemplate', COUNT(*) FROM \`$DB_NAME\`.ItemTemplate
    UNION ALL SELECT 'mob',        COUNT(*) FROM \`$DB_NAME\`.Mob
    UNION ALL SELECT 'regions',    COUNT(*) FROM \`$DB_NAME\`.Regions
    UNION ALL SELECT 'guilds',     COUNT(*) FROM \`$DB_NAME\`.Guild
    UNION ALL SELECT 'houses',     COUNT(*) FROM \`$DB_NAME\`.DbHouse;
  "
}

# ------------------------------- Main ------------------------------------------

main() {
  parse_args "$@"
  require_tools
  [[ "$USE_DOCKER" == "yes" ]] && detect_container_binaries
  prompt_password
  test_connection
  confirm_server_stopped

  WORK_DIR="$(mktemp -d -t opendaoc-migrate-XXXXXX)"
  log "Work dir: $WORK_DIR"

  local backup_file
  backup_file="$(backup_full)"

  dump_preserved_tables

  local subdir
  subdir="$(clone_new_db)"

  final_confirmation "$backup_file"

  drop_and_recreate
  import_world "$subdir"
  restore_preserved
  sanity_check
  orphan_report

  log "Migration complete. Restart the GameServer."
  log "Full backup kept at: $backup_file"
}

main "$@"
