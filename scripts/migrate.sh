#!/usr/bin/env bash
# Usage:
#   scripts/migrate.sh                — apply pending migrations to ORDER_DB_CONNECTION_STRING
#   scripts/migrate.sh add <Name>     — generate a new migration from model changes (commit the result)
#
# (see OrderDbContextFactory.cs for the same env var / localhost fallback)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ -f .env ]; then
  set -a
  source .env
  set +a
fi

dotnet tool restore

PROJECT_ARGS=(
  --project src/Infrastructure/KartOrderService.Infrastructure.csproj
  --startup-project src/Infrastructure/KartOrderService.Infrastructure.csproj
)

case "${1:-}" in
  add)
    if [ -z "${2:-}" ]; then
      echo "Usage: $0 add <MigrationName>" >&2
      exit 1
    fi
    dotnet ef migrations add "$2" "${PROJECT_ARGS[@]}"
    ;;
  "")
    dotnet ef database update "${PROJECT_ARGS[@]}"
    ;;
  *)
    echo "Usage: $0 [add <MigrationName>]" >&2
    exit 1
    ;;
esac
