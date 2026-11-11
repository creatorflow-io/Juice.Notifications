#!/usr/bin/env bash
# Runs Juice.Notifications.App against the local stack: migrates the database, listens on http://localhost:5280.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
set -a; source "$here/.env"; set +a

export ConnectionStrings__Notifications="Host=127.0.0.1;Port=5434;Database=notifications;Username=notifications;Password=$POSTGRES_PASSWORD"
export Database__Migrate=true
export Auth__Authority=http://localhost:8081/realms/juice
export Auth__RequireHttpsMetadata=false
export ASPNETCORE_URLS=http://localhost:5280
export ASPNETCORE_ENVIRONMENT=Development

exec dotnet run --project "$here/../../src/Juice.Notifications.App" --no-launch-profile
