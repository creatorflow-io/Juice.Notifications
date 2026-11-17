#!/usr/bin/env bash
# Brings an existing local Keycloak up to the realm file (keycloak/juice-realm.json), which Keycloak imports only into an empty volume.
# Safe to run again: each step skips what is already there. Run scripts/init.sh first, so .env has every secret.
#   - the juice-notifications scope's client_id mapper (11 Nov 2026): producers authenticate as their client;
#   - the notifications-tenant-admin role and the tenant-admin client (11 Nov 2026): an admin of the tests' tenant.
#   - the people-test client and the person accounts ann and ben (17 Nov 2026): the person's API (me) takes no client's own token.
#   - the juice-layout demo app's public client juice-layout-demo (17 Nov 2026, M17).
#   - the identity directory (17 Nov 2026, M16): the platform_tenant user attribute (admin-only) issued by people-test, people in two
#     tenants, the group ops, the realm role on-call, and the notifications-directory client the identity resolver reads them as: a
#     service account with realm-management view-users (users, groups) and view-realm (a realm role's holders). Never view-clients,
#     which reads every client's secret.
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

people=$($K get clients -r juice --fields id,clientId --format csv --noquotes | grep ",people-test$" | cut -d, -f1 || true)
if [[ -z "$people" ]]; then
  people=$($K create clients -r juice -i -s clientId=people-test -s "name=People (local): person accounts by password; each person's tenant is their platform_tenant attribute" -s enabled=true \
    -s publicClient=true -s standardFlowEnabled=false -s directAccessGrantsEnabled=true)
  $K update "clients/$people/default-client-scopes/$scope" -r juice
  $K create "clients/$people/protocol-mappers/models" -r juice -s name=username -s protocol=openid-connect \
    -s protocolMapper=oidc-usermodel-property-mapper -s 'config."user.attribute"=username' -s 'config."claim.name"=preferred_username' \
    -s 'config."jsonType.label"=String' -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false'
  # The realm has no "basic" scope, so a person's token gets its subject from this mapper (a service account's has one anyway).
  $K create "clients/$people/protocol-mappers/models" -r juice -s name=subject -s protocol=openid-connect -s protocolMapper=oidc-sub-mapper \
    -s 'config."access.token.claim"=true' -s 'config."introspection.token.claim"=true'
fi

# A user belongs to a tenant through this attribute (phase-2-plan p2-d1). Keycloak 26 keeps only declared attributes; only admins see or set it.
if ! $K get users/profile -r juice | grep -q '"platform_tenant"'; then
  $K update users/profile -r juice -f - < keycloak/user-profile.json
fi
# people-test issues the person's own tenant (it issued a hard-coded one until M16).
mapper=$($K get "clients/$people/protocol-mappers/models" -r juice --fields id,name,protocolMapper --format csv --noquotes | grep ",platform tenant," || true)
if [[ "$mapper" != *oidc-usermodel-attribute-mapper* ]]; then
  [[ -n "$mapper" ]] && $K delete "clients/$people/protocol-mappers/models/${mapper%%,*}" -r juice
  $K create "clients/$people/protocol-mappers/models" -r juice -s 'name=platform tenant' -s protocol=openid-connect \
    -s protocolMapper=oidc-usermodel-attribute-mapper -s 'config."user.attribute"=platform_tenant' -s 'config."claim.name"=platform_tenant' \
    -s 'config."jsonType.label"=String' -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false'
fi

$K get roles/on-call -r juice >/dev/null 2>&1 || $K create roles -r juice -s name=on-call -s "description=People on call (local identity directory)"
group=$($K get groups -r juice --fields id,name --format csv --noquotes | grep ",ops$" | cut -d, -f1 || true)
[[ -n "$group" ]] || group=$($K create groups -r juice -i -s name=ops)

OTHER=0193f0aa-0000-7000-8000-0000000000b2
# username tenant email-verified enabled groups realm-roles. A for loop, not while read: kcadm (docker compose
# exec) reads stdin.
PEOPLE=("ann TENANT true true ops -" "ben TENANT true true - on-call" "dora TENANT false true ops -" "eli TENANT true false ops on-call"
  "cara OTHER true true ops on-call")
for line in "${PEOPLE[@]}"; do
  read -r person tenant verified enabled groups roles <<< "$line"
  id=$($K get users -r juice -q "username=$person" -q exact=true --fields id --format csv --noquotes)
  if [[ -z "$id" ]]; then
    id=$($K create users -r juice -i -s "username=$person" -s "enabled=$enabled" -s "email=$person@example.com" -s "emailVerified=$verified" \
      -s "firstName=${person^}" -s lastName=Local)
  fi
  $K update "users/$id" -r juice -s "attributes.platform_tenant=[\"${!tenant}\"]" -s "enabled=$enabled" -s "emailVerified=$verified"
  [[ "$groups" == ops ]] && $K update "users/$id/groups/$group" -r juice -s realm=juice -s "userId=$id" -s "groupId=$group" -n
  [[ "$roles" == on-call ]] && $K add-roles -r juice --uid "$id" --rolename on-call
  $K set-password -r juice --username "$person" --new-password "$PEOPLE_TEST_PASSWORD"
done

# The juice-layout demo app (M17): code flow with PKCE from https://localhost:4201, for @juice-js/notifications.
demo=$($K get clients -r juice --fields id,clientId --format csv --noquotes | grep ",juice-layout-demo$" | cut -d, -f1 || true)
if [[ -z "$demo" ]]; then
  demo=$($K create clients -r juice -i -s clientId=juice-layout-demo -s "name=juice-layout demo app (local)" -s enabled=true -s publicClient=true \
    -s standardFlowEnabled=true -s directAccessGrantsEnabled=false -s 'redirectUris=["https://localhost:4201/*"]' \
    -s 'webOrigins=["https://localhost:4201"]' -s 'attributes."pkce.code.challenge.method"=S256' \
    -s 'attributes."post.logout.redirect.uris"=https://localhost:4201/*')
  $K update "clients/$demo/default-client-scopes/$scope" -r juice
  $K create "clients/$demo/protocol-mappers/models" -r juice -s 'name=platform tenant' -s protocol=openid-connect \
    -s protocolMapper=oidc-usermodel-attribute-mapper -s 'config."user.attribute"=platform_tenant' -s 'config."claim.name"=platform_tenant' \
    -s 'config."jsonType.label"=String' -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=false'
  $K create "clients/$demo/protocol-mappers/models" -r juice -s name=subject -s protocol=openid-connect -s protocolMapper=oidc-sub-mapper \
    -s 'config."access.token.claim"=true' -s 'config."introspection.token.claim"=true'
  $K create "clients/$demo/protocol-mappers/models" -r juice -s name=username -s protocol=openid-connect \
    -s protocolMapper=oidc-usermodel-property-mapper -s 'config."user.attribute"=username' -s 'config."claim.name"=preferred_username' \
    -s 'config."jsonType.label"=String' -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=true'
  $K create "clients/$demo/protocol-mappers/models" -r juice -s 'name=full name' -s protocol=openid-connect -s protocolMapper=oidc-full-name-mapper \
    -s 'config."access.token.claim"=true' -s 'config."id.token.claim"=true' -s 'config."userinfo.token.claim"=true'
fi

directory=$($K get clients -r juice --fields id,clientId --format csv --noquotes | grep ",notifications-directory$" | cut -d, -f1 || true)
if [[ -z "$directory" ]]; then
  $K create clients -r juice -s clientId=notifications-directory -s "name=Notifications identity directory (reads users, groups and roles)" \
    -s enabled=true -s publicClient=false -s secret="$NOTIFICATIONS_DIRECTORY_CLIENT_SECRET" -s serviceAccountsEnabled=true \
    -s standardFlowEnabled=false -s directAccessGrantsEnabled=false
fi
$K add-roles -r juice --uusername service-account-notifications-directory --cclientid realm-management --rolename view-users --rolename view-realm
echo "Keycloak realm juice is up to date."
