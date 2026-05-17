#!/usr/bin/env bash
# Quick DB backup script — drop a gzipped mysqldump in ./backups/
# Run from the repo root: bash tools/backup_db.sh
# Default container/user/db; override via env vars.
set -e

CONTAINER=${CONTAINER:-opendaoc-db}
DB=${DB:-opendaoc}
DBUSER=${DBUSER:-root}
DBPASS=${DBPASS:-my-secret-pw}
DEST=${DEST:-./backups}

mkdir -p "$DEST"
TS=$(date +%Y%m%d_%H%M%S)
OUT="${DEST}/${DB}_${TS}.sql.gz"

echo "Dumping ${DB} from ${CONTAINER} -> ${OUT}"
sudo docker exec "$CONTAINER" mariadb-dump -u"$DBUSER" -p"$DBPASS" \
  --single-transaction --quick --routines --triggers --events "$DB" \
  | gzip > "$OUT"

SIZE=$(du -h "$OUT" | cut -f1)
echo "Done. Size: $SIZE"
ls -lh "${DEST}"/${DB}_*.sql.gz | tail -5
