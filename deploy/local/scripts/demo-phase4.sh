#!/usr/bin/env bash
# Phase 4 against the running host (docker compose --profile app up): a storm on a route reaching its target as digests, a tenant's own
# template with a preview, bulk replay of failed deliveries, and the tenant's settings. Emails land in Mailpit (http://localhost:8026).
# Needs node for reading JSON. Safe to run again: each run uses its own route, address and type.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
set -a; source "$here/.env"; set +a

API=${API:-http://localhost:5280}
MAILPIT=${MAILPIT:-http://localhost:8026}
KC=http://127.0.0.1:8080/realms/juice/protocol/openid-connect/token
TENANT=${TENANT:-0193f0aa-0000-7000-8000-0000000000a1}
T="/api/v1/tenants/$TENANT"
RUN=$(date +%s)

json() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{let v=JSON.parse(s);for(const k of process.argv[1].split(".").filter(Boolean))v=v?.[isNaN(k)?k:+k];console.log(typeof v==="object"?JSON.stringify(v):v??"")})' "$1"; }
client() { curl -sf -d grant_type=client_credentials -d client_id="$1" -d client_secret="$2" "$KC" | json access_token; }
ADMIN=$(client notifications-admin "$NOTIFICATIONS_ADMIN_CLIENT_SECRET")
PRODUCER=$(client sample-producer "$SAMPLE_PRODUCER_CLIENT_SECRET")

call() { # method path token [body] → the body; fails on an error status other than 409 (already there)
  local status
  status=$(curl -s -o /tmp/notifications-demo4.json -w "%{http_code}" -X "$1" "$API$2" -H "Authorization: Bearer $3" -H 'content-type: application/json' ${4:+-d "$4"})
  if [[ $status -ge 400 && $status -ne 409 ]]; then echo "$1 $2 → $status $(cat /tmp/notifications-demo4.json)" >&2; exit 1; fi
  cat /tmp/notifications-demo4.json
}
step() { printf '\n== %s\n' "$1"; }
mails() { curl -sf "$MAILPIT/api/v1/search?query=$(node -e 'console.log(encodeURIComponent(process.argv[1]))' "to:$1")" | json messages_count; }
has_mail() { [ "$(mails "$1")" -ge "$2" ]; }
wait_for() { # seconds description command…
  local tries=$(( $1 * 2 )); shift; local what=$1; shift
  for _ in $(seq 1 "$tries"); do if "$@" >/dev/null 2>&1; then return 0; fi; sleep 0.5; done
  echo "timed out waiting for $what" >&2; exit 1
}

TYPE="sample.alert$RUN"
ROUTE="storm-$RUN"
OPS="ops-$RUN@example.com"
step "Setup: the sample application, the tenant's email (local SMTP), a type, and a route $ROUTE to $OPS"
call POST /api/v1/applications "$ADMIN" '{"key":"sample","name":"Sample","clientId":"sample-producer"}' >/dev/null
call PUT "/api/v1/applications/sample/tenants/$TENANT" "$ADMIN" >/dev/null
call PUT "$T/integrations/mail" "$ADMIN" '{"channel":"email","kind":"smtp","settings":{"server":"local","from":"notifications@juice.local","fromName":"Juice"},"enabled":true}' >/dev/null
# A digest renders the type's template with every notification as items; one notification has data only.
call PUT /api/v1/applications/sample/types "$PRODUCER" "{\"key\":\"$TYPE\",\"name\":\"Alert\",\"category\":\"alerts\",\"externalRecipients\":true,
  \"schema\":{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\",\"examples\":[7]},\"host\":{\"type\":\"string\"}}},
  \"templates\":{\"email\":\"Subject: {{ if items }}{{ items.size }} alerts{{ else }}Alert {{ data.n }} on {{ data.host }}{{ end }}\\n\\n{{ if items }}{{ for i in items }}- alert {{ i.data.n }}\\n{{ end }}{{ if more > 0 }}and {{ more }} more{{ end }}{{ else }}Alert {{ data.n }}.{{ end }}\"}}" | json version
call PUT "$T/integrations/mail/targets/$ROUTE" "$ADMIN" "{\"address\":\"$OPS\"}" >/dev/null
call PUT "$T/routes/$ROUTE" "$ADMIN" "{\"match\":{\"type\":\"$TYPE\"},\"targets\":[\"$ROUTE\"]}" >/dev/null

step "Storm protection: the tenant's threshold set to 5 a minute, then 40 alerts in a burst"
call PUT "$T/settings" "$ADMIN" '{"stormThreshold":5}' | json stormThreshold
for i in $(seq 1 40); do
  call POST "$T/notifications" "$PRODUCER" "{\"type\":\"$TYPE\",\"idempotencyKey\":\"$TYPE-$i\",\"data\":{\"n\":$i,\"host\":\"web-1\"}}" >/dev/null &
  (( i % 8 == 0 )) && wait
done
wait
echo "Waiting for the digests (a minute's window)…"
sleep 75
echo "Emails to $OPS: $(mails "$OPS") for 40 alerts (the first few one each, the rest in digests)"
call GET "$T/storms?all=true" "$ADMIN" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{for(const x of JSON.parse(s).filter(x=>x.route===process.argv[1]))console.log(`storm on ${x.route}: peak ${x.peak} a minute, ${x.digested} sent as digests, ${x.endedAt?"ended":"going"}`)})' "$ROUTE"
call PUT "$T/settings" "$ADMIN" '{}' >/dev/null

step "A tenant's own template: preview with the schema's sample data, save, send, then go back to the application's"
call POST "$T/templates/preview" "$ADMIN" "{\"type\":\"$TYPE\",\"channel\":\"email\",\"body\":\"Subject: [ACME] Alert {{ data.n }}\\n\\nOur own words for alert {{ data.n }}.\"}" | json content.Subject
call PUT "$T/types/$TYPE/templates/email" "$ADMIN" '{"body":"Subject: [ACME] Alert {{ data.n }}\n\nOur own words for alert {{ data.n }}."}' | json tenantVersion
OWN="own-$RUN@example.com"
call POST "$T/notifications" "$PRODUCER" "{\"type\":\"$TYPE\",\"idempotencyKey\":\"$TYPE-own\",\"recipients\":[{\"kind\":\"address\",\"channel\":\"email\",\"address\":\"$OWN\"}],\"data\":{\"n\":99,\"host\":\"db-1\"}}" >/dev/null
wait_for 30 "the tenant's email" has_mail "$OWN" 1
curl -sf "$MAILPIT/api/v1/search?query=to%3A$OWN" | json messages.0.Subject
curl -sf -X DELETE "$API$T/types/$TYPE/templates/email" -H "Authorization: Bearer $ADMIN" -o /dev/null -w "removed: %{http_code}\n"

MAIL='{"channel":"email","kind":"smtp","settings":{"server":"local","from":"notifications@juice.local","fromName":"Juice"},"priority":1,"enabled":ENABLED}'
step "Failover: an SES integration first, whose credentials it can't use (the stack's test secret has no key id), then the local SMTP"
# Refused before any call to AWS: a permanent provider failure, so the delivery moves to the next integration with its id.
call PUT "$T/integrations/ses-demo" "$ADMIN" '{"channel":"email","kind":"ses","settings":{"region":"eu-west-1","from":"notifications@juice.local"},"secretRef":"env:NOTIFICATIONS_SECRET_TEST_HOOK","priority":0,"enabled":true}' >/dev/null
call PUT "$T/integrations/mail" "$ADMIN" "${MAIL/ENABLED/true}" >/dev/null
OVER="over-$RUN@example.com"
call POST "$T/notifications" "$PRODUCER" "{\"type\":\"$TYPE\",\"idempotencyKey\":\"$TYPE-over\",\"recipients\":[{\"kind\":\"address\",\"channel\":\"email\",\"address\":\"$OVER\"}],\"data\":{\"n\":50,\"host\":\"db-2\"}}" >/dev/null
wait_for 30 "the failed-over email" has_mail "$OVER" 1
call GET "$T/deliveries?type=$TYPE&limit=100" "$ADMIN" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{for(const d of JSON.parse(s).items.filter(d=>d.destinationKey.endsWith(process.argv[1])))console.log(`delivery ${d.id}: ${d.status} through ${d.integration}, ${d.attempts} attempts, ${d.failovers} failover`)})' "$OVER"

step "Bulk replay: with SMTP off, three alerts fail on SES; SMTP back on, the failed ones are replayed under their ids (and fail over)"
call PUT "$T/integrations/mail" "$ADMIN" "${MAIL/ENABLED/false}" >/dev/null
LATE="late-$RUN@example.com"
for i in 1 2 3; do
  call POST "$T/notifications" "$PRODUCER" "{\"type\":\"$TYPE\",\"idempotencyKey\":\"$TYPE-late-$i\",\"recipients\":[{\"kind\":\"address\",\"channel\":\"email\",\"address\":\"$LATE\"}],\"data\":{\"n\":$((100+i)),\"host\":\"db-2\"}}" >/dev/null
done
sleep 8
call PUT "$T/integrations/mail" "$ADMIN" "${MAIL/ENABLED/true}" >/dev/null
call POST "$T/deliveries/replay" "$ADMIN" "{\"channel\":\"email\",\"integration\":\"ses-demo\",\"type\":\"$TYPE\"}"
echo
wait_for 30 "the replayed emails" has_mail "$LATE" 3
echo "Emails to $LATE after the replay: $(mails "$LATE")"
# The SES integration was only for the demo: off again, so the tenant's email goes straight to SMTP.
call PUT "$T/integrations/ses-demo" "$ADMIN" '{"channel":"email","kind":"ses","settings":{"region":"eu-west-1","from":"notifications@juice.local"},"secretRef":"env:NOTIFICATIONS_SECRET_TEST_HOOK","priority":0,"enabled":false}' >/dev/null

step "The tenant's settings: retention (requests 90 days, rendered content 14 by default)"
call PUT "$T/settings" "$ADMIN" '{"requestRetentionDays":30,"contentRetentionDays":7}'
echo
call PUT "$T/settings" "$ADMIN" '{}' >/dev/null
echo "Done. See $MAILPIT"
