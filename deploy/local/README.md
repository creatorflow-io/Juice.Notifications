# Local stack

PostgreSQL, RabbitMQ, Keycloak and Mailpit for developing and testing Juice.Notifications. Ports avoid the Juice.Observability stack's, so both
can run at once.

| Component | Image (pinned) | Address | Used from |
|---|---|---|---|
| PostgreSQL | `postgres:17.10-alpine` | `localhost:5434`, database `notifications` | M1 |
| Keycloak | shared: `../../../../dev-infra` (19 Nov 2026) | http://localhost:8080, realm `juice`, one sign-in for every Juice app | M1 |
| RabbitMQ | `rabbitmq:4.1.4-management-alpine` | `localhost:5673`, UI http://localhost:15673 | M2 |
| Mailpit | `axllent/mailpit:v1.27.10` | SMTP `localhost:1026`, UI http://localhost:8026 | M6 |
| Audit host | built from `audit/` (Juice.Audit 10.6.1) | gRPC `localhost:5285` (h2c); records in schema `audit` of database `notifications` | p1-09 |
| Redis | `redis:7.4.2-alpine` | `localhost:6380` | M15: the inbox hub's SignalR backplane (`Inbox:SignalR:Redis`), for the two-replica test |

## Audit records and provisioning

The service sends its audit records (changes through the API, configuration provisioning) to `services/audit`'s gRPC host, which `audit/`
builds: `Audit:Endpoint`, required unless `Audit:Enabled` is false. Read them with psql:

```bash
docker compose exec postgres psql -U notifications -d notifications \
  -c 'SELECT "DateTime", "User", "TenantId", "Action", "Req_Method", "Req_Path", "Res_Status" FROM audit."AccessLog" ORDER BY "DateTime" DESC LIMIT 20' \
  -c 'SELECT "DateTime", "User", "Tbl", "Action", "Kvps" FROM audit."DataAudit" ORDER BY "DateTime" DESC LIMIT 20'
```

`provisioning.json` is the tenant setup the app loads at start (`Provisioning:Path`, phase-1-plan I12): its rows are `ManagedBy: config`, the
API refuses to change them, and a row removed from the file is handed back to the API, disabled. A secret only the deployment may name
(any variable or file) is never used for what the API sets: through the API a row is saved or enabled only when every secret it would use,
its integration's included, is one the API may name. Disabling an API-managed row always works (fields left out or sent back as they are);
a row the file manages changes only through the file. A file that gives an integration such a secret is refused while the API still has
enabled targets under it. Without `Provisioning:Path` nothing is provisioned and rows provisioned before are left alone; a file with no tenants hands
them all back. It sets up a demo tenant of its own
(`0b5e2f7a-4c1d-4e8a-9f3b-c0f1c0f1c0f1`), so the samples and `scripts/demo.sh` are not affected.

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

The container reads Keycloak's metadata from `keycloak:8080` (`Auth:MetadataAddress`) while tokens name `localhost:8080` as issuer, which
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

Approvals, the producer's side (the inbox decides nothing; the application hears the person's choice and closes the correlation):

```bash
dotnet run --project samples/Juice.Notifications.Samples.Producer -- listen   # stays up; Ctrl+C to stop
node console/e2e/send-approval.mjs [tess|alice]                              # an approval request (Approve / Reject)
```

`listen` gives the `sample` application a broker user (`sample`, allowed to bind only to `sample.*` on `juice.notifications.events`, as a
deployment would; password derived from `RABBITMQ_PASSWORD` unless `SAMPLE_BROKER_PASSWORD` is set) and names it on the application, which
is what makes the service raise its events. It binds a queue of its own to `sample.notification.interaction`, and on the first Approve or
Reject resolves the correlation with outcome `Approved` or `Rejected`. Every inbox item of it then shows the outcome instead of its buttons.

Contact addresses and device tokens are encrypted at rest under `NOTIFICATIONS_CONTACT_KEY`, with hashes under `NOTIFICATIONS_CONTACT_HASH_KEY`
(both base64 32-byte keys in `.env`, passed by `run-app.sh` and the `app` service). The service doesn't start without them. A `.env` made before
16 Nov 2026 lacks them: `scripts/init.sh` adds them and keeps the rest.

## Clients in realm `juice`

| Client | Grant | Roles | Use |
|---|---|---|---|
| `notifications-admin` | client credentials | `notifications-platform-admin` | register applications, link tenants |
| `sample-producer` | client credentials | none | send notifications as an application (from M2) |
| `tenant-admin` | client credentials | `notifications-tenant-admin`, `platform_tenant` = `0193f0aa-0000-7000-8000-0000000000a1` | configure that one tenant's delivery (the tests' tenant) |
| `people-test` | password (public client) | none, `platform_tenant` = the person's own attribute | sign in the person accounts (password `PEOPLE_TEST_PASSWORD`) for the person's API, `me`, which refuses a client's own token |
| `juice-layout-demo` | code with PKCE (public client) | none, `platform_tenant` = the person's own attribute | the `juice-layout` demo app (`ng serve -c notifications`, `https://localhost:4201`), for `@juice-js/notifications`; the API allows that origin (`Cors:Origins`) |
| `notifications-console` | code with PKCE (public client) | the person's realm roles, `platform_tenant` = their own attribute | the Notifications Console (phase-4c-plan I51), served by the App on `http://localhost:5280` or by `ng serve` on `http://localhost:4300`; same origin as the API, so no CORS |
| `notifications-directory` | client credentials | realm-management `view-users`, `view-realm` | the identity resolver's directory `keycloak` (the app's `Identity:Directories:keycloak`): reads users, groups and realm roles |

The app knows these two tenants, both active (`Tenancy:Directory` `Configured`, in `docker-compose.yml`): the tenant pattern refuses a
request for any other tenant (`tenant_forbidden`, or `tenant_not_found` to a platform admin or a client's own token), and a tenant listed with another `Status`
(`Suspended`, say) has its requests refused and its deliveries held.

The person accounts belong to a tenant through the user attribute `platform_tenant` (declared in the realm's user profile, admin-only):

| Person | Tenant | Email | Group `ops` | Realm role `on-call` |
|---|---|---|---|---|
| `ann` | `…a1` (the tests') | verified | yes | |
| `ben` | `…a1` | verified | | yes |
| `dora` | `…a1` | not verified | yes | |
| `eli` (disabled) | `…a1` | verified | yes | yes |
| `cara` | `…b2` | verified | yes | yes |
| `tess` | `…a1` | verified | | |
| `paul` | `…a1` | verified | | |

`tess` is a tenant admin of `…a1` (realm role `notifications-tenant-admin`) and `paul` a platform admin (`notifications-platform-admin`):
the Console's admins, signed in with `PEOPLE_TEST_PASSWORD`. With the shared Keycloak, `tess` also opens Juice Observability as a viewer,
and Observability's `alice` is a platform admin here (dev-infra's README lists everyone).

A tenant reaches them through a resolver of kind `identity` naming the directory: `PUT /api/v1/tenants/{id}/resolvers/people
{ "kind": "identity", "settings": { "directory": "keycloak" } }`, then recipients such as `{ "kind": "group", "id": "ops", "resolver": "people" }`.

Producers must authenticate as their client: the API accepts a producer token only when its `client_id` claim (set by the `juice-notifications`
scope on client-credentials tokens only) equals its `azp`. 
**Keycloak is shared** (19 Nov 2026): the realm is `dev-infra/keycloak/juice-realm.json`, with Juice Observability's and the juice-layout
demo's clients beside this service's, imported each time its container is created (this stack used its own on :8081, kept up to date by
`scripts/keycloak-update.sh`; both are gone). Start it before this stack, whose app joins its `juice-dev` network:

```bash
(cd ../../../../dev-infra && scripts/init.sh && docker compose up -d --wait)
```

Its `init.sh` takes the secrets from this `.env` and says if the two differ.

Their secrets are in `.env`. A token for the admin client:

```bash
source .env
curl -s -d grant_type=client_credentials -d client_id=notifications-admin -d "client_secret=$NOTIFICATIONS_ADMIN_CLIENT_SECRET" \
  http://localhost:8080/realms/juice/protocol/openid-connect/token | jq -r .access_token
```

## Events to producers

With `Events__Enabled` (on in the `app` service), the service publishes what happens to an application's notifications (design §5.4):
`interaction`, `delivery.failed`, `escalation.exhausted`, `recipient.unresolved`, `correlation.acknowledged|resolved|cancelled`, on the
topic exchange `juice.notifications.events` with the routing key `{application}.notification.{event}`. Only applications with a
`brokerUser` get events. Each event's message id is its event id: delivery is at least once, so deduplicate on it.

An application reads its own events only: its broker user may bind just `{application}.#` (RabbitMQ topic permissions, design §19
decision 12). For the sample application, whose broker user also publishes to the intake:

```bash
# queues of its own (named sample.*), publish to the intake, read the events exchange
docker compose exec rabbitmq rabbitmqctl set_permissions -p / sample-producer '^sample\..*$' '^(notifications|sample\..*)$' '^(juice\.notifications\.events|sample\..*)$'
# bind only to its own application's events
docker compose exec rabbitmq rabbitmqctl set_topic_permissions -p / sample-producer juice.notifications.events '^$' '^sample\.'
# then declare a queue sample.notifications and bind it to juice.notifications.events with sample.notification.#
```

`ProducerEventBrokerTests` checks this on the broker: the application hears its events, and binding to another application's events
or to `#` is refused.

## RabbitMQ intake

`run-app.sh` and the `app` service turn on the RabbitMQ intake (`Intake__RabbitMQ__*`). The service connects as `notifications`, the broker's
default user, and declares its topology at start:

| Name | Kind | Purpose |
|---|---|---|
| `notifications` | direct exchange | producers publish `NotificationRequested` here, routing key `NotificationRequested` |
| `notifications.requests` | queue | the intake; a failed message dead-letters to the delay queue |
| `notifications.requests.retry` | fanout exchange and queue | holds a failed message for `RetryDelay` (30 s), then returns it to `notifications` |
| `notifications.requests.parked` | fanout exchange and queue | a message still failing after `ParkAfter` (1 day), or one that can't be read; kept until removed by hand |

A parked message is a copy the service published, so it has no `user_id`: inspect it (its `x-death-reason` header says why), and have the
producer send it again once fixed. Published back to `notifications` as it is, it would be refused.

A producer publishes as a broker user of its own, which the broker checks against the message's `user_id` (Juice:
`AddProducer(..., p => p.StampUserId())`), and the application names that user as its `brokerUser`. A broker user for an application that
may only publish to the intake:

```bash
docker compose exec rabbitmq rabbitmqctl add_user sample-producer "<password>"
docker compose exec rabbitmq rabbitmqctl set_permissions -p / sample-producer '^$' '^notifications$' '^$'
# then, as a platform admin: PUT /api/v1/applications/sample { "brokerUser": "sample-producer" }
```

`/health/ready` includes the intake's consumer (`rabbitmq`): Degraded, never unhealthy, while it isn't consuming (broker away, or the
consumer cancelled and restarting), so HTTP stays in rotation.

To change `RetryDelay` or the queues' arguments (a quorum queue, say), delete the queues that differ first: the service refuses to start when
the broker already has them with other arguments. The delay queue can go at any time; move or drain `notifications.requests` before
deleting it.

A message without `user_id`, or from a broker user no application has, is refused: logged and counted, not retried. A broker user with the
`impersonator` tag can stamp any `user_id`, so producers never get it.

## Tests

The integration tests read `.env`, create a scratch database per test class on this PostgreSQL, and drop it afterwards. The RabbitMQ intake
tests do the same with a virtual host and two producer users on the broker, through its management API. Without the stack they are skipped.
