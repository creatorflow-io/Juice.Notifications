// M42 (phase-4c-plan proof point 1): a tenant admin sees and changes only their tenant. cara's tenant (…b2) gets rows of its own (a webhook
// integration, a notification with a correlation and its delivery, a local user). tess (the tests' tenant's admin) opens every page of it by
// its URL, detail pages by those rows' ids: each is the no-access page ("not yours"), with none of the rows on it and no API call made for
// it. Her token is refused every read those pages start from (paul, platform admin, is answered each), and a change there (settings, a
// route, a replay, a retry); and her own tenant's lists, walked with every answer read, never name cara's rows.
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, receiver, seedTenant, unseed, tenantPages, personToken, attempt,
  collectAnswers, uncoveredTenantRoutes, holds } from './lib.mjs';

const run = Date.now().toString(36);
const otherTenant = '0193f0aa-0000-7000-8000-0000000000b2';
const problems = [];
const hook = await receiver();
hook.failing = false;
const ids = await seedTenant(otherTenant, run, hook);
check(Object.values(ids).every(Boolean), `cara's tenant has its rows (${JSON.stringify(ids)})`, problems);
// What only cara's tenant has: none of it may reach tess.
const marks = [ids.integration, ids.notification, ids.delivery, ids.subscriber, ids.correlation, `Seed ${run}`];
const leaks = text => marks.filter(m => m && holds(text, m));
// "Every page": the walked list is the Console's own route list, no page left out.
const routes = uncoveredTenantRoutes();
check(routes.missing.length === 0 && routes.extra.length === 0,
  `tess walks all ${routes.declared} tenant routes the Console declares (missing ${routes.missing.join(', ') || 'none'}; extra ${routes.extra.join(', ') || 'none'})`, problems);

const pages = tenantPages(otherTenant, ids);
// The reads are real: paul (platform admin) is answered each of them (the realm has no admin of cara's tenant alone).
const paul = await personToken('paul');
const answered = [];
for (const { api } of pages) answered.push(`${(await attempt(paul, 'GET', api)).status}`);
check(answered.every(s => s === '200'), `paul is answered every read of those pages (${[...new Set(answered)].join(', ')})`, problems);

// Her token, as the API sees it: every read refused, every change refused.
const token = await personToken('tess');
for (const { api } of pages) {
  const r = await attempt(token, 'GET', api);
  check(r.status === 403 && leaks(r.text).length === 0, `tess is refused GET ${api.replace(`/api/v1/tenants/${otherTenant}/`, '…b2/')} (${r.status})`, problems);
}
for (const [method, path, body] of [
  ['PUT', 'settings', { stormThreshold: 1, requestRetentionDays: null, contentRetentionDays: null }],
  ['PUT', `routes/e2e-tess-${run}`, { match: {}, targets: [ids.integration] }],
  ['POST', 'deliveries/replay', { integration: ids.integration }],
  ['POST', `deliveries/${ids.delivery}/retry`, undefined],
]) {
  const r = await attempt(token, method, `/api/v1/tenants/${otherTenant}/${path}`, body);
  check(r.status === 403, `tess is refused ${method} …b2/${path} (${r.status})`, problems);
}

const browser = await launch();
const refused = [];
try {
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}`, onRefused: e => refused.push(e) });
  await tess.getByTestId('tenant-name').waitFor({ timeout: 15000 });
  for (const { route } of pages) {
    const before = tess.apiAnswers.length;
    await tess.goto(`${base}${route}`);
    await tess.waitForURL(/\/no-access\?reason=/, { timeout: 15000 }).catch(() => {});
    await tess.getByTestId('no-access-tenant').waitFor({ timeout: 10000 }).catch(() => {});
    const reached = new URL(tess.url());
    const text = await tess.locator('body').innerText();
    // The access read on load is hers; no call names cara's tenant.
    const asked = tess.apiAnswers.slice(before).filter(a => a.url.includes(otherTenant));
    check(reached.pathname === '/no-access' && reached.searchParams.get('reason') === 'tenant' && leaks(text).length === 0 && asked.length === 0
      && await tess.getByTestId('tenant-bar').count() === 0,
      `${route.replace(otherTenant, '…b2')}: not hers, nothing of it shown or asked (${reached.pathname}${reached.search.slice(0, 30)}; ${asked.length} calls)`, problems);
  }
  await shot(tess, 'tenant-other');

  // Her own tenant's lists: every answer read, none names cara's rows.
  const collected = collectAnswers(tess);
  for (const { route } of tenantPages(testTenant, {}).filter(p => !p.route.includes('undefined'))) {
    await tess.goto(`${base}${route}`);
    await tess.waitForLoadState('networkidle').catch(() => {});
    const text = await tess.locator('body').innerText();
    check(new URL(tess.url()).pathname === route && leaks(text).length === 0, `${route.replace(testTenant, '…a1')}: hers, without cara's rows`, problems);
    // Its answers read before the next page, so none is cut off unsearched.
    await collected.read();
  }
  // Every body read before searching (one still on its way when the walk ends is waited for), and none left unread.
  const { answers, unread } = await collected.read();
  const api = answers.filter(a => a.url.includes('/api/'));
  const seen = api.flatMap(a => leaks(a.text ?? ''));
  check(api.length > 20 && unread.length === 0 && seen.length === 0,
    `none of the ${api.length} answers she read names cara's rows (${seen.join(', ') || 'none'}; unread ${unread.map(a => a.url).join(', ') || 'none'})`, problems);
  await close(tess);
} finally {
  await browser.close();
  await hook.close();
  // cara's seeded rows stop sending (the receiver is gone).
  await unseed(otherTenant, ids).catch(e => problems.push(`unseed: ${e.message}`));
}
await settled();
check(refused.length === 0, `no API call refused in the browser (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
