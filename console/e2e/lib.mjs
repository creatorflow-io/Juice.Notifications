// Shared by the Console's browser journeys (phase-4c-plan I56), as Juice.Observability's: the local stack's .env, the Console's address,
// sign-in through Keycloak, and checks that fail the run with a message.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/** deploy/local, with a trailing slash. */
export const local = fileURLToPath(new URL('../../deploy/local/', import.meta.url));

/** deploy/local/.env as an object (KEY=value lines; comments and CRLF ignored). */
export const env = Object.fromEntries(readFileSync(`${local}.env`, 'utf8').split(/\r?\n/)
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));

/** The Console as the stack's app serves it (`docker compose --profile app up`), or `ng serve` (BASE=http://localhost:4300). */
export const base = process.env.BASE ?? 'http://localhost:5280';
export const keycloak = process.env.KEYCLOAK ?? 'http://localhost:8081';
/** The tests' tenant, which tess administers. */
export const testTenant = '0193f0aa-0000-7000-8000-0000000000a1';

const shots = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(shots, { recursive: true });

export function launch() {
  return chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
}

/**
 * A new page signed in as `user` (a person account, password PEOPLE_TEST_PASSWORD), at `path`. Page errors go to `problems`; API answers
 * of 400 or more after sign-in go to `onRefused` (by default `problems` too), so a journey that expects refusals collects them apart.
 */
export async function signIn(browser, user, problems, { path = '/', onRefused = e => problems.push(e) } = {}) {
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  page.on('pageerror', e => problems.push(`${user} pageerror: ${e.message}`));
  await page.goto(`${base}${path}`);
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', user);
  await page.fill('#password', env.PEOPLE_TEST_PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed') && !u.toString().includes('/auth/'),
    { timeout: 30000 });
  // From here on: before sign-in completes, the bell (@juice-js/notifications) asks for the inbox without a token, and is refused.
  page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) onRefused(`${user} http ${r.status()} ${r.url()}`); });
  return page;
}

/** Fails the journey with `message` unless `ok`. */
export function check(ok, message, problems) {
  if (!ok) problems.push(message);
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${message}`);
}

/** A screenshot under e2e/shots. */
export async function shot(page, name) {
  await page.screenshot({ path: `${shots}${name}.png`, fullPage: true });
}

/** Prints the problems and exits non-zero when there are any. */
export function finish(problems) {
  if (problems.length) {
    console.error(`\n${problems.length} problem(s):\n${problems.map(p => `  - ${p}`).join('\n')}`);
    process.exit(1);
  }
  console.log('\nall checks passed');
}

/** A client-credentials token from the local realm (`notifications-admin` with NOTIFICATIONS_ADMIN_CLIENT_SECRET, …). */
export async function clientToken(clientId, secretName) {
  const r = await fetch(`${keycloak}/realms/juice/protocol/openid-connect/token`, { method: 'POST',
    body: new URLSearchParams({ grant_type: 'client_credentials', client_id: clientId, client_secret: env[secretName] }) });
  if (!r.ok) throw new Error(`token for ${clientId}: ${r.status}`);
  return (await r.json()).access_token;
}

/** The API as the platform admin client: `api('PUT', '/api/v1/applications/sample/tenants/…')`. */
export async function adminApi(method, path, body) {
  const token = await clientToken('notifications-admin', 'NOTIFICATIONS_ADMIN_CLIENT_SECRET');
  const r = await fetch(`${base}${path}`, { method, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
}
