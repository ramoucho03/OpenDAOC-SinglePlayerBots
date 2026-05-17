#!/usr/bin/env bash
# Apply all DOLSharp merge files in alphabetical order.
# Run from the repo root: bash tools/dol_merge/run_all.sh
# Adapt env vars below if your db container is named differently.
set -e
CONTAINER=${CONTAINER:-opendaoc-db}
DB=${DB:-opendaoc}
DBUSER=${DBUSER:-root}
DBPASS=${DBPASS:-my-secret-pw}

for f in tools/dol_merge/*.sql; do
  echo "--> Applying $f"
  time sudo docker exec -i "$CONTAINER" mariadb -u"$DBUSER" -p"$DBPASS" "$DB" < "$f"
done
echo "All merges applied. Restart the gameserver to pick up new content."
