// M36 and M42 (phase-4c-plan proof point 2): someone signed in without an admin role (ann) gets the no-access page from the home, a
// tenant's deep link and a menu link, saying why, and no tenant data: the access read is answered (none) and nothing is refused. Every page:
// each tenant page (detail pages by real ids) and the applications are the no-access page for her, and her token is refused every read they
// start from, which paul (platform admin) is answered.
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, receiver, seedTenant, unseed, tenantPages, personToken,
  attempt, uncoveredTenantRoutes } from './lib.mjs';

const problems = [];
const refused = [];
// Proof point 2, every page: rows of the tests' tenant for its detail pages, and the reads they start from (paul is answered each; ann none).
const run = Date.now().toString(36);
const hook = await receiver();
hook.failing = false;
const seeded = await seedTenant(testTenant, run, hook);
const pages = tenantPages(testTenant, seeded);
// "Every page": the walked list is the Console's own route list, no page left out.
const routes = uncoveredTenantRoutes();
check(routes.missing.length === 0 && routes.extra.length === 0,
  `ann walks all ${routes.declared} tenant routes the Console declares (missing ${routes.missing.join(', ') || 'none'}; extra ${routes.extra.join(', ') || 'none'})`, problems);
const paulToken = await personToken('paul');
const annToken = await personToken('ann');
const real = [];
for (const { api } of pages) real.push((await attempt(paulToken, 'GET', api)).status);
check(real.every(s => s === 200), `paul is answered every read the tenant's pages start from (${[...new Set(real)].join(', ')})`, problems);
for (const { api } of [...pages, { api: '/api/v1/applications' }, { api: '/api/v1/applications/sample' }]) {
  const r = await attempt(annToken, 'GET', api);
  check(r.status === 403, `ann is refused GET ${api.replace(`/api/v1/tenants/${testTenant}/`, '…a1/')} (${r.status})`, problems);
}

const browser = await launch();
try {
  for (const [path, reason, text] of [['/', 'none', 'no-access'], [`/tenants/${testTenant}`, 'tenant', 'no-access-tenant'], ['/go/deliveries', 'none', 'no-access']]) {
    const page = await signIn(browser, 'ann', problems, { path, onRefused: e => refused.push(e) });
    await page.waitForURL(/\/no-access\?reason=/, { timeout: 15000 }).catch(() => {});
    const reached = new URL(page.url());
    check(reached.pathname === '/no-access' && reached.searchParams.get('reason') === reason && reached.searchParams.get('returnUrl') === path,
      `ann at ${path} gets the no-access page, reason ${reason}, with her link (${page.url()})`, problems);
    await page.getByTestId(text).waitFor({ timeout: 10000 }).catch(() => {});
    check(await page.getByTestId(text).count() === 1, `ann at ${path} is told why (${text})`, problems);
    check(await page.getByTestId('tenant-bar').count() === 0, `ann at ${path} sees no tenant`, problems);
    await shot(page, `no-access${path.replace(/\//g, '-')}`);
    if (reason === 'tenant') {
      // Try again: the access afresh, then her link; still not hers, so back on this page for that link (not stuck, not the home).
      const reads = () => page.apiAnswers.filter(a => a.url.endsWith('/api/v1/access')).length;
      const before = reads();
      await page.getByTestId('no-access-retry').click();
      await page.waitForURL(u => u.pathname !== '/no-access' || u.searchParams.get('returnUrl') !== path, { timeout: 3000 }).catch(() => {});
      await page.waitForTimeout(500);
      const again = new URL(page.url());
      check(again.pathname === '/no-access' && again.searchParams.get('reason') === 'tenant' && again.searchParams.get('returnUrl') === path,
        `ann's try again goes back to ${path} and is refused again (${page.url()})`, problems);
      check(reads() > before, `ann's try again reads her access afresh (${before} → ${reads()})`, problems);
    }
    await close(page);
  }
  // Every page by its link, in one signed-in tab: the no-access page each time, with no tenant shown and no call refused (none made).
  const ann = await signIn(browser, 'ann', problems, { path: '/', onRefused: e => refused.push(e) });
  await ann.waitForURL(/\/no-access\?reason=/, { timeout: 15000 }).catch(() => {});
  for (const [route, reason] of [...pages.map(p => [p.route, 'tenant']), ['/applications', 'platform-only'], ['/applications/sample', 'platform-only']]) {
    await ann.goto(`${base}${route}`);
    await ann.waitForURL(/\/no-access\?reason=/, { timeout: 15000 }).catch(() => {});
    await ann.getByTestId(reason === 'tenant' ? 'no-access-tenant' : 'no-access-platform-only').waitFor({ timeout: 10000 }).catch(() => {});
    const reached = new URL(ann.url());
    check(reached.pathname === '/no-access' && reached.searchParams.get('reason') === reason && reached.searchParams.get('returnUrl') === route
      && await ann.getByTestId('tenant-bar').count() === 0, `ann at ${route.replace(testTenant, '…a1')}: no access (${reason})`, problems);
  }
  await close(ann);
} finally {
  await browser.close();
  await hook.close();
  await unseed(testTenant, seeded).catch(e => problems.push(`unseed: ${e.message}`));
}
await settled();
check(refused.length === 0, `no API call refused once signed in (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
