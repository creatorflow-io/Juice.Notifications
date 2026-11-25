# Juice.Notifications

A multi-tenant notification service for Juice products. A product says "this happened"; Juice.Notifications works out who should hear about it, on
which channels, in what words and when, then sends it, retries it and records what happened to each delivery.

- Design: [`docs/design.md`](docs/design.md)
- Phase 1 plan: [`docs/phase-1-plan.md`](docs/phase-1-plan.md)
- Deferred findings: [`docs/deferred.md`](docs/deferred.md)
- Setting up providers (SES, SendGrid, …): [`docs/providers.md`](docs/providers.md)

## Status

Phases 1 to 4 are accepted ([`docs/phase-4-acceptance.md`](docs/phase-4-acceptance.md)); SQL Server is phase 4b. Phase 1 brought:

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

## Packages and CI

Besides the image, the service ships as libraries for a host app of the customer's own:

| Package | Feed | Contents |
|---|---|---|
| `Juice.Notifications.Api` (NuGet) | nuget.org; alpha/beta on GitHub Packages | `AddJuiceNotifications()`, `UseJuiceNotifications()`, `MapJuiceNotifications()`: the API and the workers. Depends on `Juice.Notifications` and `.Api.Contracts`; a host adds `.EF.PostgreSQL` (the database), `.Messaging` (RabbitMQ intake) and the `.Providers.*` it sends with, all published |
| `Juice.Notifications.Embedded` (NuGet) | as above | `AddJuiceNotificationsEmbedded()`: the pipeline inside a product's own host and PostgreSQL (design §4.2), with `INotificationClient` writing in the host's transaction. No authentication, tenancy middleware, people, inbox, RabbitMQ or `services/audit`; the host gives its transaction (`INotificationTransaction`) and, if it keeps one, an audit sink (`INotificationsAuditSink`) |
| `@juice-js/notifications-admin` (npm) | npmjs | `provideNotificationsAdmin()`, `NOTIFICATIONS_ADMIN_ROUTES`, `notificationsAdminMenus()` and the page styles |

`src/Juice.Notifications.App` and `console/src` are the reference host and app: they use the packages the way a customer would.

Publishing (`.github/workflows`, as in Juice.Observability): the backend and the console library are versioned and released independently.

| Package | Its next version lives in | Release tag |
|---|---|---|
| `Juice.Notifications.*` | `<VersionPrefix>` in `Directory.Build.props` | `api-<version>` |
| `@juice-js/notifications-admin` | `"version"` in `console/projects/juice-js/notifications-admin/package.json` | `console-<version>` |

| You do | You get |
|---|---|
| push to `main`, `dev/**` | build and test only (`build.yml`) |
| push to `release/**` that changes `src/**` (or the props / `nuget.config`) | build and test, then, if they pass, NuGet alphas at `<VersionPrefix>-alpha.<run>` on GitHub Packages (`build.yml`) |
| push to `release/**` that changes `console/projects/juice-js/notifications-admin/**` | build and test, then, if they pass, an npm alpha at `<version>-alpha.<run>` under `@alpha` (`build.yml`); run Build by hand on the branch to publish both |
| create a GitHub release on `main` tagged `api-0.1.0` | the backend at 0.1.0 on nuget.org (`release.yml`) |
| create a GitHub release on `main` tagged `console-0.1.0` | the console library at 0.1.0 on npm under `latest` |
| the same, on `release/0.x` | a patch of an older line; npm under `release-0.x`, never replacing `latest` |
| the same, with a pre-release version (`api-0.1.0-beta.1`, `console-0.1.0-rc.1`) | a beta or rc: nuget.org as a pre-release, npm under the label (`beta`, `rc`) |

Each next version is the one to release: raise it after a release so alphas sort above it. Tags have no `v`. nuget.org takes
the push through trusted publishing: its policy names owner `creatorflow-io`, repository `Juice.Notifications`, workflow
`release.yml`.

The console library calls the API over HTTP, so record which versions work together:

| `@juice-js/notifications-admin` | needs `Juice.Notifications.Api` |
|---|---|
| 0.1.x | 0.1.x |

## Run the service

```bash
deploy/local/scripts/run-app.sh                    # migrates the database and listens on http://localhost:5280
deploy/local/scripts/demo.sh                       # in another shell: register, configure, send; then open http://localhost:8026
```

Keycloak is the Juice projects' shared one (`../../dev-infra`, since 19 Nov 2026): start it first (see [`deploy/local/README.md`](deploy/local/README.md)).

## Migrations

```bash
dotnet ef migrations add <Name> --project src/Juice.Notifications.EF.PostgreSQL --startup-project src/Juice.Notifications.EF.PostgreSQL
```
