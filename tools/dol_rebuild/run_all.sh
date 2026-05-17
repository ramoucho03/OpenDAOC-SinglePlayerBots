#!/usr/bin/env bash
# DOLSharp full rebuild — wipe + reimport.
# Run from repo root: bash tools/dol_rebuild/run_all.sh
set -e
CONTAINER=${CONTAINER:-opendaoc-db}
DB=${DB:-opendaoc}
DBUSER=${DBUSER:-root}
DBPASS=${DBPASS:-my-secret-pw}

for f in tools/dol_rebuild/0*_*.sql tools/dol_rebuild/[1-9]*_*.sql; do
  [ -f "$f" ] || continue
  echo "==> $(basename "$f")"
  time sudo docker exec -i "$CONTAINER" mariadb -u"$DBUSER" -p"$DBPASS" "$DB" < "$f" >/dev/null
done
echo "Rebuild done. Restart the gameserver."
