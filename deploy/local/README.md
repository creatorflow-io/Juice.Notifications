# Local stack

PostgreSQL, RabbitMQ, Keycloak and Mailpit for developing and testing Juice.Notifications. Ports avoid the Juice.Observability stack's, so both
can run at once.

| Component | Image (pinned) | Address | Used from |
|---|---|---|---|
| PostgreSQL | `postgres:17.10-alpine` | `localhost:5434`, database `notifications` | M1 |
| Keycloak | `quay.io/keycloak/keycloak:26.7.5` | http://localhost:8081, realm `juice` | M1 |
| RabbitMQ | `rabbitmq:4.1.4-management-alpine` | `localhost:5673`, UI http://localhost:15673 | M2 |
| Mailpit | `axllent/mailpit:v1.27.10` | SMTP `localhost:1026`, UI http://localhost:8026 | M6 |

## Start

Needs Docker with Compose, bash (Git Bash on Windows) and openssl.

```bash
scripts/init.sh                       # once: .env with random secrets
docker compose up -d --wait
scripts/run-app.sh                    # the service on http://localhost:5280, migrating the database first
```

Or run the service as its container image instead of `run-app.sh` (same port; stop it before running `run-app.sh`):

```bash
docker compose --profile app up -d --build --wait
docker compose --profile app stop app
```

The container reads Keycloak's metadata from `keycloak:8080` (`Auth:MetadataAddress`) while tokens name `localhost:8081` as issuer, which
Keycloak's `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` allows.

## Samples

With the service running, from `services/notifications`:

```bash
deploy/local/scripts/demo.sh                                  # curl: register, configure email, declare, send
dotnet run --project samples/Juice.Notifications.Samples.Producer
```

The sample producer also receives a signed webhook on this machine's private address and verifies it, as a receiver would. The service signs
with `SAMPLE_WEBHOOK_SECRET` from `.env` (passed to it as `NOTIFICATIONS_SECRET_SAMPLE_HOOK` by `run-app.sh` and the `app` service). It
exits non-zero when a delivery doesn't arrive.

## Clients in realm `juice`

| Client | Grant | Roles | Use |
|---|---|---|---|
| `notifications-admin` | client credentials | `notifications-platform-admin` | register applications, link tenants |
| `sample-producer` | client credentials | none | send notifications as an application (from M2) |
| `tenant-admin` | client credentials | `notifications-tenant-admin`, `platform_tenant` = `0193f0aa-0000-7000-8000-0000000000a1` | configure that one tenant's delivery (the tests' tenant) |

Producers must authenticate as their client: the API accepts a producer token only when its `client_id` claim (set by the `juice-notifications`
scope on client-credentials tokens only) equals its `azp`. A stack created before 11 Nov 2026 lacks that mapper and the `tenant-admin` client, because Keycloak imports the realm only into an
empty volume. Bring it up to date (safe to repeat):

```bash
scripts/init.sh && scripts/keycloak-update.sh
```

Their secrets are in `.env`. A token for the admin client:

```bash
source .env
curl -s -d grant_type=client_credentials -d client_id=notifications-admin -d "client_secret=$NOTIFICATIONS_ADMIN_CLIENT_SECRET" \
  http://localhost:8081/realms/juice/protocol/openid-connect/token | jq -r .access_token
```

## RabbitMQ intake

`run-app.sh` and the `app` service turn on the RabbitMQ intake (`Intake__RabbitMQ__*`). The service connects as `notifications`, the broker's
default user, and declares its topology at start:

| Name | Kind | Purpose |
|---|---|---|
| `notifications` | direct exchange | producers publish `NotificationRequested` here, routing key `NotificationRequested` |
| `notifications.requests` | queue | the intake; a failed message dead-letters to the delay queue |
| `notifications.requests.retry` | fanout exchange and queue | holds a failed message for `RetryDelay` (30 s), then returns it to `notifications` |

A producer publishes as a broker user of its own, which the broker checks against the message's `user_id` (Juice:
`AddProducer(..., p => p.StampUserId())`), and the application names that user as its `brokerUser`. A broker user for an application that
may only publish to the intake:

```bash
docker compose exec rabbitmq rabbitmqctl add_user sample-producer "<password>"
docker compose exec rabbitmq rabbitmqctl set_permissions -p / sample-producer '^$' '^notifications$' '^$'
# then, as a platform admin: PUT /api/v1/applications/sample { "brokerUser": "sample-producer" }
```

A message without `user_id`, or from a broker user no application has, is refused: logged and counted, not retried. A broker user with the
`impersonator` tag can stamp any `user_id`, so producers never get it.

## Tests

The integration tests read `.env`, create a scratch database per test class on this PostgreSQL, and drop it afterwards. The RabbitMQ intake
tests do the same with a virtual host and two producer users on the broker, through its management API. Without the stack they are skipped.
