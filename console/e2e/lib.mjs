// Shared by the Console's browser journeys (phase-4c-plan I56), as Juice.Observability's: the local stack's .env, the Console's address,
// sign-in through Keycloak, and checks that fail the run with a message.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { createServer } from 'node:http';
import { networkInterfaces } from 'node:os';
import { fileURLToPath } from 'node:url';

/** deploy/local, with a trailing slash. */
export const local = fileURLToPath(new URL('../../deploy/local/', import.meta.url));

/** deploy/local/.env as an object (KEY=value lines; comments and CRLF ignored). */
export const env = Object.fromEntries(readFileSync(`${local}.env`, 'utf8').split(/\r?\n/)
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));

/** The Console as the stack's app serves it (`docker compose --profile app up`), or `ng serve` (BASE=http://localhost:4300). */
export const base = process.env.BASE ?? 'http://localhost:5280';
export const keycloak = process.env.KEYCLOAK ?? 'http://localhost:8080';
/** The tests' tenant, which tess administers. */
export const testTenant = '0193f0aa-0000-7000-8000-0000000000a1';

const shots = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(shots, { recursive: true });

export function launch() {
  return chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
}

/**
 * A new page signed in as `user` (a person account, password PEOPLE_TEST_PASSWORD), at `path`. Page errors go to `problems`; API answers
 * of 400 or more go to `onRefused` (by default `problems` too), so a journey that expects refusals collects them apart: once back from
 * Keycloak, a refused call that carried a token (a 401 only if it isn't retried within 3 s: the same method and URL answered with success
 * under a different token, the auth library having refreshed it), and any refused call sent once the app has left its sign-in pages
 * (/auth/…). A call without a token sent before then is expected: the bell asks for the inbox on the signed-out page load and while the
 * callback stores the token. Close the page with `close(page)` (it waits for those retries), and call `settled()` before reading what was
 * refused.
 * `page.apiAnswers` keeps every API answer once back from Keycloak (url and status).
 */
export async function signIn(browser, user, problems, { path = '/', onRefused = e => problems.push(e) } = {}) {
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  page.on('pageerror', e => problems.push(`${user} pageerror: ${e.message}`));
  let atKeycloak = false;
  let back = false;
  let settled = false;
  page.apiAnswers = [];
  page.pending = [];
  page.on('framenavigated', frame => {
    if (frame !== page.mainFrame()) return;
    if (frame.url().includes('/realms/juice')) atKeycloak = true;
    else if (atKeycloak && frame.url().startsWith(base)) {
      back = true;
      settled ||= !new URL(frame.url()).pathname.startsWith('/auth/');
    }
  });
  // Whether the app had left its sign-in pages when each call was sent (not when its answer came).
  const sentSettled = new WeakMap();
  page.on('request', r => sentSettled.set(r, settled));
  page.on('response', r => {
    if (!back || !r.url().includes('/api/')) return;
    const authorization = r.request().headers()['authorization'];
    const answer = { method: r.request().method(), url: r.url(), status: r.status(), authorization, at: Date.now() };
    page.apiAnswers.push(answer);
    if (r.status() < 400) return;
    const withToken = !!authorization;
    const refusal = `${user} http ${r.status()} ${r.url()}${withToken ? '' : ' (no token)'}`;
    if (withToken && r.status() === 401) {
      const decided = new Promise(resolve => setTimeout(() => {
        const retried = page.apiAnswers.some(a => a.method === answer.method && a.url === answer.url && a.status < 400 && a.at >= answer.at
          && a.authorization && a.authorization !== answer.authorization);
        if (!retried) onRefused(refusal);
        resolve();
      }, 3000));
      pending.push(decided);
      page.pending.push(decided);
    } else if (withToken || sentSettled.get(r.request())) {
      onRefused(refusal);
    }
  });
  await page.goto(`${base}${path}`);
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', user);
  await page.fill('#password', env.PEOPLE_TEST_PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed') && !u.toString().includes('/auth/'),
    { timeout: 30000 });
  return page;
}

/** Refusals waiting to see whether their call is retried. */
const pending = [];

/** Closes a page from `signIn` once its refusals waiting on a retry are decided (the page stays open meanwhile, so the retry can happen). */
export async function close(page) {
  await Promise.all(page.pending);
  await page.context().close();
}

/** Once every refusal waiting on a retry is decided. */
export async function settled() {
  await Promise.all(pending);
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

/** Prints the problems (once every refusal is decided) and exits non-zero when there are any. */
export async function finish(problems) {
  await settled();
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
export async function adminApi(method, path, body, { allow = [] } = {}) {
  return call(await clientToken('notifications-admin', 'NOTIFICATIONS_ADMIN_CLIENT_SECRET'), method, path, body, allow);
}

/** The API as the sample application (`sample-producer`): declaring types, sending. */
export async function producerApi(method, path, body) {
  return call(await clientToken('sample-producer', 'SAMPLE_PRODUCER_CLIENT_SECRET'), method, path, body);
}

/** The API as a person (`ann`, `ben`, …, through the people-test client): their own `me` pages. */
export async function personApi(user, method, path, body) {
  const r = await fetch(`${keycloak}/realms/juice/protocol/openid-connect/token`, { method: 'POST',
    body: new URLSearchParams({ grant_type: 'password', client_id: 'people-test', username: user, password: env.PEOPLE_TEST_PASSWORD }) });
  if (!r.ok) throw new Error(`token for ${user}: ${r.status}`);
  return call((await r.json()).access_token, method, path, body);
}

/** A call that fails on an error status, but those in `allow` (409: already there). */
async function call(token, method, path, body, allow = []) {
  const r = await fetch(`${base}${path}`, { method, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  if (!r.ok && !allow.includes(r.status)) throw new Error(`${method} ${path}: ${r.status} ${await r.text()}`);
  return r.status === 204 || !r.ok ? null : r.json();
}

/** The tests' tenant's API path: `tenantPath('deliveries')` → `/api/v1/tenants/{testTenant}/deliveries`. */
export const tenantPath = path => `/api/v1/tenants/${testTenant}/${path}`;

/** Polls `read` every 500 ms until `done(value)`, up to `seconds`; the last value (check it). */
export async function until(read, done, seconds = 30) {
  let value;
  for (let i = 0; i < seconds * 2; i++) {
    value = await read();
    if (done(value)) return value;
    await new Promise(r => setTimeout(r, 500));
  }
  return value;
}

/**
 * A webhook receiver on this machine's private IPv4 address (the service never sends to loopback), as the integration tests' Receiver:
 * answers `status()` (404 while `failing`), and keeps each request's `webhook-id` (the delivery's id) and body.
 */
export async function receiver() {
  const address = Object.values(networkInterfaces()).flat()
    .find(a => a && a.family === 'IPv4' && !a.internal && /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(a.address))?.address;
  if (!address) throw new Error('no private IPv4 address on this machine for the webhook receiver');
  const state = { failing: true, received: [] };
  const server = createServer((req, res) => {
    let body = '';
    req.on('data', d => body += d).on('end', () => {
      state.received.push({ id: req.headers['webhook-id'], body, status: state.failing ? 404 : 200 });
      res.writeHead(state.failing ? 404 : 200).end();
    });
  });
  await new Promise(resolve => server.listen(0, '0.0.0.0', resolve));
  const port = server.address().port;
  return Object.assign(state, { address, port, url: `http://${address}:${port}/hook`, close: () => new Promise(r => server.close(r)) });
}
