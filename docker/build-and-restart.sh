#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/../docker-compose.yml"

echo "Using compose file: $COMPOSE_FILE"

docker compose -f "$COMPOSE_FILE" up -d --build

echo "Done."