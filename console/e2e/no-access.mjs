// M36 (phase-4c-plan proof point 2, its first part): someone signed in without an admin role (ann) gets the no-access page from the home,
// a tenant's deep link and a menu link, saying why, and no tenant data: the access read is answered (none) and nothing is refused.
import { launch, signIn, check, shot, finish, settled, base, testTenant } from './lib.mjs';

const problems = [];
const refused = [];
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
    await page.context().close();
  }
} finally {
  await browser.close();
}
await settled();
check(refused.length === 0, `no API call refused once signed in (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
