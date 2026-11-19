// M37 (phase-4c-plan §4): a correlation found in the Console, opened by its application and key (a slash in it), acknowledged and resolved
// by tess as herself, its notifications linked.
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, adminApi, producerApi, tenantPath, until } from './lib.mjs';

const run = Date.now().toString(36);
const type = `sample.e2e.c${run}`;
const key = `order/${run}`;
const problems = [];

await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'An order', category: 'orders', correlation: 'required',
  schema: { type: 'object' }, templates: { inbox: 'Title: Order {{ data.n }}\n\nWaiting.' } });
const sent = [];
for (let n = 1; n <= 2; n++) {
  sent.push((await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `${key}-${n}`, correlationKey: key, data: { n } })).id);
}

const browser = await launch();
const refused = [];
try {
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/correlations?key=${encodeURIComponent(key)}`,
    onRefused: e => refused.push(e) });
  await tess.getByTestId('correlation-row').first().waitFor({ timeout: 15000 }).catch(() => {});
  check(await tess.getByTestId('correlation-row').count() === 1, 'the list finds the correlation by its key', problems);
  await tess.getByTestId('correlation-row').first().getByRole('link').click();
  await tess.getByTestId('correlation-state').waitFor({ timeout: 10000 }).catch(() => {});
  check(tess.url() === `${base}/tenants/${testTenant}/correlations/sample/${encodeURIComponent(key)}`, `it opens by application and key (${tess.url()})`, problems);
  check(await tess.getByTestId('correlation-key').innerText() === key, 'the page names the key, slash and all', problems);
  const links = await tess.getByTestId('correlation-notifications').getByRole('link').count();
  check(links === 2, `its two notifications are listed (${links})`, problems);

  await tess.getByTestId('acknowledge').click();
  const acknowledged = await until(() => tess.getByTestId('correlation-state').innerText().catch(() => ''), s => s.includes('acknowledged'), 10);
  check(acknowledged.includes('acknowledged'), `tess acknowledges it (${acknowledged})`, problems);
  const by = await tess.getByTestId('correlation-acknowledged').innerText();
  check(by.includes('tess'), `as herself (${by.trim()})`, problems);

  await tess.getByTestId('resolve-outcome').fill('shipped');
  await tess.getByTestId('resolve').click();
  const resolved = await until(() => tess.getByTestId('correlation-state').innerText().catch(() => ''), s => s.includes('resolved'), 10);
  check(resolved.includes('resolved'), `and resolves it (${resolved})`, problems);
  const api = await adminApi('GET', tenantPath(`correlations/${encodeURIComponent(key)}?application=sample`));
  check(api.state === 'resolved' && api.outcome === 'shipped' && api.resolvedBy === 'tess', `the API agrees (${api.state}, ${api.outcome}, ${api.resolvedBy})`, problems);
  await shot(tess, 'correlation-resolved');

  // A notification of it links back to it.
  await tess.getByTestId('correlation-notifications').getByRole('link').first().click();
  await tess.getByTestId('notification-correlation').waitFor({ timeout: 10000 }).catch(() => {});
  check(await tess.getByTestId('notification-correlation').innerText().catch(() => '') === key, 'its notification links back to it', problems);
  await close(tess);
} finally {
  await browser.close();
}
await settled();
check(refused.length === 0, `no API call refused (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
