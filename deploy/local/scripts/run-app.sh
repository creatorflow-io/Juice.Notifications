#!/usr/bin/env bash
# Runs Juice.Notifications.App against the local stack: migrates the database, listens on http://localhost:5280.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
set -a; source "$here/.env"; set +a

export ConnectionStrings__Notifications="Host=127.0.0.1;Port=5434;Database=notifications;Username=notifications;Password=$POSTGRES_PASSWORD"
export Database__Migrate=true
export Auth__Authority="http://${PUBLIC_HOST:-localhost}:8080/realms/juice"  # the shared Keycloak's issuer (dev-infra)
export Auth__RequireHttpsMetadata=false
export ASPNETCORE_URLS=http://localhost:5280
export ASPNETCORE_ENVIRONMENT=Development
# The signing secret the sample producer's webhook integration names (env:NOTIFICATIONS_SECRET_SAMPLE_HOOK).
export NOTIFICATIONS_SECRET_SAMPLE_HOOK="${SAMPLE_WEBHOOK_SECRET:-}"
# Contact addresses and device tokens are encrypted under these (Encryption:KeyRef and HashKeyRef in appsettings.json name them).
export NOTIFICATIONS_CONTACT_KEY NOTIFICATIONS_CONTACT_HASH_KEY
# NotificationRequested over RabbitMQ, from the stack's broker.
export Intake__RabbitMQ__Enabled=true
export Intake__RabbitMQ__Host=127.0.0.1
export Intake__RabbitMQ__Port=5673
export Intake__RabbitMQ__UserName=notifications
export Intake__RabbitMQ__Password="$RABBITMQ_PASSWORD"
# Audit records to the stack's audit host (gRPC), and the tenant setup kept with the deployment.
export Audit__Endpoint=http://127.0.0.1:5285
export Provisioning__Path="$here/provisioning.json"

exec dotnet run --project "$here/../../src/Juice.Notifications.App" --no-launch-profile
