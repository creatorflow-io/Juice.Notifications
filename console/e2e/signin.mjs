// M36 (phase-4c-plan): an admin signs in and lands in a tenant. tess (tenant admin of the tests' tenant) sees only it, by name, with no
// switcher; paul (platform admin) lands in a tenant and, with more than one, switches tenants on the same page. Both have the bell.
import { launch, signIn, check, shot, finish, base, testTenant, adminApi } from './lib.mjs';

// A second tenant for paul to switch to: cara's (…b2), linked to the stack's sample application (idempotent).
const otherTenant = '0193f0aa-0000-7000-8000-0000000000b2';
await adminApi('PUT', `/api/v1/applications/sample/tenants/${otherTenant}`);

const problems = [];
const browser = await launch();
try {
  const tess = await signIn(browser, 'tess', problems);
  await tess.waitForURL(new RegExp(`/tenants/${testTenant}$`), { timeout: 15000 }).catch(() => {});
  check(tess.url() === `${base}/tenants/${testTenant}`, `tess lands in her tenant (${tess.url()})`, problems);
  await tess.getByTestId('tenant-name').waitFor({ timeout: 10000 }).catch(() => {});
  check(await tess.getByTestId('tenant-name').textContent() === testTenant, 'tess sees her tenant named', problems);
  check(await tess.getByTestId('tenant-switcher').count() === 0, 'tess has no tenant switcher', problems);
  check(await tess.locator('jn-notification-bell, [data-testid="notification-bell"], .jn-bell').count() > 0
    || await tess.getByRole('button', { name: /notification/i }).count() > 0, 'tess has the bell in the toolbar', problems);
  // The bell's own calls carry her token (the API's origin is among the token's allowed URLs): its counts load.
  const counts = () => tess.apiAnswers.filter(a => a.url.includes('/me/inbox/counts'));
  for (let i = 0; i < 50 && !counts().some(a => a.status === 200); i++) await tess.waitForTimeout(200);
  check(counts().some(a => a.status === 200), `tess's bell loads her inbox counts (${counts().map(a => a.status).join(', ') || 'no call'})`, problems);
  await shot(tess, 'signin-tess');

  const paul = await signIn(browser, 'paul', problems);
  await paul.waitForURL(/\/tenants\/[0-9a-f-]{36}$/, { timeout: 15000 }).catch(() => {});
  check(/\/tenants\/[0-9a-f-]{36}$/.test(paul.url()), `paul lands in a tenant (${paul.url()})`, problems);
  const switcher = paul.getByTestId('tenant-switcher');
  if (await switcher.count() > 0) {
    const from = paul.url();
    await switcher.click();
    const options = paul.getByRole('option');
    const count = await options.count();
    check(count > 1, `paul's switcher lists his tenants (${count})`, problems);
    // Another tenant: an option not naming the one in the URL (a label is the id, or "name (id)").
    const currentId = from.split('/').pop();
    for (let i = 0; i < count; i++) {
      if (!(await options.nth(i).textContent()).includes(currentId)) { await options.nth(i).click(); break; }
    }
    await paul.waitForURL(u => u.toString() !== from, { timeout: 10000 }).catch(() => {});
    check(paul.url() !== from && /\/tenants\/[0-9a-f-]{36}$/.test(paul.url()), `paul switches tenant (${paul.url()})`, problems);
    await paul.locator('.cdk-overlay-pane').waitFor({ state: 'detached', timeout: 5000 }).catch(() => {});
  } else {
    console.log('note paul administers one tenant here: no switcher to try');
  }
  await shot(paul, 'signin-paul');

  // Every tenant is a platform admin's, listed or not: a deep link to one without a linked application opens it (the API answers for it).
  const unlisted = '0193f0aa-0000-7000-8000-0000000000c3';
  await paul.goto(`${base}/tenants/${unlisted}`);
  await paul.getByTestId('tenant-bar').waitFor({ timeout: 10000 }).catch(() => {});
  check(paul.url() === `${base}/tenants/${unlisted}` && await paul.getByTestId('overview-tenant').count() === 1,
    `paul opens an unlisted tenant by its link (${paul.url()})`, problems);

  // A link that isn't a tenant's: the no-access page says so (not that no tenant is linked).
  await paul.goto(`${base}/tenants/not-a-tenant`);
  await paul.getByTestId('no-access-tenant').waitFor({ timeout: 10000 }).catch(() => {});
  check(new URL(paul.url()).pathname === '/no-access' && new URL(paul.url()).searchParams.get('reason') === 'tenant'
    && await paul.getByTestId('no-access-tenant').count() === 1,
    `paul at a malformed tenant link is told it isn't his tenant's (${paul.url()})`, problems);
} finally {
  await browser.close();
}
await finish(problems);
