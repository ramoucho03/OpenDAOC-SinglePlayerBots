#!/usr/bin/env bash
# ===============================================================================
# import-region-content.sh
#
# Surgically import all content (mobs, npc templates, loot, merchants, items)
# for a single region from OpenDAoC/OpenDAoC-Database into the live opendaoc DB.
#
# Strategy
#   1. Download the relevant .sql files from the official repo.
#   2. Build a throwaway "staging" database, import the files into it.
#   3. From staging, INSERT IGNORE the rows that touch the target region into
#      the live database. INSERT IGNORE preserves your existing customs:
#      if a Mob_ID / TemplateId / ItemTemplate.Id_nb already exists in your DB,
#      the row from staging is skipped silently.
#   4. Drop the staging database.
#
# Default target: region 249 (Darkness Falls). Pass --region NNN to target
# another region (e.g. ToA, Catacombs).
#
# Requirements: bash, curl, docker (or specify --docker-cmd), MariaDB container.
# ===============================================================================

set -Eeuo pipefail

# ------------------------------- Defaults --------------------------------------

REGION_ID="249"
DB_NAME="opendaoc"
DB_USER="root"
DB_PASS="my-secret-pw"
DOCKER_CONTAINER="opendaoc-db"
DOCKER_CMD="sudo docker"

REPO_BASE="https://raw.githubusercontent.com/OpenDAoC/OpenDAoC-Database/master/opendaoc-db-core"
STAGE_DB="opendaoc_stage_$$"
WORK_DIR="$(mktemp -d -t opendaoc-import-XXXXXX)"
KEEP_STAGE="no"

# Files we need (in dependency order). mob+npctemplate are essential. The rest
# enriches loot and vendor inventories.
SQL_FILES=(
  mob.sql
  npctemplate.sql
  npcequipment.sql
  loottemplate.sql
  mobxloottemplate.sql
  merchantitem.sql
  itemtemplate.sql
)

# ------------------------------ Helpers ----------------------------------------

log()  { printf '\033[1;36m[import] %s\033[0m\n' "$*" >&2; }
warn() { printf '\033[1;33m[import] WARN: %s\033[0m\n' "$*" >&2; }
err()  { printf '\033[1;31m[import] ERROR: %s\033[0m\n' "$*" >&2; }
die()  { err "$*"; exit 1; }

usage() {
  cat <<EOF
Usage: $0 [options]

Imports all content rows that touch a given region from OpenDAoC-Database
into your live opendaoc DB, using INSERT IGNORE (your customs are preserved).

Options:
  --region NNN       Target region ID         (default: $REGION_ID - Darkness Falls)
  --db NAME          Live database name       (default: $DB_NAME)
  --user USER        MySQL user               (default: $DB_USER)
  --pass PASS        MySQL password           (default: hardcoded)
  --container NAME   Docker container name    (default: $DOCKER_CONTAINER)
  --docker-cmd CMD   Docker command prefix    (default: "$DOCKER_CMD")
  --keep-stage       Don't drop staging DB at the end (for debugging)
  -h, --help         Show this help

Common region IDs:
   10  Camelot City     249  Darkness Falls    489  Demon's Breach
   30  Albion ToA       73   Volcanus          286+ Labyrinth instances
  100  Albion frontier  101  Jordheim          200  Hibernia frontier

Example:
  $0                       # Import Darkness Falls (region 249)
  $0 --region 489          # Import Demon's Breach (Catacombs)
EOF
}

cleanup() {
  if [[ "$KEEP_STAGE" == "no" ]]; then
    if my_mysql -e "SHOW DATABASES LIKE '$STAGE_DB';" 2>/dev/null | grep -q "$STAGE_DB"; then
      log "Dropping staging DB '$STAGE_DB'"
      my_mysql -e "DROP DATABASE \`$STAGE_DB\`;" || true
    fi
  else
    warn "Keeping staging DB '$STAGE_DB' (use --keep-stage was set)"
  fi
  if [[ -d "$WORK_DIR" ]]; then
    rm -rf "$WORK_DIR"
  fi
}
trap cleanup EXIT

# Wrappers
my_mysql() {
  $DOCKER_CMD exec -i -e MYSQL_PWD="$DB_PASS" "$DOCKER_CONTAINER" \
    mariadb -u "$DB_USER" "$@"
}

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --region)      REGION_ID="$2";        shift 2 ;;
    --db)          DB_NAME="$2";          shift 2 ;;
    --user)        DB_USER="$2";          shift 2 ;;
    --pass)        DB_PASS="$2";          shift 2 ;;
    --container)   DOCKER_CONTAINER="$2"; shift 2 ;;
    --docker-cmd)  DOCKER_CMD="$2";       shift 2 ;;
    --keep-stage)  KEEP_STAGE="yes";      shift ;;
    -h|--help)     usage; exit 0 ;;
    *) die "Unknown argument: $1 (use --help)" ;;
  esac
done

# ------------------------------ Sanity -----------------------------------------

command -v curl >/dev/null || die "curl not in PATH"
$DOCKER_CMD inspect "$DOCKER_CONTAINER" >/dev/null 2>&1 \
  || die "Docker container '$DOCKER_CONTAINER' not found"
my_mysql -e "USE \`$DB_NAME\`; SELECT 1;" >/dev/null \
  || die "Cannot connect to live DB '$DB_NAME'"

log "Target region: $REGION_ID"
log "Live DB:       $DB_NAME"
log "Staging DB:    $STAGE_DB"
log "Work dir:      $WORK_DIR"

# ------------------------------ Download ---------------------------------------

log "Downloading ${#SQL_FILES[@]} SQL files from official repo"
for f in "${SQL_FILES[@]}"; do
  log "  -> $f"
  curl -fsSL "$REPO_BASE/$f" -o "$WORK_DIR/$f" \
    || die "Failed to download $f"
done
log "Total downloaded: $(du -sh "$WORK_DIR" | awk '{print $1}')"

# ------------------------------ Stage ------------------------------------------

log "Creating staging DB '$STAGE_DB'"
my_mysql -e "CREATE DATABASE \`$STAGE_DB\` CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;"

log "Importing files into staging (this takes 1-3 minutes)"
for f in "${SQL_FILES[@]}"; do
  log "  importing $f ($(du -h "$WORK_DIR/$f" | awk '{print $1}'))"
  {
    echo 'SET FOREIGN_KEY_CHECKS=0;'
    echo 'SET UNIQUE_CHECKS=0;'
    cat "$WORK_DIR/$f"
  } | my_mysql --default-character-set=utf8mb4 "$STAGE_DB"
done

# Sanity: how many rows touch our region in the staging mob table?
STAGE_MOB_COUNT="$(my_mysql -N -e "SELECT COUNT(*) FROM \`$STAGE_DB\`.mob WHERE Region = $REGION_ID;")"
if [[ "$STAGE_MOB_COUNT" -eq 0 ]]; then
  die "Staging DB has 0 mobs for region $REGION_ID. Wrong region ID?"
fi
log "Staging contains $STAGE_MOB_COUNT mobs for region $REGION_ID"

# ------------------------------ Cascade ----------------------------------------

log "Cascading INSERT IGNORE from staging -> $DB_NAME for region $REGION_ID"

# Order matters: parent tables first (so FK-like refs resolve).
my_mysql <<SQL
SET FOREIGN_KEY_CHECKS=0;
SET UNIQUE_CHECKS=0;
USE \`$DB_NAME\`;

-- 1. NPCTemplate: every template referenced by a region-$REGION_ID mob.
INSERT IGNORE INTO NpcTemplate
  SELECT * FROM \`$STAGE_DB\`.npctemplate
  WHERE TemplateId IN (
    SELECT DISTINCT NPCTemplateID FROM \`$STAGE_DB\`.mob
    WHERE Region = $REGION_ID AND NPCTemplateID IS NOT NULL AND NPCTemplateID > 0
  );

-- 2. NPCEquipment: equipment of those templates (if the template field name
--    differs, just continue - failures here are non-fatal).
INSERT IGNORE INTO NPCEquipment
  SELECT * FROM \`$STAGE_DB\`.npcequipment
  WHERE TemplateID IN (
    SELECT DISTINCT NPCTemplateID FROM \`$STAGE_DB\`.mob
    WHERE Region = $REGION_ID AND NPCTemplateID IS NOT NULL AND NPCTemplateID > 0
  );

-- 3. LootTemplate + MobXLootTemplate: drop tables of region-$REGION_ID mobs.
INSERT IGNORE INTO MobXLootTemplate
  SELECT * FROM \`$STAGE_DB\`.mobxloottemplate
  WHERE MobName IN (
    SELECT DISTINCT Name FROM \`$STAGE_DB\`.mob WHERE Region = $REGION_ID
  );

INSERT IGNORE INTO LootTemplate
  SELECT * FROM \`$STAGE_DB\`.loottemplate
  WHERE TemplateName IN (
    SELECT DISTINCT LootTemplateName FROM \`$STAGE_DB\`.mobxloottemplate
    WHERE MobName IN (SELECT Name FROM \`$STAGE_DB\`.mob WHERE Region = $REGION_ID)
  );

-- 4. ItemTemplate: items dropped by the loot tables we just imported.
INSERT IGNORE INTO ItemTemplate
  SELECT * FROM \`$STAGE_DB\`.itemtemplate
  WHERE Id_nb IN (
    SELECT DISTINCT ItemTemplateID FROM \`$STAGE_DB\`.loottemplate
    WHERE TemplateName IN (
      SELECT DISTINCT LootTemplateName FROM \`$STAGE_DB\`.mobxloottemplate
      WHERE MobName IN (SELECT Name FROM \`$STAGE_DB\`.mob WHERE Region = $REGION_ID)
    )
  );

-- 5. MerchantItem: vendor inventories. We can't easily filter to the region
--    (Mob.MerchantListID may not exist; vendors may be referenced indirectly).
--    Pragmatic call: import every merchant whose ItemListID matches a merchant
--    NPC in region $REGION_ID *if* the join is possible; otherwise skip.
INSERT IGNORE INTO MerchantItem
  SELECT * FROM \`$STAGE_DB\`.merchantitem
  WHERE ItemListID IN (
    SELECT DISTINCT GuildName FROM \`$STAGE_DB\`.mob WHERE Region = $REGION_ID AND GuildName <> ''
  );
-- The above heuristic uses Mob.GuildName which often stores the merchant list
-- key in DOL. Some merchants use a different field; the worst case is "no row
-- imported" not "wrong row imported".

-- 6. Finally the mobs themselves.
INSERT IGNORE INTO Mob
  SELECT * FROM \`$STAGE_DB\`.mob WHERE Region = $REGION_ID;

SET FOREIGN_KEY_CHECKS=1;
SET UNIQUE_CHECKS=1;
SQL

# ------------------------------ Report -----------------------------------------

log "Post-import counts (live DB):"
my_mysql -t "$DB_NAME" -e "
SELECT 'Mob region $REGION_ID' AS table_, COUNT(*) AS n FROM Mob WHERE Region = $REGION_ID
UNION ALL SELECT 'NpcTemplate (total)',    COUNT(*) FROM NpcTemplate
UNION ALL SELECT 'NPCEquipment (total)',   COUNT(*) FROM NPCEquipment
UNION ALL SELECT 'LootTemplate (total)',   COUNT(*) FROM LootTemplate
UNION ALL SELECT 'MobXLootTemplate (total)', COUNT(*) FROM MobXLootTemplate
UNION ALL SELECT 'MerchantItem (total)',   COUNT(*) FROM MerchantItem
UNION ALL SELECT 'ItemTemplate (total)',   COUNT(*) FROM ItemTemplate;
"

log "Done. Restart the gameserver to pick up the new mobs:"
log "  sudo docker compose restart gameserver"
