#!/usr/bin/env bash
# Phases 2 and 3 end to end against the running host (docker compose --profile app up): people from Keycloak, an approval in their inboxes,
# an action, the producer resolving it, an escalation that stops, and a digest. Needs the shared Keycloak (dev-infra), node for reading
# JSON, and Mailpit (http://localhost:8026) to see the emails. Safe to run again.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
set -a; source "$here/.env"; set +a

API=${API:-http://localhost:5280}
KC=http://127.0.0.1:8080/realms/juice/protocol/openid-connect/token
TENANT=${TENANT:-0193f0aa-0000-7000-8000-0000000000a1}
T="/api/v1/tenants/$TENANT"
RUN=$(date +%s)

json() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{let v=JSON.parse(s);for(const k of process.argv[1].split(".").filter(Boolean))v=v?.[isNaN(k)?k:+k];console.log(typeof v==="object"?JSON.stringify(v):v??"")})' "$1"; }
client() { curl -sf -d grant_type=client_credentials -d client_id="$1" -d client_secret="$2" "$KC" | json access_token; }
person() { curl -sf -d grant_type=password -d client_id=people-test -d "username=$1" --data-urlencode "password=$PEOPLE_TEST_PASSWORD" "$KC" | json access_token; }
ADMIN=$(client notifications-admin "$NOTIFICATIONS_ADMIN_CLIENT_SECRET")
PRODUCER=$(client sample-producer "$SAMPLE_PRODUCER_CLIENT_SECRET")
ANN=$(person ann)

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

step "Setup: the sample application, the tenant's email, and its people from Keycloak (resolver 'people', directory 'keycloak')"
call POST /api/v1/applications "$ADMIN" '{"key":"sample","name":"Sample","clientId":"sample-producer"}' >/dev/null
call PUT "/api/v1/applications/sample/tenants/$TENANT" "$ADMIN" >/dev/null
call PUT "$T/integrations/mail" "$ADMIN" '{"channel":"email","kind":"smtp","settings":{"server":"local","from":"notifications@juice.local","fromName":"Juice"}}' >/dev/null
call PUT "$T/resolvers/people" "$ADMIN" '{"kind":"identity","handles":["user","group","role"],"settings":{"directory":"keycloak"}}' | json kind
call PUT "$T/escalation-policies/spend" "$ADMIN" \
  '{"steps":[{"afterMinutes":1,"recipients":[{"kind":"role","id":"on-call","resolver":"people"}]}],"stopOnAcknowledge":true}' | json key

step "The application declares an approval (actions, correlation, escalation) and a digested reminder"
call PUT /api/v1/applications/sample/types "$PRODUCER" '{"key":"sample.spend.approval","name":"Approve a spend","category":"spend","correlation":"required",
  "schema":{"type":"object","properties":{"amount":{"type":"integer"},"what":{"type":"string"}}},
  "actions":[{"id":"approve","label":"Approve"},{"id":"reject","label":"Reject"}],
  "workflow":[{"step":"send"},{"step":"escalate","policy":"spend"}],
  "templates":{"email":"Subject: Approve {{ data.amount }} USD for {{ data.what }}\n\nPlease approve or reject in your inbox.",
               "inbox":"Title: Approve {{ data.amount }} USD for {{ data.what }}\n\nWaiting for your decision."}}' | json version
call PUT /api/v1/applications/sample/types "$PRODUCER" '{"key":"sample.build.finished","name":"Build finished","category":"builds",
  "schema":{"type":"object","properties":{"build":{"type":"string"}}},
  "workflow":[{"step":"digest","windowSeconds":10,"by":"subscriber"},{"step":"send"}],
  "templates":{"email":"Subject: {{ items.size }} builds finished\n\n{{ for i in items }}- {{ i.data.build }}\n{{ end }}{{ if more > 0 }}and {{ more }} more\n{{ end }}"}}' | json version

KEY="spend-$RUN"
step "Send the approval to the group 'ops' (Keycloak), correlation $KEY: ann (verified email) and dora (unverified: inbox only)"
call POST "$T/notifications" "$PRODUCER" "{\"type\":\"sample.spend.approval\",\"idempotencyKey\":\"$KEY\",\"correlationKey\":\"$KEY\",
  \"recipients\":[{\"kind\":\"group\",\"id\":\"ops\",\"resolver\":\"people\"}],\"data\":{\"amount\":1200,\"what\":\"new laptops\"}}" | json id

step "Ann's inbox (her own token): the approval, open, with its actions"
has_item() { call GET "$T/me/inbox" "$ANN" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.exit(JSON.parse(s).some(i=>i.type==="sample.spend.approval"&&!i.interactions.some(x=>x.kind==="action"))?0:1))'; }
wait_for 30 "ann's item" has_item
ITEM=$(call GET "$T/me/inbox" "$ANN" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const i=JSON.parse(s).find(i=>i.type==="sample.spend.approval"&&!i.interactions.some(x=>x.kind==="action"));console.log(i.id);console.error("  "+i.title+" · "+i.correlation.state+" · actions: "+i.actions.map(a=>a.label).join(", "))})')
echo "  item $ITEM"

step "Ann approves in her inbox: an interaction (the producer would hear it as an event), nothing decided yet"
call POST "$T/me/inbox/$ITEM/actions/approve" "$ANN" | json interactions

step "The producer resolves the correlation: everything still due is cancelled, the escalation stops, every item shows the outcome"
call POST "$T/correlations/$KEY/resolve" "$PRODUCER" '{"outcome":"Approved by ann","by":"ann"}' | json state
call GET "$T/me/inbox/$ITEM" "$ANN" | json correlation

step "A digest: three builds finish within 10 s; ann gets one email listing them (and three inbox items)"
for b in 101 102 103; do
  call POST "$T/notifications" "$PRODUCER" "{\"type\":\"sample.build.finished\",\"idempotencyKey\":\"build-$RUN-$b\",
    \"recipients\":[{\"kind\":\"user\",\"id\":\"$(curl -sf -H "Authorization: Bearer $ANN" "$API$T/me" | json externalId)\",\"resolver\":\"people\"}],\"data\":{\"build\":\"#$b\"}}" >/dev/null
done
echo "  sent; the digest closes in about 10 s, then Mailpit shows one email: http://localhost:8026"
