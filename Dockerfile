# syntax=docker/dockerfile:1.6

# ============================================================================
# Build stage — restore + compile against the .NET 10 SDK
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
LABEL stage=build

WORKDIR /build

# Install only what we actually use:
#   - git: needed to clone the OpenDAoC-Database SQL schema.
#   - sed: invoked inline below.
# unzip used to be here; nothing in the build actually unzips anything.
RUN apt-get update \
    && apt-get install --no-install-recommends -y git sed ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Clone the SQL schema repo with TLS verification ON. The previous Dockerfile
# turned http.sslVerify off globally, which made the whole clone vulnerable
# to MITM tampering — we keep the default and trust the system CA bundle
# that ca-certificates ships.
RUN git clone --depth=1 https://github.com/OpenDAoC/OpenDAoC-Database.git /tmp/opendaoc-db

# Concatenate every .sql in the canonical OpenDAoC-Database layout into a
# single file. MariaDB's docker-entrypoint replays /docker-entrypoint-initdb.d
# alphabetically; one combined file means the order is predictable and the
# entire schema lands in one transaction-able pass.
WORKDIR /tmp/opendaoc-db/opendaoc-db-core
RUN cat *.sql > combined.sql

# Back to the source tree. The .dockerignore at the repo root keeps build/,
# Release/, obj/, bin/, .git/ etc. out of the build context.
WORKDIR /build
COPY . .

# Build the solution — restore happens implicitly. We tried splitting into
# `restore` + `build --no-restore`, but SDK 10.0.300 fails to locate the
# generated project.assets.json under the BaseIntermediateOutputPath=..\build\<Project>\
# layout declared by the .csproj files, with NETSDK1004 on every project.
# Letting `build` drive the restore avoids that path-resolution bug.
# The project files declare OutputPath=../Release (see CoreServer.csproj),
# so the runtime tree ends up at /build/Release.
RUN dotnet build DOLLinux.sln -c Release

# ============================================================================
# Runtime stage — ASP.NET 10 on Alpine, smaller than the SDK
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
LABEL stage=final

# ICU is required because we run with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=False.
# su-exec lets the entrypoint drop privileges from root to appuser.
# mariadb-client is used by the embedded TCP-wait below.
RUN apk add --no-cache icu-libs su-exec mariadb-client

WORKDIR /app

# Runtime tree produced by the build stage.
COPY --from=build /build/Release /app

# Pre-baked SQL schema, ready for the db-seed sidecar to copy into the shared
# volume mounted at /docker-entrypoint-initdb.d on the db container.
COPY --from=build /tmp/opendaoc-db/opendaoc-db-core/combined.sql /tmp/opendaoc-db/combined.sql

# Entrypoint
COPY --from=build /build/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Game TCP + UDP. Documentary only — docker-compose maps the real ports.
EXPOSE 10300/tcp 10400/udp

ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]
