#!/usr/bin/env bash
# Brings an existing local Keycloak up to the realm file (keycloak/juice-realm.json), which Keycloak imports only into an empty volume.
# Safe to run again: each step skips what is already there. Run scripts/init.sh first, so .env has every secret.
#   - the juice-notifications scope's client_id mapper (11 Nov 2026): producers authenticate as their client;
#   - the notifications-tenant-admin role and the tenant-admin client (11 Nov 2026): an admin of the tests' tenant.
set -euo pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
set -a; source .env; set +a
K="docker compose exec -T keycloak /opt/keycloak/bin/kcadm.sh"
TENANT=0193f0aa-0000-7000-8000-0000000000a1

$K config credentials --server http://localhost:8080 --realm master --user admin --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null

# -q name= does not filter in kcadm: match the name ourselves.
scope=$($K get client-scopes -r juice --fields id,name --format csv --noquotes | grep ",juice-notifications$" | cut -d, -f1)
if ! $K get "client-scopes/$scope/protocol-mappers/models" -r juice --fields name --format csv --noquotes | grep -qx "client id"; then
  $K create "client-scopes/$scope/protocol-mappers/models" -r juice -s name="client id" -s protocol=openid-connect \
    -s protocolMapper=oidc-usersessionmodel-note-mapper -s 'config."user.session.note"=client_id' -s 'config."claim.name"=client_id' \
    -s 'config."jsonType.label"=String' -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false'
fi

$K get roles/notifications-tenant-admin -r juice >/dev/null 2>&1 \
  || $K create roles -r juice -s name=notifications-tenant-admin -s "description=Administers the notifications of the one tenant its token's platform_tenant claim names"

client=$($K get clients -r juice --fields id,clientId --format csv --noquotes | grep ",tenant-admin$" | cut -d, -f1 || true)
if [[ -z "$client" ]]; then
  client=$($K create clients -r juice -i -s clientId=tenant-admin -s "name=Tenant admin (local): administers tenant $TENANT" -s enabled=true \
    -s publicClient=false -s secret="$TENANT_ADMIN_CLIENT_SECRET" -s serviceAccountsEnabled=true -s standardFlowEnabled=false \
    -s directAccessGrantsEnabled=false)
  $K update "clients/$client/default-client-scopes/$scope" -r juice
  $K create "clients/$client/protocol-mappers/models" -r juice -s 'name=platform tenant' -s protocol=openid-connect \
    -s protocolMapper=oidc-hardcoded-claim-mapper -s 'config."claim.name"=platform_tenant' -s "config.\"claim.value\"=$TENANT" \
    -s 'config."jsonType.label"=String' -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false'
  $K add-roles -r juice --uusername service-account-tenant-admin --rolename notifications-tenant-admin
fi
echo "Keycloak realm juice is up to date."
