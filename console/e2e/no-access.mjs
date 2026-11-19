// M36 (phase-4c-plan proof point 2, its first part): someone signed in without an admin role (ann) gets the no-access page from the home,
// a tenant's deep link and a menu link, saying why, and no tenant data: the access read is answered (none) and nothing is refused.
import { launch, signIn, check, shot, finish, base, testTenant } from './lib.mjs';

const problems = [];
const refused = [];
const browser = await launch();
try {
  for (const [path, reason, text] of [['/', 'none', 'no-access'], [`/tenants/${testTenant}`, 'tenant', 'no-access-tenant'], ['/go/deliveries', 'none', 'no-access']]) {
    const page = await signIn(browser, 'ann', problems, { path, onRefused: e => refused.push(e) });
    await page.waitForURL(/\/no-access\?reason=/, { timeout: 15000 }).catch(() => {});
    check(page.url() === `${base}/no-access?reason=${reason}`, `ann at ${path} gets the no-access page, reason ${reason} (${page.url()})`, problems);
    await page.getByTestId(text).waitFor({ timeout: 10000 }).catch(() => {});
    check(await page.getByTestId(text).count() === 1, `ann at ${path} is told why (${text})`, problems);
    check(await page.getByTestId('tenant-bar').count() === 0, `ann at ${path} sees no tenant`, problems);
    await shot(page, `no-access${path.replace(/\//g, '-')}`);
    await page.context().close();
  }
} finally {
  await browser.close();
}
check(refused.length === 0, `no API call refused once signed in (${refused.join('; ') || 'none'})`, problems);
finish(problems);
