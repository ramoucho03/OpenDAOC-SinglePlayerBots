# ---- build ----
# Use the official .NET 10.0 SDK image as the build environment
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
LABEL stage=build

# Set the working directory in the container
WORKDIR /build

# Copy the source code to the build container
COPY . .

# Pin Eve-of-Darkness/db-public release tag. Bump to pull a newer monthly
# snapshot of the vanilla DOL data set (mobs / loot / quests / etc.).
# Override at build time:  docker compose build --build-arg EOD_RELEASE_TAG=86
ARG EOD_RELEASE_TAG=85

# Install required tools and clone the database repository
RUN apt-get update && \
    apt-get install -y unzip git sed python3 p7zip-full curl && \
    git config --global http.sslVerify false && \
    git clone https://github.com/OpenDAoC/OpenDAoC-Database.git /tmp/opendaoc-db && \
    git clone https://github.com/Larogoth/DAoCDatabase.git /tmp/larogoth-db && \
    mkdir -p /tmp/eod-db && \
    curl -fL --retry 3 \
        -o /tmp/eod-db/public-db.mysql.sql.7z \
        "https://github.com/Eve-of-Darkness/db-public/releases/download/${EOD_RELEASE_TAG}/public-db.mysql.sql.7z" && \
    7z x -y /tmp/eod-db/public-db.mysql.sql.7z -o/tmp/eod-db && \
    rm /tmp/eod-db/public-db.mysql.sql.7z && \
    rm -rf /var/lib/apt/lists/*

# Combine the SQL files
WORKDIR /tmp/opendaoc-db/opendaoc-db-core
RUN cat *.sql > combined.sql

# Copy our custom SQL patches alongside combined.sql so they get applied
# by mariadb's docker-entrypoint-initdb.d on FIRST DB init (clean install).
# Filename ordering matters: combined.sql (c) runs before zz_*.sql (z), so
# our patches always apply *after* the upstream seed has been loaded.
RUN cp /build/sql/heretic_live.sql       /tmp/opendaoc-db/opendaoc-db-core/zz_heretic_live.sql && \
    cp /build/sql/battlegrounds_live.sql /tmp/opendaoc-db/opendaoc-db-core/zz_battlegrounds_live.sql

# Build the 5-stage Larogoth + Eve-of-Darkness pipeline (numbering = apply order):
#   10_larogoth_items.sql       — INSERT IGNORE missing items (shields + magical only)
#   20_larogoth_ext.sql         — extended item metadata (delve_text, utility, tags)
#   30_larogoth_loot.sql        — ItemLootSource table (drop/quest/store info)
#   40_larogoth_loot_wiring.sql — ItemLootSource → LootTemplate + MobXLootTemplate
#                                  + LootOTD so loot actually drops in-game
#   50_eveofdarkness_fill.sql   — vanilla DOL fill (INSERT IGNORE, see step 2 below)
# Applied by the gameserver entrypoint with checksum-based idempotency, NOT via
# docker-entrypoint-initdb.d, so existing DBs also receive the data on upgrade.
RUN python3 /build/scripts/build_larogoth_sql.py \
        /tmp/larogoth-db/daoc_item_database.json \
        /build/sql/larogoth && \
    cp /build/sql/larogoth_loot_wiring.sql /build/sql/larogoth/40_larogoth_loot_wiring.sql

# Preprocess the Eve-of-Darkness vanilla DOL DB into 'fill the gaps' SQL:
# strip DDL (we keep OpenDAoC's schema), convert INSERT -> INSERT IGNORE so
# existing rows stay untouched. Output lives next to the Larogoth files so the
# entrypoint applies them via the same checksum-tracked path.
RUN python3 /build/scripts/preprocess_eod_sql.py \
        /tmp/eod-db/public-db.mysql.sql \
        /build/sql/larogoth/50_eveofdarkness_fill.sql

# Set the working directory back to the build container
WORKDIR /build

# Copy serverconfig.example.xml to serverconfig.xml
RUN cp /build/CoreServer/config/serverconfig.example.xml /build/CoreServer/config/serverconfig.xml

# Build the application in Release mode
RUN dotnet build DOLLinux.sln -c Release

# ---- final ----
# Use the official .NET 10.0 Alpine Runtime image as the base for the final image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
LABEL stage=final

## Install ICU libraries, su-exec, and mariadb-client (for entrypoint SQL re-apply)
RUN apk add --no-cache icu-libs su-exec mariadb-client

# Set the working directory in the container
WORKDIR /app

# Copy the build output from the build stage
COPY --from=build /build/Release /app

# Copy the combined.sql file from the build stage
COPY --from=build /tmp/opendaoc-db/opendaoc-db-core/combined.sql /tmp/opendaoc-db/combined.sql

# Copy our custom SQL patches into the runtime image so the entrypoint
# can re-apply them on every startup (idempotent) — necessary because mariadb
# only runs /docker-entrypoint-initdb.d/ on first init, not on existing DBs.
COPY --from=build /build/sql/heretic_live.sql       /app/sql/heretic_live.sql
COPY --from=build /build/sql/battlegrounds_live.sql /app/sql/battlegrounds_live.sql

# Larogoth-generated SQL (one file per migration, applied in order by entrypoint).
COPY --from=build /build/sql/larogoth/                /app/sql/larogoth/

# Copy the entrypoint script
COPY --from=build /build/entrypoint.sh /app

# Make the entrypoint script executable
RUN chmod +x /app/entrypoint.sh

# Set the entrypoint
ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]
