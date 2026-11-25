#!/usr/bin/env bash
# Phase 4c: the Console, against the running host (docker compose --profile app up, the Console served at http://localhost:5280). Sets up a
# small story through the API (as the platform admin client and the sample application), then prints the walk through it in the browser:
# tess (tenant admin) finds a delivered email, a recipient muted with no delivery and why, a failed webhook delivery (its attempt's 404,
# retried under the same id), an open correlation to acknowledge and resolve, and the tenant's storm threshold; paul (platform admin) sees
# the applications and their tenants. Needs the shared Keycloak (dev-infra), node for reading JSON, and Mailpit (http://localhost:8026).
# Safe to run again.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
set -a; source "$here/.env"; set +a

API=${API:-http://localhost:5280}
KC=http://127.0.0.1:8080/realms/juice/protocol/openid-connect/token
TENANT=${TENANT:-0193f0aa-0000-7000-8000-0000000000a1}
T="/api/v1/tenants/$TENANT"
C="$API/tenants/$TENANT"
RUN=$(date +%s)

json() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{let v=JSON.parse(s);for(const k of process.argv[1].split(".").filter(Boolean))v=v?.[isNaN(k)?k:+k];console.log(typeof v==="object"?JSON.stringify(v):v??"")})' "$1"; }
client() { curl -sf -d grant_type=client_credentials -d client_id="$1" -d client_secret="$2" "$KC" | json access_token; }
person() { curl -sf -d grant_type=password -d client_id=people-test -d "username=$1" --data-urlencode "password=$PEOPLE_TEST_PASSWORD" "$KC" | json access_token; }
ADMIN=$(client notifications-admin "$NOTIFICATIONS_ADMIN_CLIENT_SECRET")
PRODUCER=$(client sample-producer "$SAMPLE_PRODUCER_CLIENT_SECRET")
ANN=$(person ann)
BEN=$(person ben)

call() { # method path token [body] → the body; fails on an error status other than 409 (already there)
  local status
  status=$(curl -s -o /tmp/notifications-demo.json -w "%{http_code}" -X "$1" "$API$2" -H "Authorization: Bearer $3" -H 'content-type: application/json' ${4:+-d "$4"})
  if [[ $status -ge 400 && $status -ne 409 ]]; then echo "$1 $2 → $status $(cat /tmp/notifications-demo.json)" >&2; exit 1; fi
  cat /tmp/notifications-demo.json
}
step() { printf '\n== %s\n' "$1"; }
wait_for() { # seconds description command…
  local tries=$(( $1 * 2 )); shift; local what=$1; shift
  for _ in $(seq 1 "$tries"); do if "$@" >/dev/null 2>&1; then return 0; fi; sleep 0.5; done
  echo "timed out waiting for $what" >&2; exit 1
}
deliveries() { call GET "$T/notifications/$1" "$ADMIN" | json deliveries; }
has_delivery() { [[ $(deliveries "$1") != "[]" ]]; }
failed() { deliveries "$1" | grep -q '"status":"failed"'; }

step "Setup: the sample application, the tenant's email (local SMTP), its people from Keycloak, and a webhook to a receiver here that answers 404"
call POST /api/v1/applications "$ADMIN" '{"key":"sample","name":"Sample","clientId":"sample-producer"}' >/dev/null
call PUT "/api/v1/applications/sample/tenants/$TENANT" "$ADMIN" >/dev/null
call PUT "$T/integrations/mail" "$ADMIN" '{"channel":"email","kind":"smtp","settings":{"server":"local","from":"notifications@juice.local","fromName":"Juice"}}' >/dev/null
call PUT "$T/resolvers/people" "$ADMIN" '{"kind":"identity","handles":["user","group","role"],"settings":{"directory":"keycloak"}}' >/dev/null
# A receiver on this machine's address (the service never sends to loopback) that answers 404: a destination's failure, at once. It runs
# until the walk is done (Enter at the end), so a retry from the Console reaches it too.
HOOK_PORT=${HOOK_PORT:-5299}
node -e "require('http').createServer((q, s) => { q.resume(); q.on('end', () => s.writeHead(404).end()); }).listen($HOOK_PORT, '$PUBLIC_HOST')" &
HOOK_PID=$!
trap 'kill $HOOK_PID 2>/dev/null || true' EXIT
# Listening, or the webhook would only retry (connection refused) and the demo wait for a failure that never comes.
hook_up() { [[ $(curl -s -o /dev/null -w "%{http_code}" -X POST "http://$PUBLIC_HOST:$HOOK_PORT/hook") == 404 ]]; }
wait_for 10 "the receiver on $PUBLIC_HOST:$HOOK_PORT (PUBLIC_HOST in .env: this machine's private address; HOOK_PORT free)" hook_up
call PUT "$T/allowlist" "$ADMIN" "{\"destination\":\"$PUBLIC_HOST\",\"ports\":[$HOOK_PORT]}" >/dev/null
call PUT "$T/integrations/demo-hooks" "$ADMIN" '{"channel":"webhook","kind":"webhook","secretRef":"env:NOTIFICATIONS_SECRET_TEST_HOOK"}' >/dev/null
call PUT "$T/integrations/demo-hooks/targets/demo-ops" "$ADMIN" "{\"address\":\"http://$PUBLIC_HOST:$HOOK_PORT/hook\"}" >/dev/null

step "The application declares an alert (email and inbox, correlated) and an ops event (webhook), routed to demo-ops"
call PUT /api/v1/applications/sample/types "$PRODUCER" '{"key":"sample.demo.alert","name":"Demo alert","category":"alerts","correlation":"optional",
  "schema":{"type":"object","properties":{"what":{"type":"string"}}},
  "templates":{"email":"Subject: Alert: {{ data.what }}\n\nSomething needs a look.","inbox":"Title: Alert: {{ data.what }}\n\nSomething needs a look."}}' | json version
call PUT /api/v1/applications/sample/types "$PRODUCER" '{"key":"sample.demo.ops","name":"Demo ops event","category":"ops",
  "schema":{"type":"object"},"templates":{"webhook":"passthrough"}}' | json version
call PUT "$T/routes/demo-ops" "$ADMIN" '{"match":{"type":"sample.demo.ops"},"targets":["demo-ops"]}' >/dev/null

step "Ann mutes the alert type for an hour (her own mute); tess's storm threshold is 50 a minute"
ENDS=$(node -e 'console.log(new Date(Date.now()+3600e3).toISOString())')
call POST "$T/me/mutes" "$ANN" "{\"match\":{\"type\":\"sample.demo.alert\"},\"endsAt\":\"$ENDS\"}" >/dev/null
call PUT "$T/settings" "$ADMIN" '{"stormThreshold":50,"requestRetentionDays":null,"contentRetentionDays":null}' | json stormThreshold

KEY="demo-$RUN"
# People are named by their identity (Keycloak's id), as each reads it on their own page.
ANN_ID=$(call GET "$T/me" "$ANN" | json externalId)
BEN_ID=$(call GET "$T/me" "$BEN" | json externalId)
step "Send: an alert to ann and ben, correlation $KEY (ben gets it, ann muted it); an ops event to the webhook"
ALERT=$(call POST "$T/notifications" "$PRODUCER" "{\"type\":\"sample.demo.alert\",\"idempotencyKey\":\"$KEY\",\"correlationKey\":\"$KEY\",
  \"recipients\":[{\"kind\":\"user\",\"id\":\"$ANN_ID\",\"resolver\":\"people\"},{\"kind\":\"user\",\"id\":\"$BEN_ID\",\"resolver\":\"people\"}],\"data\":{\"what\":\"disk 91%\"}}" | json id)
OPS=$(call POST "$T/notifications" "$PRODUCER" "{\"type\":\"sample.demo.ops\",\"idempotencyKey\":\"ops-$RUN\",\"data\":{\"run\":$RUN}}" | json id)
wait_for 30 "the alert's deliveries" has_delivery "$ALERT"
wait_for 60 "the ops event to fail at the webhook" failed "$OPS"
HOOK=$(deliveries "$OPS" | json 0.id)
echo "alert $ALERT, ops event $OPS (delivery $HOOK, failed)"

cat <<EOF

== In the Console ($API), signed in as tess (password PEOPLE_TEST_PASSWORD):
1. Overview: $C
   the last day's deliveries by status and channel; the failing integration demo-hooks.
2. Why didn't ann get the alert? $C/notifications/$ALERT
   two recipients: ben's email and inbox deliveries; ann muted (her own mute), with no delivery.
3. The failed webhook: $C/deliveries?status=failed&integration=demo-hooks
   open it ($C/deliveries/$HOOK): the attempt's 404; Retry sends it again under the same id (it fails again: the address is wrong).
   The target's address is in Integrations: $C/integrations/demo-hooks
4. The alert's correlation: $C/correlations/sample/$KEY
   acknowledge, then resolve: what is still due is cancelled, and ben's inbox item follows.
5. Settings: $C/settings
   storm threshold 50 (empty is the deployment's default); retention by default.
6. Signed in as paul: "Applications and their tenants" in the tenant bar ($API/applications): sample, linked to this tenant;
   it has sent for it, so its link stays.
Emails: http://localhost:8026
EOF
# The receiver stays up for the walk (step 3's retry); Enter, or the end of input, stops it.
read -r -p "Enter when done: " _ || true
