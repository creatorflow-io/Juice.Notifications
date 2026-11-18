# Juice.Notifications

A multi-tenant notification service for Juice products. A product says "this happened"; Juice.Notifications works out who should hear about it, on
which channels, in what words and when, then sends it, retries it and records what happened to each delivery.

- Design: [`docs/design.md`](docs/design.md)
- Phase 1 plan: [`docs/phase-1-plan.md`](docs/phase-1-plan.md)
- Deferred findings: [`docs/deferred.md`](docs/deferred.md)
- Setting up providers (SES, SendGrid, …): [`docs/providers.md`](docs/providers.md)

## Status

Phase 1 (core) is in progress. Working end to end today:

- applications, versioned notification types (JSON Schema 2020-12 data, Scriban templates in a sandbox) and HTTP intake with idempotency;
- tenant configuration: integrations, targets, routes and the outbound allowlist, for platform admins and the tenant's own admins;
- the pipeline: planner, delivery planner and dispatcher, as claim-based workers (PostgreSQL `SKIP LOCKED`, version fencing);
- delivery by email (SMTP servers from the deployment) and signed webhooks (SSRF policy), with retries and every attempt recorded.
- delivery history (`GET …/deliveries`, `GET …/deliveries/{id}`) and dead-letter retry (`POST …/deliveries/{id}/retry`);
- metrics and traces (`Juice.Notifications` meter and activity source), exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; a Dockerfile.

- RabbitMQ intake: `NotificationRequested` (`Juice.Notifications.Messaging`) accepted like an HTTP request, for the application whose
  `brokerUser` published it (`Intake:RabbitMQ`, off by default; [`deploy/local/README.md`](deploy/local/README.md#rabbitmq-intake)).

- configuration provisioning: a tenant's integrations, targets, routes and allowlist from a JSON file loaded at start (`Provisioning:Path`);
  the API refuses changes to those rows;
- audit records to `services/audit` (`Audit:Endpoint`, its gRPC host; required unless `Audit:Enabled` is false): every change through the API
  with who made it, and the configuration rows it changed, once committed. Producers' notification requests are not audited.

Progress is tracked in [`docs/tracking.md`](docs/tracking.md); the phase 1 gate is recorded in
[`docs/phase-1-acceptance.md`](docs/phase-1-acceptance.md).

## Build and test

```bash
dotnet tool restore
dotnet build Juice.Notifications.slnx
dotnet test Juice.Notifications.slnx
```

Integration tests need the local stack (`deploy/local`); without it they are skipped.

```bash
deploy/local/scripts/init.sh                       # once: .env with random secrets
docker compose -f deploy/local/docker-compose.yml up -d --wait
dotnet test Juice.Notifications.slnx
```

## Run the service

```bash
deploy/local/scripts/run-app.sh                    # migrates the database and listens on http://localhost:5280
deploy/local/scripts/demo.sh                       # in another shell: register, configure, send; then open http://localhost:8026
```

A stack created before 11 Nov 2026 needs `deploy/local/scripts/keycloak-update.sh` once (see [`deploy/local/README.md`](deploy/local/README.md)).

## Migrations

```bash
dotnet ef migrations add <Name> --project src/Juice.Notifications.EF.PostgreSQL --startup-project src/Juice.Notifications.EF.PostgreSQL
```
