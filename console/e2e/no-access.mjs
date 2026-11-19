// M36 (phase-4c-plan proof point 2, its first part): someone signed in without an admin role (ann) gets the no-access page from the home,
// a tenant's deep link and a menu link, and no tenant data: the access read is answered (none), the pages' own reads are refused.
import { launch, signIn, check, shot, finish, base, testTenant } from './lib.mjs';

const problems = [];
const refused = [];
const browser = await launch();
try {
  for (const path of ['/', `/tenants/${testTenant}`, '/go/deliveries']) {
    const page = await signIn(browser, 'ann', problems, { path, onRefused: e => refused.push(e) });
    await page.waitForURL(/\/no-access$/, { timeout: 15000 }).catch(() => {});
    check(page.url() === `${base}/no-access`, `ann at ${path} gets the no-access page (${page.url()})`, problems);
    await page.getByTestId('no-access').waitFor({ timeout: 10000 }).catch(() => {});
    check(await page.getByTestId('no-access').count() === 1, `ann at ${path} is told she administers no tenant`, problems);
    check(await page.getByTestId('tenant-bar').count() === 0, `ann at ${path} sees no tenant`, problems);
    await shot(page, `no-access${path.replace(/\//g, '-')}`);
    await page.context().close();
  }
} finally {
  await browser.close();
}
console.log(`(refused API calls seen: ${refused.length})`);
finish(problems);
