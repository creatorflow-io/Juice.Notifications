// M41 (phase-4c-plan §4, Settings and Platform). tess (the tests' tenant's admin) changes the tenant's settings: content kept longer than
// requests is refused before sending, a storm threshold and both retentions are saved, and emptied fields return to the deployment's
// defaults; the platform's pages are not hers (no link in the tenant bar, the no-access page for them). paul (platform admin) finds the
// applications from the tenant bar, adds one (a key taken is refused on the field, a client id another application uses by the API),
// renames it, takes its broker user away and disables it, links it to the tests' tenant (a bad id refused on the field) and unlinks it;
// the sample application's link to the tenant stays (it has sent for it); an add still on its way when he moves on leaves him where he went;
// the service's own application is only shown.
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, adminApi, producerApi, tenantPath, until } from './lib.mjs';

const run = Date.now().toString(36);
const appKey = `e2e-${run}`;
const type = `sample.e2e.s${run}`;
const problems = [];

await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'E2E platform', category: `e2e${run}`, schema: { type: 'object' },
  templates: { inbox: 'Title: Platform\n\nSomething happened.' } });
// The sample application has sent for the tests' tenant: its link there stays (unlinking it is refused below).
await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `platform-${run}`, recipients: [{ kind: 'user', id: 'ben', resolver: 'people' }], data: {} });
const original = await adminApi('GET', tenantPath('settings'));

const browser = await launch();
// Refused by design: the client id another application uses, and the sample application's link that stays.
const refused = [];
try {
  // tess: the tenant's settings.
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/settings` });
  await tess.getByTestId('settings-form').waitFor({ timeout: 10000 });
  const fill = async (threshold, requests, content) => {
    await tess.getByTestId('settings-storm-threshold').fill(threshold);
    await tess.getByTestId('settings-request-days').fill(requests);
    await tess.getByTestId('settings-content-days').fill(content);
    await tess.getByTestId('settings-save').click();
  };
  await fill('50', '30', '40');
  const contentError = await tess.locator('[data-testid=field-error][data-pointer="/contentRetentionDays"]').innerText({ timeout: 5000 }).catch(() => '');
  check(contentError.includes("requests' 30"), `content kept longer than requests is refused before sending (${contentError.slice(0, 60)})`, problems);
  // Requests kept a day: an empty content field keeps content that long too (it goes with its deliveries), and its hint says so.
  await tess.getByTestId('settings-request-days').fill('1');
  await tess.getByTestId('settings-content-days').fill('');
  const hint = await tess.getByTestId('settings-content-hint').innerText();
  check(hint.includes('so 1 here'), `an empty content field with requests kept a day says it keeps content a day (${hint.slice(0, 80)})`, problems);
  await fill('50', '30', '7');
  await tess.getByTestId('settings-saved').waitFor({ timeout: 10000 }).catch(() => {});
  const stored = await adminApi('GET', tenantPath('settings'));
  check(stored.stormThreshold === 50 && stored.requestRetentionDays === 30 && stored.contentRetentionDays === 7,
    `the settings are saved (${stored.stormThreshold}, ${stored.requestRetentionDays}, ${stored.contentRetentionDays})`, problems);
  await fill('', '', '');
  await tess.getByTestId('settings-saved').waitFor({ timeout: 10000 }).catch(() => {});
  const defaults = await until(() => adminApi('GET', tenantPath('settings')), s => s.stormThreshold === null, 10);
  check(defaults.stormThreshold === null && defaults.requestRetentionDays === null && defaults.contentRetentionDays === null,
    'emptied, each returns to the deployment\'s default', problems);
  await shot(tess, 'platform-settings');

  check(await tess.getByTestId('platform-applications').count() === 0, 'tess has no link to the applications', problems);
  await tess.goto(`${base}/applications`);
  await tess.getByTestId('no-access-platform-only').waitFor({ timeout: 10000 }).catch(() => {});
  check(await tess.getByTestId('no-access-platform-only').count() === 1, 'the applications are not hers: the no-access page says why', problems);
  await close(tess);

  // paul: the applications, from the tenant bar.
  const paul = await signIn(browser, 'paul', problems, { path: `/tenants/${testTenant}`, onRefused: e => refused.push(e) });
  await paul.getByTestId('platform-applications').click({ timeout: 15000 });
  await paul.getByTestId('applications').waitFor({ timeout: 10000 });
  check(paul.url() === `${base}/applications`, `the tenant bar leads paul to the applications (${paul.url()})`, problems);
  await paul.locator('[data-testid=application-row][data-key="sample"]').waitFor({ timeout: 10000 }).catch(() => {});

  const add = async (key, clientId) => {
    await paul.getByTestId('application-key').fill(key);
    await paul.getByTestId('application-name').fill(`E2E ${run}`);
    await paul.getByTestId('application-client-id').fill(clientId);
    await paul.getByTestId('application-broker-user').fill(`e2e-broker-${run}`);
    await paul.getByTestId('application-save').click();
  };
  await paul.getByTestId('application-new').click();
  await add('sample', `e2e-client-${run}`);
  const taken = await paul.locator('[data-testid=field-error][data-pointer="/key"]').innerText({ timeout: 5000 }).catch(() => '');
  check(taken.includes('already'), `a key taken is refused on its field (${taken.slice(0, 50)})`, problems);
  check(await paul.getByTestId('form-error').count() === 0, 'the form says nothing was refused by the API (nothing was sent)', problems);
  await add(appKey, 'sample-producer');
  // The key's refusal above was the form's own, with no sentence; this one is the API's.
  const clientTaken = await until(() => paul.getByTestId('form-error').innerText({ timeout: 1000 }).catch(() => ''), t => t.includes('client id'), 10);
  check(clientTaken.includes('client id'), `a client id another application uses is refused by the API (${clientTaken.slice(0, 60)})`, problems);
  await add(appKey, `e2e-client-${run}`);
  await paul.waitForURL(`${base}/applications/${appKey}`, { timeout: 10000 }).catch(() => {});
  check(paul.url() === `${base}/applications/${appKey}`, `adding it opens its page (${paul.url()})`, problems);

  // Renamed, its broker user taken away, disabled: only what changed is sent.
  await paul.getByTestId('application-form').waitFor({ timeout: 10000 });
  await paul.getByTestId('application-name').fill(`E2E renamed ${run}`);
  await paul.getByTestId('application-broker-user').fill('');
  await paul.getByTestId('application-enabled').click();
  await paul.getByTestId('application-save').click();
  await paul.getByTestId('application-saved').waitFor({ timeout: 10000 }).catch(() => {});
  const changed = await adminApi('GET', `/api/v1/applications/${appKey}`);
  check(changed.name === `E2E renamed ${run}` && changed.brokerUser === null && changed.enabled === false && changed.clientId === `e2e-client-${run}`,
    `renamed, no broker user, disabled (${changed.name}, ${changed.brokerUser}, ${changed.enabled})`, problems);

  // Linked to the tests' tenant (a bad id refused on the field), the tenant opened from it, then unlinked.
  await paul.getByTestId('tenant-link-id').fill('not-a-tenant');
  await paul.getByTestId('tenant-link').click();
  const badId = await paul.locator('[data-testid=field-error][data-pointer="/tenantId"]').innerText({ timeout: 5000 }).catch(() => '');
  check(badId.includes('GUID'), `a tenant id that isn't one is refused on the field (${badId.slice(0, 50)})`, problems);
  await paul.getByTestId('tenant-link-id').fill(testTenant);
  await paul.getByTestId('tenant-link').click();
  const row = paul.locator(`[data-testid=tenant-row][data-tenant="${testTenant}"]`);
  await row.waitFor({ timeout: 10000 }).catch(() => {});
  check((await adminApi('GET', `/api/v1/applications/${appKey}`)).tenants.includes(testTenant), `${appKey} is linked to the tests' tenant`, problems);
  await shot(paul, 'platform-application');
  await row.getByTestId('tenant-unlink').click();
  await row.getByTestId('tenant-unlink-confirm').click();
  await row.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  check((await adminApi('GET', `/api/v1/applications/${appKey}`)).tenants.length === 0, `${appKey} is unlinked again`, problems);

  // The sample application has sent for the tenant: its link stays, and the row says why.
  await paul.goto(`${base}/applications/sample`);
  const sampleRow = paul.locator(`[data-testid=tenant-row][data-tenant="${testTenant}"]`);
  await sampleRow.waitFor({ timeout: 10000 });
  await sampleRow.getByTestId('tenant-unlink').click();
  await sampleRow.getByTestId('tenant-unlink-confirm').click();
  const stays = await paul.getByTestId('tenant-row-error').innerText({ timeout: 10000 }).catch(() => '');
  check(stays.includes('sent notifications'), `the sample application's link stays (${stays.slice(0, 60)})`, problems);
  check((await adminApi('GET', '/api/v1/applications/sample')).tenants.includes(testTenant), 'the sample application still sends for the tenant', problems);
  // A link tried next (a bad id, refused on its field): the unlink's sentence is gone.
  await paul.getByTestId('tenant-link-id').fill('not-a-tenant');
  await paul.getByTestId('tenant-link').click();
  await paul.locator('[data-testid=field-error][data-pointer="/tenantId"]').waitFor({ timeout: 5000 }).catch(() => {});
  check(await paul.getByTestId('tenant-row-error').count() === 0, "a link tried after the refused unlink drops the unlink's sentence", problems);

  // An add still on its way when paul moves on (to the sample application): its answer doesn't take him to the new one.
  await paul.goto(`${base}/applications`);
  await paul.getByTestId('application-new').click({ timeout: 10000 });
  await paul.route('**/api/v1/applications', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    await new Promise(r => setTimeout(r, 1500));
    await route.continue().catch(() => {});
  });
  await paul.getByTestId('application-key').fill(`${appKey}-late`);
  await paul.getByTestId('application-name').fill(`E2E late ${run}`);
  await paul.getByTestId('application-client-id').fill(`e2e-late-${run}`);
  await paul.getByTestId('application-save').click();
  await paul.locator('[data-testid=application-row][data-key="sample"] a').click();
  await paul.waitForTimeout(2500);
  check(paul.url() === `${base}/applications/sample`, `a late add leaves paul where he went (${paul.url()})`, problems);
  await paul.unroute('**/api/v1/applications');

  // The service's own application: shown, not changed.
  await paul.goto(`${base}/applications/notifications`);
  await paul.getByTestId('application-facts').waitFor({ timeout: 10000 }).catch(() => {});
  check(await paul.getByTestId('application-service-owned').count() === 1 && await paul.getByTestId('application-form').count() === 0
    && await paul.getByTestId('tenant-link-form').count() === 0, "the service's own application is only shown", problems);

  await settled();
  const expected = [
    e => e.includes(' 409 ') && e.endsWith('/api/v1/applications'),
    e => e.includes(' 409 ') && e.includes(`/api/v1/applications/sample/tenants/${testTenant}`),
  ];
  const unexpected = refused.filter(e => !expected.some(x => x(e)));
  check(unexpected.length === 0, `no other API call refused (${unexpected.join('; ') || 'none'})`, problems);
  check(refused.length === 2, `the two refusals asked for, once each (${refused.length})`, problems);
  await close(paul);
} finally {
  // The tenant's settings as they were; the application stays (the API deletes none), disabled and unlinked.
  await adminApi('PUT', tenantPath('settings'), { stormThreshold: original.stormThreshold, requestRetentionDays: original.requestRetentionDays,
    contentRetentionDays: original.contentRetentionDays }).catch(() => {});
  await adminApi('DELETE', `/api/v1/applications/${appKey}/tenants/${testTenant}`, undefined, { allow: [404] }).catch(() => {});
  await adminApi('PUT', `/api/v1/applications/${appKey}-late`, { enabled: false }, { allow: [404] }).catch(() => {});
  await browser.close();
}
await finish(problems);
