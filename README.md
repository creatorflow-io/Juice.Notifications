# Juice.Notifications

A single-instance ASP.NET Core notification service. Each tenant has its own API key,
notification types, destinations, and history. Producers declare a type with fields
of type `string`, `number`, or `boolean` and plain-text subject/body templates. A
notification is routed to every email address and webhook configured for its tenant.

## Run and configure

Requires .NET 10. Run `dotnet run` and configure the service using ASP.NET Core
configuration (environment variables, a private configuration file, or a secret
store). For example, environment variable keys include:

```
Notifications__StateFile=/var/lib/juice-notifications/state.json
Notifications__MaxAttempts=5
Notifications__RetryDelaySeconds=30
Notifications__Smtp__Host=smtp.example.com
Notifications__Smtp__Port=587
Notifications__Smtp__EnableSsl=true
Notifications__Smtp__From=notifications@example.com
Notifications__Smtp__Username=...
Notifications__Smtp__Password=...
Notifications__Tenants__acme__ApiKey=...
Notifications__Tenants__acme__Emails__0=recipient@example.com
Notifications__Tenants__acme__Webhooks__orders__Url=https://example.com/notifications
Notifications__Tenants__acme__Webhooks__orders__Secret=...
```

Use distinct, strong randomly generated API keys and webhook secrets. Configure
destinations only from trusted sources, use HTTPS for API and webhook traffic,
and protect configuration and the state file with appropriate filesystem
permissions. Neither keys nor webhook secrets are stored in the state file.

The state file defaults to `data/notifications.json`. It contains notification
data and delivery history and must be persisted and backed up. Run only one
service instance against a state file: it is not a distributed queue. A delivery
may be repeated after a crash between sending and recording its result; consumers
should deduplicate using the notification ID. Pending deliveries survive restarts.

## API

All endpoints require the tenant's `X-API-Key` header. JSON requests use
`Content-Type: application/json`.

* `PUT /tenants/{tenantId}/types/{name}` declares or replaces a type:
  `{"fields":{"customer":"string","count":"number"},"subject":"Hello {{customer}}","body":"You have {{count}} items."}`.
  Templates substitute declared fields literally; no HTML escaping is performed.
* `POST /tenants/{tenantId}/notifications` accepts
  `{"type":"welcome","data":{"customer":"Ada","count":2}}`. Data must contain
  exactly the declared fields with matching types. The response is `202` with
  the notification and its ID.
* `GET /tenants/{tenantId}/notifications/{id}` returns the notification and
  per-destination status and attempts. `GET /tenants/{tenantId}/notifications`
  returns the tenant's full history.

Each delivery starts as `pending`, becomes `delivered` after success, or `failed`
after the configured maximum attempts (default 5). Failures retry after an
exponential delay starting at `RetryDelaySeconds` (default 30), capped at one day.
Changing a type does not change already accepted notifications.

Webhook requests are JSON POSTs. `X-Notification-Timestamp` is the Unix timestamp
in seconds, and `X-Notification-Signature` is `sha256=` followed by the lowercase
hex HMAC-SHA256 of the UTF-8 bytes of `<timestamp>.<raw request body>` using the
configured secret. Receivers should verify the signature in constant time, reject
stale timestamps, and deduplicate by `id`. Only 2xx responses count as success;
redirects are not followed.