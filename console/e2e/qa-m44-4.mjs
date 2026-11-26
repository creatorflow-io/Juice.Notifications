// QA (M44-4, the Console library following the hosting mode): against a service host (BASE, ng serve with the API proxied), tess (the tests'
// tenant's admin) opens every service page; the access answer is then replaced in the browser to stand for an embedded host and for each
// set of rights (viewer, operator, admin), which is what the library reads them from. A route's digest window is set at 0–3600 and refused
// at 3601 with the API's error on its field; a WouldSend delivery and a held correlation show their badges; a key field validates in the
// browser.
//   BASE=http://localhost:4300 node e2e/qa-m44-4.mjs
import { execFileSync } from 'node:child_process';
import { launch, signIn, close, check, finish, base, testTenant, adminApi, producerApi, tenantPath, until, receiver, env } from './lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const t = `/tenants/${testTenant}`;
const hook = await receiver();
hook.failing = false;

// ─── Setup, as the API: an application, a webhook, a type routed to it, a correlated notification and its delivery ────────
await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await adminApi('PUT', tenantPath('allowlist'), { destination: hook.address, ports: [hook.port] });
await adminApi('PUT', tenantPath('integrations/qa-hooks'), { channel: 'webhook', kind: 'webhook', secretRef: 'env:NOTIFICATIONS_SECRET_TEST_HOOK' });
await adminApi('PUT', tenantPath(`integrations/qa-hooks/targets/qa-${run}`), { address: hook.url });
const type = `sample.qa.m${run}`;
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'QA badge', category: 'qa', schema: { type: 'object' },
  templates: { webhook: 'passthrough' }, correlation: 'optional' });
await adminApi('PUT', tenantPath(`routes/qa-${run}`), { match: { type }, targets: [`qa-${run}`] });
const correlation = `qa-${run}`;
const sent = (await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `${type}-1`, correlationKey: correlation, data: { n: 1 } })).id;
const [delivery] = await until(async () => (await adminApi('GET', tenantPath(`notifications/${sent}`))).deliveries ?? [], ds => ds.length === 1 && ds[0].status === 'delivered', 60);
check(delivery?.status === 'delivered', `the setup's delivery was delivered (${delivery?.status})`, problems);
// A failed delivery, for retry and replay: the receiver refuses (404, at once).
hook.failing = true;
const failing = (await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `${type}-2`, data: { n: 2 } })).id;
const [failed] = await until(async () => (await adminApi('GET', tenantPath(`notifications/${failing}`))).deliveries ?? [], ds => ds.length === 1 && ds[0].status === 'failed', 60);
check(failed?.status === 'failed', `the failing delivery failed (${failed?.status})`, problems);
// The badges' rows, written as the pipeline would: a dry-run delivery, and the producer's hold on the correlation.
const sql = q => execFileSync('docker', ['exec', 'juice-notifications-postgres-1', 'psql', '-U', 'notifications', '-d', process.env.DATABASE,
  '-v', 'ON_ERROR_STOP=1', '-tAc', q], { encoding: 'utf8' });
sql(`UPDATE notifications.deliveries SET status = 'WouldSend', dry_run = true WHERE id = '${delivery.id}'`);
sql(`UPDATE notifications.correlations SET held_at = now(), hold_reason = 'silence:qa', held_by = 'qa' WHERE key = '${correlation}'`);

/** The service's access answer, rewritten in the browser: another mode, pages and rights for the tests' tenant. */
async function asAccess(page, { mode, pages, rights }) {
  await page.route('**/api/v1/access', async route => {
    const answer = await (await route.fetch()).json();
    answer.mode = mode ?? answer.mode;
    answer.pages = pages ?? answer.pages;
    answer.tenants = answer.tenants.map(x => ({ ...x, rights: rights ?? x.rights }));
    await route.fulfill({ json: answer });
  });
}

const SERVICE = ['overview', 'deliveries', 'notifications', 'correlations', 'storms', 'integrations', 'routes', 'mutes', 'escalation-policies', 'allowlist',
  'types', 'display-groups', 'subscribers', 'groups', 'topics', 'resolvers', 'settings'];
const EMBEDDED = ['overview', 'deliveries', 'notifications', 'correlations', 'storms', 'integrations', 'routes', 'mutes', 'escalation-policies', 'allowlist',
  'types', 'settings'];
const pathOf = id => id === 'overview' ? t : `${t}/${id}`;

const browser = await launch();
try {
  // ─── Service mode: every page shows ────────
  {
    const tess = await signIn(browser, 'tess', problems, { path: t });
    for (const id of SERVICE) {
      await tess.goto(`${base}${pathOf(id)}`);
      await tess.waitForLoadState('networkidle');
      const url = new URL(tess.url());
      check(!url.pathname.includes('no-access') && url.pathname === pathOf(id), `service: ${id} shows (${url.pathname}${url.search})`, problems);
    }
    const access = tess.apiAnswers.find(a => a.url.endsWith('/api/v1/access'));
    check(access?.status === 200, 'service: the access read answered', problems);
    await close(tess);
  }

  // ─── Embedded: an unlisted page goes to no-access (reason page), a listed one shows ────────
  {
    const tess = await signIn(browser, 'tess', problems, { path: t });
    await asAccess(tess, { mode: 'embedded', pages: EMBEDDED });
    for (const id of ['subscribers', 'groups', 'topics', 'resolvers', 'display-groups']) {
      await tess.goto(`${base}${pathOf(id)}`);
      await tess.waitForURL(u => u.pathname.includes('no-access'), { timeout: 15000 }).catch(() => {});
      const url = new URL(tess.url());
      check(url.pathname.endsWith('/no-access') && url.searchParams.get('reason') === 'page', `embedded: ${id} goes to no-access?reason=page (${url.pathname}${url.search})`, problems);
    }
    for (const id of ['deliveries', 'routes', 'settings']) {
      await tess.goto(`${base}${pathOf(id)}`);
      await tess.waitForLoadState('networkidle');
      check(new URL(tess.url()).pathname === pathOf(id), `embedded: ${id} shows`, problems);
    }
    await close(tess);
  }

  // ─── Rights: viewer none, operator retry and replay only, admin all ────────
  for (const [who, rights, operate, configure] of [['viewer', ['read'], false, false], ['operator', ['read', 'operate'], true, false],
    ['admin', ['read', 'operate', 'configure'], true, true]]) {
    const tess = await signIn(browser, 'tess', problems, { path: t, onRefused: () => {} });
    await asAccess(tess, { rights });
    await tess.goto(`${base}${t}/deliveries?status=failed`);
    await tess.getByTestId('delivery-row').first().waitFor({ timeout: 15000 });
    await tess.getByTestId('replay-bar').waitFor({ state: 'attached', timeout: 15000 });
    check(await tess.getByTestId('replay').count() === (operate ? 1 : 0), `${who}: replay ${operate ? 'offered' : 'hidden'}`, problems);
    await tess.goto(`${base}${t}/deliveries/${failed.id}`);
    await tess.getByTestId('attempt').first().waitFor({ timeout: 15000 }).catch(() => {});
    await tess.waitForLoadState('networkidle');
    check(await tess.getByTestId('retry').count() === (operate ? 1 : 0), `${who}: retry ${operate ? 'offered' : 'hidden'}`, problems);
    await tess.goto(`${base}${t}/routes`);
    await tess.getByTestId('route-digest-shown').first().waitFor({ timeout: 15000 });
    check(await tess.getByTestId('route-new').count() === (configure ? 1 : 0), `${who}: new route ${configure ? 'offered' : 'hidden'}`, problems);
    check(await tess.getByTestId('route-edit').count() === (configure ? await tess.getByTestId('route-digest-shown').count() : 0),
      `${who}: route edit ${configure ? 'on every row' : 'hidden'}`, problems);
    check(await tess.getByTestId('route-delete').count() === (configure ? await tess.getByTestId('route-digest-shown').count() : 0),
      `${who}: route delete ${configure ? 'on every row' : 'hidden'}`, problems);
    await tess.goto(`${base}${t}/integrations`);
    await tess.waitForLoadState('networkidle');
    const buttons = await tess.locator('main button, na-tenant-shell button').evaluateAll(bs => bs.map(b => (b.textContent ?? '').trim()).filter(Boolean));
    const changes = buttons.filter(b => /^(New|Add|Save|Delete|Edit|Disable|Enable)/i.test(b));
    check(configure || changes.length === 0, `${who}: integrations offers no change (${changes.join(', ') || 'none'})`, problems);
    await close(tess);
  }

  // ─── digestSeconds, badges and key fields, as the admin ────────
  {
    const refused = [];
    const tess = await signIn(browser, 'tess', problems, { path: `${t}/routes`, onRefused: e => refused.push(e) });
    const row = tess.locator('tr', { hasText: `qa-${run}` }).first();
    for (const seconds of [0, 3600]) {
      await row.getByTestId('route-edit').click();
      await tess.getByTestId('route-digest').fill(String(seconds));
      await tess.getByRole('button', { name: 'Save' }).click();
      const want = seconds === 0 ? 'at once' : '3600 s';
      await until(async () => (await row.getByTestId('route-digest-shown').innerText()).trim(), v => v === want, 15);
      const shown = (await row.getByTestId('route-digest-shown').innerText()).trim();
      check(seconds === 0 ? shown === 'at once' : shown === '3600 s', `digest ${seconds} saved and shown (${shown})`, problems);
    }
    await row.getByTestId('route-edit').click();
    await tess.getByTestId('route-digest').fill('3601');
    // The input's max is 3600: a browser that enforces it in the form stops the save; else the API refuses it on its field.
    const invalidInBrowser = await tess.getByTestId('route-digest').evaluate(e => !e.checkValidity());
    await tess.getByRole('button', { name: 'Save' }).click();
    await tess.waitForTimeout(1500);
    const fieldError = await tess.locator('na-field-errors[pointer="/digestSeconds"]').innerText().catch(() => '');
    check(fieldError.trim().length > 0, `digest 3601: the API's error shows on the field (browser max: ${invalidInBrowser ? 'flagged' : 'not flagged'}; "${fieldError.trim()}")`, problems);
    // The page refuses it before the API is asked (no request); the API refuses it too, with its pointer.
    check(!refused.some(r => r.includes('/routes/')), 'digest 3601: no request was sent', problems);
    const r3601 = await fetch(`${base}${tenantPath(`routes/qa-${run}`)}`, { method: 'PUT', headers: { authorization: `Bearer ${await (await import('./lib.mjs')).clientToken('notifications-admin', 'NOTIFICATIONS_ADMIN_CLIENT_SECRET')}`, 'content-type': 'application/json' }, body: JSON.stringify({ match: { type }, targets: [`qa-${run}`], digestSeconds: 3601 }) });
    const p3601 = await r3601.json();
    check(r3601.status === 400 && JSON.stringify(p3601).includes('/digestSeconds'), `digest 3601: the API answers 400 on /digestSeconds (${r3601.status} ${JSON.stringify(p3601.errors ?? p3601).slice(0, 160)})`, problems);
    const stored = await adminApi('GET', tenantPath('routes'));
    check((stored.find?.(r => r.key === `qa-${run}`) ?? stored.items?.find(r => r.key === `qa-${run}`))?.digestSeconds === 3600, 'digest 3601 left the stored 3600', problems);
    await tess.getByRole('button', { name: 'Cancel' }).click().catch(() => {});

    // Key fields: a digit first or an upper-case letter is invalid in the browser; letters, digits and hyphens are valid.
    await tess.getByTestId('route-new').click();
    const key = tess.getByTestId('route-key');
    for (const [value, valid] of [['1ops', false], ['Ops', false], ['ops_1', false], ['ops-1', true], ['a', true], ['a'.repeat(63), true], ['a'.repeat(64), false]]) {
      await key.fill(value);
      const ok = await key.evaluate(e => e.checkValidity());
      check(ok === valid, `route key "${value.length > 12 ? value.slice(0, 6) + '…(' + value.length + ')' : value}" is ${valid ? 'valid' : 'invalid'} in the browser`, problems);
    }
    const pattern = await key.getAttribute('pattern');
    check(pattern === '[a-z][a-z0-9\\-]{0,62}', `the key input carries the escaped pattern (${pattern})`, problems);
    await tess.getByRole('button', { name: 'Cancel' }).click().catch(() => {});

    // Badges.
    await tess.goto(`${base}${t}/deliveries`);
    await tess.getByTestId('delivery-row').first().waitFor({ timeout: 15000 });
    const would = tess.locator('na-badge [data-state="wouldSend"]');
    check(await would.count() >= 1 && (await would.first().innerText()).trim() === 'would send', `the deliveries list shows the would send badge (${await would.count()})`, problems);
    await tess.goto(`${base}${t}/deliveries/${delivery.id}`);
    await tess.waitForLoadState('networkidle');
    check(await tess.locator('na-badge [data-state="wouldSend"]').count() >= 1, 'the delivery page shows the would send badge', problems);
    await tess.goto(`${base}${t}/correlations`);
    await tess.getByTestId('correlation-held-badge').first().waitFor({ timeout: 15000 }).catch(() => {});
    check(await tess.locator(`[data-testid=correlation-row][data-key="${correlation}"] [data-testid=correlation-held-badge]`).count() === 1, 'the correlations list shows the held badge on its row', problems);
    await tess.goto(`${base}${t}/correlations/sample/${encodeURIComponent(correlation)}`);
    await tess.getByTestId('correlation-held').waitFor({ timeout: 15000 }).catch(() => {});
    const held = await tess.getByTestId('correlation-held').innerText().catch(() => '');
    check(held.includes('silence:qa') && held.includes('qa'), `the correlation page says it is held (${held.trim()})`, problems);
    await close(tess);
  }
} finally {
  await browser.close();
  await hook.close();
}
await finish(problems);
