#!/usr/bin/env bash
# One-time setup of the local stack: writes .env with random secrets. Safe to re-run: an existing .env is kept.
#   scripts/init.sh           create .env if it is missing
#   scripts/init.sh --force   regenerate it (then: docker compose down -v, since the database keeps its old password)
set -euo pipefail
cd "$(dirname "$0")/.."

force=false
case "${1:-}" in
  "") ;;
  --force) force=true ;;
  *) echo "usage: scripts/init.sh [--force]" >&2; exit 1 ;;
esac

secret() { openssl rand -hex 24; }

if $force || [[ ! -f .env ]]; then
  cat > .env <<EOF
POSTGRES_PASSWORD=$(secret)
RABBITMQ_PASSWORD=$(secret)
KEYCLOAK_ADMIN_PASSWORD=$(secret)
NOTIFICATIONS_ADMIN_CLIENT_SECRET=$(secret)
SAMPLE_PRODUCER_CLIENT_SECRET=$(secret)
TENANT_ADMIN_CLIENT_SECRET=$(secret)
EOF
  echo "wrote deploy/local/.env"
else
  # Keep what is there; add the secrets later versions introduced.
  for name in TENANT_ADMIN_CLIENT_SECRET; do
    grep -q "^$name=" .env || { echo "$name=$(secret)" >> .env; echo "added $name to deploy/local/.env"; }
  done
  echo "deploy/local/.env exists; kept (scripts/init.sh --force to regenerate)"
fi
