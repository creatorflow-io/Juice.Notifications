# Notifications Console

The admin Console of Juice.Notifications (`docs/phase-4c-plan.md`): an Angular workspace on juice-layout, as Juice.Observability's Console.

- `src/`: the app, `notifications-console`: the juice-layout shell, sign-in (`public/config.json`: issuer, client, scope), the admin's
  own bell (`@juice-js/notifications`).
- `projects/juice-js/notifications-admin/`: the pages, `@juice-js/notifications-admin`, mounted by the app at `/`.
- `e2e/`: browser journeys over the stack (Edge through `playwright-core`; `BROWSER=` another Chromium).

The App serves the built Console from its web root on the API's own origin (`Dockerfile`, stage `console`).

```bash
npm ci
npx ng build notifications-console          # dist/notifications-console/browser
npx ng serve                                 # http://localhost:4300, /api and /hubs proxied to the App on 5280

# With the stack and the app container up (deploy/local: docker compose --profile app up -d --build --wait):
node e2e/signin.mjs                          # tess (tenant admin) and paul (platform admin) sign in and land in a tenant
node e2e/no-access.mjs                       # ann (no admin role) gets the no-access page on every page; her token is refused their reads
node e2e/tenant.mjs                          # tess opens every page of another tenant: none of it shown, every read and change refused
node e2e/secrets.mjs                         # no secret on any page, in any field or in any answer, for tess and paul
node e2e/deliveries.mjs                      # a failed delivery found, inspected, retried under its id; a bulk replay of a filter
node e2e/why-not.mjs                         # a muted, opted-out and deferred recipient, each with why
node e2e/correlations.mjs                    # a correlation found, acknowledged and resolved
node e2e/configure.mjs                       # tess sets up email and webhook delivery in the browser; both arrive
node e2e/content.mjs                         # types, a template edited with its preview, display groups
node e2e/people.mjs                          # subscribers, local users, groups, topics, resolvers
node e2e/platform.mjs                        # tenant settings; applications and their tenants (paul)

# The phase 4c demo: a small story set up through the API, then walked in the browser (it prints the links).
../deploy/local/scripts/demo-console.sh
```

The local realm's Console admins are `tess` and `paul` (password `PEOPLE_TEST_PASSWORD` in `deploy/local/.env`).
