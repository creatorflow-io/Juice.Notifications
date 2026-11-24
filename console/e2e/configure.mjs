// M38 (phase-4c-plan proof point 5): delivery set up entirely in the Console works end to end. tess (the tests' tenant's admin) allows the
// receiver's private address, creates an email and a signed webhook integration with a target each, and a route to both; a notification of
// a fresh type then arrives in Mailpit and, signed, at the receiver. On the way: the API's field errors show on their fields, a mute and an
// escalation policy are made and undone, and the setup is taken down again in the browser (integrations that sent are disabled, not deleted).
import { createHmac } from 'node:crypto';
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, adminApi, producerApi, tenantPath, until, receiver } from './lib.mjs';

const run = Date.now().toString(36);
const mailpit = process.env.MAILPIT ?? 'http://localhost:8026';
const hookSecret = 'test-signing-secret'; // NOTIFICATIONS_SECRET_TEST_HOOK in the compose app's environment
const keys = { mail: `e2e-mail-${run}`, hooks: `e2e-hooks-${run}`, ops: `e2e-ops-${run}`, hook: `e2e-hook-${run}`, route: `e2e-cfg-${run}`,
  policy: `e2e-esc-${run}` };
const mailbox = `ops-${run}@example.com`;
const type = `sample.e2e.c${run}`;
const hook = await receiver();
hook.failing = false;
const problems = [];

// The application, linked; its type declared by the producer (types are the application's, not the Console's: M39).
await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'E2E configure', category: 'e2e',
  schema: { type: 'object', required: ['host'], properties: { host: { type: 'string' } } },
  templates: { email: `Subject: Configured ${run} on {{ data.host }}\n\nSent through the Console's setup.`, webhook: 'passthrough' } });

const browser = await launch();
const refused = [];
try {
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/allowlist`, onRefused: e => refused.push(e) });
  const go = async page => { await tess.goto(`${base}/tenants/${testTenant}/${page}`); };
  const pick = async (testId, option) => {
    await tess.getByTestId(testId).click();
    await tess.getByRole('option', { name: option, exact: true }).click();
  };

  // The allowlist: the receiver's private address and port.
  // The API stores a host address as its network (10.0.0.5/32). Other journeys (deliveries.mjs) allow the same address through the API:
  // listed already, it is edited to this receiver's port; otherwise allowed anew.
  // Exactly the address or its /32: a prefix would also take 10.0.0.50/32 for 10.0.0.5.
  const allowRow = tess.locator(`[data-testid=allow-row]:is([data-destination="${hook.address}"], [data-destination="${hook.address}/32"])`);
  await tess.getByTestId('allowlist').waitFor({ timeout: 10000 });
  await tess.waitForTimeout(1000);
  if (await allowRow.count()) {
    await allowRow.getByTestId('allow-edit').click();
  } else {
    await tess.getByTestId('allow-new').click();
    await tess.getByTestId('allow-destination').fill(hook.address);
  }
  await tess.getByTestId('allow-ports').fill(String(hook.port));
  await tess.getByTestId('allow-save').click();
  const allowed = await until(() => allowRow.innerText().catch(() => ''), t => t.includes(String(hook.port)), 10);
  check(allowed.includes(String(hook.port)), `the allowlist lists ${hook.address} on port ${hook.port}`, problems);
  // Allowing it again doesn't replace its ports: the page says to edit it.
  await tess.getByTestId('allow-new').click();
  await tess.getByTestId('allow-destination').fill(hook.address);
  await tess.getByTestId('allow-save').click();
  const again = await tess.locator('[data-testid=field-error][data-pointer="/destination"]').innerText({ timeout: 5000 }).catch(() => '');
  check(again.includes('allowed already'), `allowing it again is refused on the field (${again})`, problems);
  await tess.getByRole('button', { name: 'Cancel' }).click();

  // An email integration. Its settings first without a sender: the API's error shows on the settings field, and nothing is created.
  await go('integrations');
  await tess.getByTestId('integration-new').click();
  await tess.getByTestId('integration-key').fill(keys.mail);
  await pick('integration-channel', 'email');
  await pick('integration-kind', 'smtp');
  await tess.getByTestId('integration-settings').fill(JSON.stringify({ server: 'local' }));
  await tess.getByTestId('integration-save').click();
  const settingsError = tess.locator('[data-testid=field-error][data-pointer="/settings"]');
  await settingsError.first().waitFor({ timeout: 10000 }).catch(() => {});
  check(await settingsError.count() > 0, `a sender left out is refused on the settings field (${await settingsError.first().innerText().catch(() => 'none')})`, problems);
  await tess.getByTestId('integration-settings').fill(JSON.stringify({ server: 'local', from: 'notifications@juice.local', fromName: 'Juice E2E' }));
  await tess.getByTestId('integration-save').click();
  await tess.waitForURL(u => u.pathname.endsWith(`/integrations/${keys.mail}`), { timeout: 10000 }).catch(() => {});
  check(tess.url().endsWith(`/tenants/${testTenant}/integrations/${keys.mail}`), `the new integration opens (${tess.url()})`, problems);

  const addTarget = async (key, address) => {
    await tess.getByTestId('target-new').click();
    await tess.getByTestId('target-key').fill(key);
    await tess.getByTestId('target-address').fill(address);
    await tess.getByTestId('target-save').click();
    await tess.locator(`[data-testid=target-row][data-key="${key}"]`).waitFor({ timeout: 10000 }).catch(() => {});
    return tess.locator(`[data-testid=target-row][data-key="${key}"]`).innerText().catch(() => '');
  };
  check((await addTarget(keys.ops, mailbox)).includes(mailbox), `the email target ${keys.ops} is added for ${mailbox}`, problems);
  await shot(tess, 'configure-email');

  // A webhook integration: it signs, so it names its secret by reference (and only one provider sends webhooks: picked by itself).
  await go('integrations');
  await tess.getByTestId('integration-new').click();
  await tess.getByTestId('integration-key').fill(keys.hooks);
  await pick('integration-channel', 'webhook');
  await tess.getByTestId('integration-secret-ref').fill('env:NOTIFICATIONS_SECRET_TEST_HOOK');
  await tess.getByTestId('integration-save').click();
  await tess.waitForURL(u => u.pathname.endsWith(`/integrations/${keys.hooks}`), { timeout: 10000 }).catch(() => {});
  check(tess.url().endsWith(`/integrations/${keys.hooks}`), `the webhook integration opens (${tess.url()})`, problems);
  // A change typed but not saved outlives adding a target (the page reads the integration again: review M38 r1); Cancel puts it back.
  await tess.getByTestId('integration-priority').fill('7');
  check((await addTarget(keys.hook, hook.url)).includes(hook.url), `the webhook target ${keys.hook} is added for the receiver`, problems);
  const kept = await tess.getByTestId('integration-priority').inputValue();
  check(kept === '7', `the unsaved priority is still there after adding the target (${kept})`, problems);
  await tess.getByTestId('integration-form').getByRole('button', { name: 'Cancel' }).click();
  const putBack = await until(() => tess.getByTestId('integration-priority').inputValue(), v => v === '0', 5);
  check(putBack === '0', `Cancel puts the priority back as saved (${putBack})`, problems);

  // A route: the fresh type to both targets. Without a key it is refused on the key field, not sent (review M38 r1).
  await go('routes');
  await tess.getByTestId('route-new').click();
  await tess.getByTestId('route-save').click();
  const noKey = await tess.locator('[data-testid=field-error][data-pointer="/key"]').innerText({ timeout: 5000 }).catch(() => '');
  check(noKey.includes('Give the route a key'), `a route without a key is refused on its field (${noKey})`, problems);
  await tess.getByTestId('route-key').fill(keys.route);
  await tess.getByTestId('match-types').fill(type);
  await tess.getByTestId('route-targets').click();
  await tess.getByRole('option', { name: new RegExp(`^${keys.ops}\\b`) }).click();
  await tess.getByRole('option', { name: new RegExp(`^${keys.hook}\\b`) }).click();
  await tess.keyboard.press('Escape');
  await tess.getByTestId('route-save').click();
  const routeRow = tess.locator(`[data-testid=route-row][data-key="${keys.route}"]`);
  await routeRow.waitFor({ timeout: 10000 }).catch(() => {});
  const routeText = await routeRow.innerText().catch(() => '');
  check(routeText.includes(type) && routeText.includes(keys.ops) && routeText.includes(keys.hook), `the route lists its type and both targets`, problems);
  await shot(tess, 'configure-route');

  // Sent: an email in Mailpit and a signed webhook at the receiver.
  const sent = await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `${type}-1`, data: { host: 'db-1' } });
  const mail = await until(async () => {
    const r = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${mailbox}"`)}`);
    return r.ok ? (await r.json()).messages ?? [] : [];
  }, ms => ms.length > 0, 30);
  check(mail.length === 1 && mail[0].Subject === `Configured ${run} on db-1`, `the email arrived in Mailpit (${mail.map(m => m.Subject).join(', ') || 'none'})`, problems);
  // The receiver is this run's own: whatever reached it came through the route.
  const got = await until(async () => [...hook.received], rs => rs.length > 0, 30);
  const signed = got[0] && got[0].signature === 'v1,' + createHmac('sha256', hookSecret).update(`${got[0].id}.${got[0].timestamp}.${got[0].body}`).digest('base64');
  check(got.length === 1 && signed, `the webhook arrived once, signed with the integration's secret (${got.length}, ${signed ? 'valid' : 'invalid'} signature)`, problems);

  // A mute for exactly the type, then cancelled.
  await go('mutes');
  await tess.getByTestId('mute-new').click();
  await tess.getByTestId('match-types').fill(type);
  await tess.getByTestId('mute-reason').fill(`e2e ${run}`);
  await tess.getByTestId('mute-save').click();
  const muteRow = tess.locator('[data-testid=mute-row]', { hasText: type });
  await muteRow.first().waitFor({ timeout: 10000 }).catch(() => {});
  check((await muteRow.first().innerText().catch(() => '')).includes('active'), 'the mute is listed as active', problems);
  await muteRow.first().getByTestId('mute-cancel').click();
  await muteRow.first().getByTestId('mute-cancel-confirm').click();
  await until(() => muteRow.count(), n => n === 0, 10);
  check(await muteRow.count() === 0, 'once cancelled it leaves the current mutes', problems);

  // An escalation policy: one step, after 5 minutes, to the route's targets; then deleted.
  await go('escalation-policies');
  await tess.getByTestId('policy-new').click();
  await tess.getByTestId('policy-key').fill(keys.policy);
  await tess.getByTestId('step-after').fill('5');
  await pick('recipient-kind', "the route's targets");
  await tess.getByTestId('policy-save').click();
  const policyRow = tess.locator(`[data-testid=policy-row][data-key="${keys.policy}"]`);
  await policyRow.waitFor({ timeout: 10000 }).catch(() => {});
  check((await policyRow.innerText().catch(() => '')).includes("after 5 min: the route's targets"), 'the escalation policy lists its step', problems);
  await policyRow.getByTestId('policy-delete').click();
  await policyRow.getByTestId('policy-delete-confirm').click();
  await until(() => policyRow.count(), n => n === 0, 10);
  check(await policyRow.count() === 0, 'the escalation policy is deleted', problems);

  // Taken down: the route deleted; an integration that sent can't be (its history keeps it), so it is disabled; the allowlist entry removed.
  await go('routes');
  await routeRow.getByTestId('route-delete').click();
  await routeRow.getByTestId('route-delete-confirm').click();
  await until(() => routeRow.count(), n => n === 0, 10);
  check(await routeRow.count() === 0, 'the route is deleted', problems);
  for (const key of [keys.mail, keys.hooks]) {
    await go(`integrations/${key}`);
    await tess.getByTestId('integration-delete').click();
    await tess.getByTestId('integration-delete-confirm').click();
    await tess.getByTestId('integration-action-error').waitFor({ timeout: 10000 }).catch(() => {});
    const inUse = await tess.getByTestId('integration-action-error').innerText().catch(() => '');
    check(inUse.includes('disable it instead'), `${key} that sent isn't deleted: the Console says why (${inUse})`, problems);
    await tess.locator('[data-testid=integration-enabled] input').uncheck();
    await tess.getByTestId('integration-save').click();
    await until(() => tess.getByTestId('integration-state').innerText().catch(() => ''), s => s.includes('disabled'), 10);
    check((await tess.getByTestId('integration-state').innerText().catch(() => '')).includes('disabled'), `${key} is disabled`, problems);
  }
  await go('allowlist');
  await allowRow.getByTestId('allow-delete').click();
  await allowRow.getByTestId('allow-delete-confirm').click();
  await until(() => allowRow.count(), n => n === 0, 10);
  check(await allowRow.count() === 0, 'the allowlist entry is removed', problems);
  await shot(tess, 'configure-done');

  // The API agrees with what the pages showed.
  const integrations = await adminApi('GET', tenantPath('integrations'));
  const mine = integrations.filter(i => i.key === keys.mail || i.key === keys.hooks);
  check(mine.length === 2 && mine.every(i => !i.enabled), `the API has both integrations, disabled (${mine.map(i => `${i.key}:${i.enabled}`).join(', ')})`, problems);
  check(mine.find(i => i.key === keys.hooks)?.secretRef === 'env:NOTIFICATIONS_SECRET_TEST_HOOK', 'the webhook integration keeps its secret reference', problems);

  await close(tess);
} finally {
  await browser.close();
  await hook.close();
}
await settled();
// Expected refusals: the settings without a sender (400) and each delete of an integration that sent (409).
const unexpected = refused.filter(r => !(/ http 400 .*\/integrations\/e2e-mail-/.test(r) || / http 409 .*\/integrations\/e2e-(mail|hooks)-[^/]+$/.test(r)));
check(unexpected.length === 0, `no other API call refused (${unexpected.join('; ') || 'none'})`, problems);
await finish(problems);
