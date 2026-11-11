#!/usr/bin/env bash
# The MVP end to end against the running host (scripts/run-app.sh): register an application, set up a tenant's email delivery, declare a type,
# send a notification, and show where it went. Safe to run again: each step accepts "already there".
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
set -a; source "$here/.env"; set +a

API=${API:-http://localhost:5280}
KC=http://127.0.0.1:8081/realms/juice/protocol/openid-connect/token
TENANT=${TENANT:-0193f0aa-0000-7000-8000-0000000000a1}
MAILBOX=${MAILBOX:-ops@example.com}

token() { curl -sf -d grant_type=client_credentials -d client_id="$1" -d client_secret="$2" "$KC" | sed 's/.*"access_token":"\([^"]*\)".*/\1/'; }
ADMIN=$(token notifications-admin "$NOTIFICATIONS_ADMIN_CLIENT_SECRET")
PRODUCER=$(token sample-producer "$SAMPLE_PRODUCER_CLIENT_SECRET")

call() { # method path token body → prints "status body"
  local out; out=$(curl -s -o /tmp/notifications-demo.json -w "%{http_code}" -X "$1" "$API$2" -H "Authorization: Bearer $3" -H 'content-type: application/json' ${4:+-d "$4"})
  echo "$out $(cat /tmp/notifications-demo.json)"
}
step() { printf '\n== %s\n%s\n' "$1" "$2"; }

step "1. Register the application 'sample' (client sample-producer)" \
  "$(call POST /api/v1/applications "$ADMIN" '{"key":"sample","name":"Sample","clientId":"sample-producer"}')"
step "2. Let it send for tenant $TENANT" "$(call PUT "/api/v1/applications/sample/tenants/$TENANT" "$ADMIN")"
step "3. The tenant's email integration (deployment SMTP server 'local' = Mailpit)" \
  "$(call PUT "/api/v1/tenants/$TENANT/integrations/mail" "$ADMIN" '{"channel":"email","kind":"smtp","settings":{"server":"local","from":"notifications@juice.local","fromName":"Juice"}}')"
step "4. A target: the ops mailbox" "$(call PUT "/api/v1/tenants/$TENANT/integrations/mail/targets/ops" "$ADMIN" "{\"address\":\"$MAILBOX\"}")"
step "5. A route: production alerts go to ops" \
  "$(call PUT "/api/v1/tenants/$TENANT/routes/prod-alerts" "$ADMIN" '{"match":{"type":"sample.alert.*","labels":{"env":"prod"}},"targets":["ops"]}')"
step "6. The application declares its type" \
  "$(call PUT /api/v1/applications/sample/types "$PRODUCER" '{"key":"sample.alert.firing","name":"Alert firing","category":"alerts","priority":"high","schema":{"type":"object","required":["host","used"],"properties":{"host":{"type":"string"},"used":{"type":"integer"}}},"templates":{"email":"Subject: Disk {{ data.used }}% on {{ data.host }}\n\nHost {{ data.host }} is at {{ data.used }}% disk.\nSeverity: {{ labels.severity }}"}}')"

KEY="demo-$(date +%s)"
ACCEPTED=$(call POST "/api/v1/tenants/$TENANT/notifications" "$PRODUCER" \
  "{\"type\":\"sample.alert.firing\",\"idempotencyKey\":\"$KEY\",\"labels\":{\"env\":\"prod\",\"severity\":\"high\"},\"data\":{\"host\":\"db-1\",\"used\":97}}")
step "7. Send a notification (idempotency key $KEY)" "$ACCEPTED"
ID=$(echo "$ACCEPTED" | sed 's/.*"id":"\([^"]*\)".*/\1/')

for _ in $(seq 1 30); do
  DETAIL=$(call GET "/api/v1/tenants/$TENANT/notifications/$ID" "$PRODUCER")
  echo "$DETAIL" | grep -q '"status":"accepted"\|"status":"delivered"\|"status":"failed"' && break
  sleep 0.5
done
step "8. What happened to it" "$DETAIL"
printf '\nOpen Mailpit to read the email: http://localhost:8026\n'
