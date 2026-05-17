#!/usr/bin/env bash
# Apply all DOLSharp merge files in order. Run from the repo root.
# Usage: bash tools/dol_merge/run_all.sh
# Adapt the docker exec line if your db container is named differently.
set -e
CONTAINER=${CONTAINER:-opendaoc-db}
DB=${DB:-opendaoc}
USER=${USER:-root}
PASS=${PASS:-my-secret-pw}

for f in tools/dol_merge/*.sql; do
  echo "--> Applying $f"
  sudo docker exec -i "$CONTAINER" mariadb -u"$USER" -p"$PASS" "$DB" < "$f"
done
echo "All merges applied. Restart the gameserver to pick up new spells/styles/loot."
