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
# A 32-byte key, base64, for the contact address and device token encryption (Encryption:KeyRef, Encryption:HashKeyRef).
key() { openssl rand -base64 32; }

if $force || [[ ! -f .env ]]; then
  cat > .env <<EOF
POSTGRES_PASSWORD=$(secret)
RABBITMQ_PASSWORD=$(secret)
KEYCLOAK_ADMIN_PASSWORD=$(secret)
NOTIFICATIONS_ADMIN_CLIENT_SECRET=$(secret)
SAMPLE_PRODUCER_CLIENT_SECRET=$(secret)
TENANT_ADMIN_CLIENT_SECRET=$(secret)
PEOPLE_TEST_PASSWORD=$(secret)
NOTIFICATIONS_DIRECTORY_CLIENT_SECRET=$(secret)
SAMPLE_WEBHOOK_SECRET=$(secret)
NOTIFICATIONS_CONTACT_KEY=$(key)
NOTIFICATIONS_CONTACT_HASH_KEY=$(key)
EOF
  echo "wrote deploy/local/.env"
else
  # Keep what is there; add the secrets later versions introduced.
  for name in TENANT_ADMIN_CLIENT_SECRET SAMPLE_WEBHOOK_SECRET PEOPLE_TEST_PASSWORD NOTIFICATIONS_DIRECTORY_CLIENT_SECRET; do
    grep -q "^$name=" .env || { echo "$name=$(secret)" >> .env; echo "added $name to deploy/local/.env"; }
  done
  for name in NOTIFICATIONS_CONTACT_KEY NOTIFICATIONS_CONTACT_HASH_KEY; do
    grep -q "^$name=" .env || { echo "$name=$(key)" >> .env; echo "added $name to deploy/local/.env"; }
  done
  echo "deploy/local/.env exists; kept (scripts/init.sh --force to regenerate)"
fi
# The shared Keycloak's address (dev-infra, 19 Nov 2026): the issuer in its tokens is http://PUBLIC_HOST:8080/realms/juice, so this
# stack's app, Console and tests must say the same. Taken from dev-infra's .env; localhost when it has none.
if ! grep -q "^PUBLIC_HOST=" .env; then
  shared="$(cd ../../../.. && pwd)/dev-infra/.env"
  host=$( [[ -f "$shared" ]] && grep -E "^PUBLIC_HOST=" "$shared" | cut -d= -f2- || true)
  echo "PUBLIC_HOST=${host:-localhost}" >> .env
  echo "added PUBLIC_HOST=${host:-localhost} to deploy/local/.env"
fi
