// M42 (phase-4c-plan proof point 6): no secret appears in the Console, on a page or in an API answer it reads. The secrets are the stack's
// own values (deploy/local/.env: passwords, client secrets, encryption keys) and the tests' webhook signing secret, which a seeded webhook
// integration names by reference (env:NOTIFICATIONS_SECRET_TEST_HOOK) and has signed a delivery with. tess walks every page of her tenant
// (detail pages by the seeded rows' ids) and paul every page of it and the applications; every page's text and every answer either saw is
// searched for each secret (as written, JSON-escaped or URL-encoded), and so is every field's value and what the hubs push. The pages are
// the Console's own route list (checked against it). The integration's page shows its reference, as given.
import { launch, signIn, close, check, finish, settled, base, env, testTenant, receiver, seedTenant, unseed, tenantPages,
  collectAnswers, uncoveredTenantRoutes, holds } from './lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const secrets = [...new Set([
  ...Object.entries(env).filter(([k]) => /SECRET|PASSWORD|KEY/.test(k)).map(([, v]) => v),
  'test-signing-secret',
])].filter(v => v && v.length >= 8);
check(secrets.length >= 8, `secrets to look for: ${secrets.length}`, problems);
const found = text => secrets.filter(s => holds(text, s));
// "Every page": the walked list is the Console's own route list, no page left out.
const routes = uncoveredTenantRoutes();
check(routes.missing.length === 0 && routes.extra.length === 0,
  `the walk walks all ${routes.declared} tenant routes the Console declares (missing ${routes.missing.join(', ') || 'none'}; extra ${routes.extra.join(', ') || 'none'})`, problems);

const hook = await receiver();
hook.failing = false;
const ids = await seedTenant(testTenant, run, hook);
// The delivery was signed with the secret: it was read and used, so it was there to leak.
await new Promise(r => setTimeout(r, 1000));
check(hook.received.some(r => r.signature), `the seeded webhook arrived signed (${hook.received.length} received)`, problems);

const browser = await launch();
const refused = [];
try {
  for (const [user, extra] of [['tess', []], ['paul', ['/applications', '/applications/sample', `/applications/notifications`]]]) {
    const page = await signIn(browser, user, problems, { path: `/tenants/${testTenant}`, onRefused: e => refused.push(e) });
    // The page signed in on (the overview) loaded first, so the walk doesn't cut its answers off; from here on every answer is read (the
    // walk opens the overview again).
    await page.getByTestId('overview-tiles').waitFor({ timeout: 15000 }).catch(() => {});
    await page.waitForLoadState('networkidle').catch(() => {});
    const collected = collectAnswers(page);
    let leaked = [];
    const routes = [...tenantPages(testTenant, ids).map(p => p.route), ...extra];
    for (const route of routes) {
      await page.goto(`${base}${route}`);
      await page.waitForLoadState('networkidle').catch(() => {});
      if (route.endsWith(`/integrations/${ids.integration}`)) await page.getByTestId('integration-secret-ref').waitFor({ timeout: 10000 }).catch(() => {});
      // The page's text and what its fields hold (a form filled from what was read).
      const fields = await page.locator('input, textarea').evaluateAll(es => es.map(e => e.value).join('\n'));
      const text = `${await page.locator('body').innerText()}\n${fields}`;
      if (route.endsWith(`/integrations/${ids.integration}`)) {
        // In its form's field (inputs are not the page's text): the reference, as given.
        check(await page.getByTestId('integration-secret-ref').inputValue().catch(() => '') === 'env:NOTIFICATIONS_SECRET_TEST_HOOK', `${user} sees the integration's secret reference, as given`, problems);
      }
      leaked = leaked.concat(found(text).map(s => `${route}: ${s.slice(0, 3)}…`));
      // Its answers read before the next page: leaving cuts off a body still coming (a refresh, a preview), and that one would go unsearched.
      await collected.read();
    }
    check(leaked.length === 0, `${user}: no secret on any of ${routes.length} pages (${leaked.join('; ') || 'none'})`, problems);
    // Every body read before searching, and none from the API left unread.
    const { answers, unread } = await collected.read();
    const api = answers.filter(a => a.url.includes('/api/'));
    const inAnswers = answers.flatMap(a => found(a.text ?? '').map(s => `${a.url.replace(base, '')}: ${s.slice(0, 3)}…`));
    check(api.length > routes.length && unread.length === 0 && inAnswers.length === 0,
      `${user}: no secret in any of ${answers.length} answers (${api.length} from the API, ${answers.filter(a => a.pushed).length} pushed by the hubs) (${inAnswers.join('; ') || 'none'}; unread ${unread.map(a => a.url).join(', ') || 'none'})`, problems);
    await close(page);
  }
} finally {
  await browser.close();
  await hook.close();
  await unseed(testTenant, ids).catch(e => problems.push(`unseed: ${e.message}`));
}
await settled();
check(refused.length === 0, `no API call refused (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
