#!/usr/bin/env bash
# Usage: scripts/seed-orders.sh <count> [seeder options...]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ -f .env ]; then
  set -a
  source .env
  set +a
fi

dotnet run --project tools/OrderSeeder -- "$@"
