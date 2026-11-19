// M37 (phase-4c-plan proof point 4): a failed delivery is found, inspected and retried with the same id; a bulk replay from a filter replays
// exactly what the filter shows. tess (the tests' tenant's admin) does it in the browser; a receiver on this machine fails, then accepts.
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, adminApi, producerApi, tenantPath, until, receiver } from './lib.mjs';

const run = Date.now().toString(36);
const hook = await receiver();
const problems = [];

// Setup, as the API: a webhook to the receiver, and three types routed to it (one to retry, one to replay, one the replay must leave).
await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await adminApi('PUT', tenantPath('allowlist'), { destination: hook.address, ports: [hook.port] });
await adminApi('PUT', tenantPath('integrations/e2e-hooks'), { channel: 'webhook', kind: 'webhook', secretRef: 'env:NOTIFICATIONS_SECRET_TEST_HOOK' });
await adminApi('PUT', tenantPath(`integrations/e2e-hooks/targets/e2e-${run}`), { address: hook.url });
const types = { retry: `sample.e2e.r${run}`, replay: `sample.e2e.p${run}`, other: `sample.e2e.o${run}` };
for (const type of Object.values(types)) {
  await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'E2E delivery', category: 'e2e',
    schema: { type: 'object' }, templates: { webhook: 'passthrough' } });
  await adminApi('PUT', tenantPath(`routes/e2e-${type.split('.').pop()}`), { match: { type }, targets: [`e2e-${run}`] });
}
const send = async (type, n) => (await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `${type}-${n}`, data: { n } })).id;
const sent = { retry: [await send(types.retry, 1)], replay: [], other: [await send(types.other, 1)] };
for (let n = 1; n <= 3; n++) sent.replay.push(await send(types.replay, n));
// Each failed (404: the destination's, at once).
const deliveriesOf = async id => (await adminApi('GET', tenantPath(`notifications/${id}`))).deliveries;
const failedOf = async ids => (await Promise.all(ids.map(deliveriesOf))).flat();
const all = await until(() => failedOf([...sent.retry, ...sent.replay, ...sent.other]), ds => ds.length === 5 && ds.every(d => d.status === 'failed'), 60);
check(all.length === 5 && all.every(d => d.status === 'failed'), `five deliveries failed at the receiver (${all.map(d => d.status).join(', ')})`, problems);
const [retryDelivery] = await deliveriesOf(sent.retry[0]);
const replayIds = (await failedOf(sent.replay)).map(d => d.id).sort();
const [otherDelivery] = await deliveriesOf(sent.other[0]);

const browser = await launch();
const refused = [];
try {
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/deliveries?status=failed&type=${types.retry}`,
    onRefused: e => refused.push(e) });

  // Found: the failed list for its type shows it; opened, its attempt says why.
  await tess.getByTestId('delivery-row').first().waitFor({ timeout: 15000 }).catch(() => {});
  const rows = await tess.getByTestId('delivery-row').evaluateAll(rs => rs.map(r => r.getAttribute('data-id')));
  check(rows.length === 1 && rows[0] === retryDelivery?.id, `the failed list for the type shows the one delivery (${rows.join(', ')})`, problems);
  await tess.getByTestId('delivery-row').first().getByRole('link').click();
  await tess.getByTestId('attempt').first().waitFor({ timeout: 10000 }).catch(() => {});
  check(tess.url().endsWith(`/tenants/${testTenant}/deliveries/${retryDelivery?.id}`), `the delivery opens by its id (${tess.url()})`, problems);
  check((await tess.getByTestId('attempt').first().innerText()).includes('404'), 'its attempt shows the receiver\'s 404', problems);
  await shot(tess, 'delivery-failed');

  // Retried: the same id, sent again and delivered.
  hook.failing = false;
  await tess.getByTestId('retry').click();
  await tess.getByTestId('retry-result').waitFor({ timeout: 10000 }).catch(() => {});
  const delivered = await until(async () => {
    await tess.getByRole('button', { name: 'Refresh' }).click();
    await tess.waitForTimeout(300);
    return tess.getByTestId('delivery-status').innerText().catch(() => '');
  }, s => s.includes('delivered'), 30);
  check(delivered.includes('delivered'), `the retried delivery is delivered (${delivered})`, problems);
  const attempts = await tess.getByTestId('attempt').count();
  check(attempts === 2, `it shows both attempts (${attempts})`, problems);
  const retried = hook.received.filter(r => r.id === retryDelivery?.id);
  check(retried.length === 2 && retried.every(r => r.body === retried[0].body), `the receiver got the same delivery twice, same id and body (${retried.length})`, problems);
  await shot(tess, 'delivery-retried');

  // Bulk replay from the filter: exactly the three failed deliveries of its type, not the other type's.
  await tess.goto(`${base}/tenants/${testTenant}/deliveries?status=failed&type=${types.replay}`);
  await until(async () => tess.getByTestId('delivery-row').count(), n => n === 3, 15);
  const shown = (await tess.getByTestId('delivery-row').evaluateAll(rs => rs.map(r => r.getAttribute('data-id')))).sort();
  check(JSON.stringify(shown) === JSON.stringify(replayIds), `the filter shows the three failed deliveries of its type (${shown.length})`, problems);
  await tess.getByTestId('replay').click();
  await tess.getByTestId('replay-confirm').click();
  await tess.getByTestId('replay-result').waitFor({ timeout: 10000 }).catch(() => {});
  const result = await tess.getByTestId('replay-result').innerText().catch(() => '');
  check(/Replayed 3\b/.test(result), `the replay put back three (${result.trim()})`, problems);
  await shot(tess, 'deliveries-replayed');
  const replayed = await until(async () => [...new Set(hook.received.filter(r => replayIds.includes(r.id) && r.status === 200).map(r => r.id))].sort(),
    ids => ids.length === 3, 30);
  check(JSON.stringify(replayed) === JSON.stringify(replayIds), `the receiver got exactly those three again (${replayed.length})`, problems);
  const other = (await deliveriesOf(sent.other[0]))[0];
  check(other?.status === 'failed' && !hook.received.some(r => r.id === otherDelivery?.id && r.status === 200),
    `the other type's failed delivery wasn't replayed (${other?.status})`, problems);

  await close(tess);
} finally {
  await browser.close();
  await hook.close();
}
await settled();
check(refused.length === 0, `no API call refused (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
