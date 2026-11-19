// M37 (phase-4c-plan proof point 3): "why didn't they get it?" is answered in the Console. One notice to three people: ann has muted its
// type, ben has opted out of its category, tess is in quiet hours. tess, the admin, opens the notification and each recipient says why, with no delivery
// (her inbox has it: quiet hours hold the other channels). Their settings are put back afterwards.
import { launch, signIn, close, check, shot, finish, settled, testTenant, adminApi, producerApi, personApi, tenantPath, until } from './lib.mjs';

const run = Date.now().toString(36);
const type = `sample.e2e.w${run}`;
const category = `whynot${run}`;
const problems = [];

await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await adminApi('PUT', tenantPath('integrations/mail'), { channel: 'email', kind: 'smtp', settings: { server: 'local', from: 'notifications@juice.local' } });
await adminApi('PUT', tenantPath('resolvers/people'), { kind: 'identity', handles: ['user', 'group', 'role'], settings: { directory: 'keycloak' } });
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'Why not', category, schema: { type: 'object' },
  templates: { email: 'Subject: A notice\n\nSomething happened.', inbox: 'Title: A notice\n\nSomething happened.' } });

// Each person's own settings, kept to put back.
const me = user => personApi(user, 'GET', tenantPath('me'));
const people = { ann: (await me('ann')).externalId, ben: (await me('ben')).externalId, tess: (await me('tess')).externalId };
const mute = await personApi('ann', 'POST', tenantPath('me/mutes'), { match: { type }, endsAt: new Date(Date.now() + 3600_000).toISOString() });
const benBefore = await personApi('ben', 'GET', tenantPath('me/preferences'));
await personApi('ben', 'PUT', tenantPath('me/preferences'), { preferences: [...benBefore.map(p => ({ category: p.category, channel: p.channel, mode: p.mode })),
  { category, channel: '*', mode: 'off' }] });
const hhmm = d => `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')}`;
const tessBefore = (await personApi('tess', 'GET', tenantPath('me'))).quietHours ?? null;
await personApi('tess', 'PUT', tenantPath('me/quiet-hours'), { start: hhmm(new Date(Date.now() - 3600_000)), end: hhmm(new Date(Date.now() + 3600_000)), timeZone: 'UTC' });

const browser = await launch();
const refused = [];
try {
  const id = (await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `why-${run}`,
    recipients: Object.values(people).map(id => ({ kind: 'user', id, resolver: 'people' })), data: {} })).id;
  const outcomes = await until(async () => (await adminApi('GET', tenantPath(`notifications/${id}/recipients`))).items,
    // Settled: a held person passes through planned (the planner) before the delivery stage holds them, with their inbox's delivery.
    items => items.length === 3 && ['muted', 'optedOut'].every(o => items.some(r => r.outcome === o))
      && items.some(r => r.outcome === 'deferred' && r.deliveries.length > 0), 60);
  console.log(`     recipients: ${outcomes.map(r => `${r.via.join('/')}=${r.outcome}[${r.deliveries.map(d => d.channel).join(',')}]`).join('; ')}`);

  const page = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/notifications/${id}`, onRefused: e => refused.push(e) });
  await until(async () => page.getByTestId('recipient').count(), n => n === 3, 15);
  for (const [user, outcome, words] of [['ann', 'muted', 'Muted'], ['ben', 'optedOut', 'Opted out'], ['tess', 'deferred', 'quiet hours']]) {
    const row = page.getByTestId('recipient').filter({ hasText: people[user] });
    check(await row.getAttribute('data-outcome').catch(() => null) === outcome, `${user}'s recipient is ${outcome}`, problems);
    const why = await row.getByTestId('recipient-why').innerText().catch(() => '');
    check(why.includes(words), `${user}'s says why: "${why.trim()}"`, problems);
    const channels = outcomes.find(r => r.via.includes(`user:${people[user]}`))?.deliveries.map(d => d.channel) ?? ['?'];
    // Held by quiet hours: the inbox only (it never disturbs), nothing sent anywhere else.
    check(user === 'tess' ? channels.length > 0 && channels.every(c => c === 'inbox') : channels.length === 0, `${user} got no delivery${user === 'tess' ? ' but the inbox' : ''} (${channels.join(', ') || 'none'})`, problems);
  }
  await shot(page, 'why-not');
  await close(page);
} finally {
  await browser.close();
  await personApi('ann', 'DELETE', tenantPath(`me/mutes/${mute.id}`));
  await personApi('ben', 'PUT', tenantPath('me/preferences'), { preferences: benBefore.map(p => ({ category: p.category, channel: p.channel, mode: p.mode })) });
  await (tessBefore ? personApi('tess', 'PUT', tenantPath('me/quiet-hours'), tessBefore) : personApi('tess', 'DELETE', tenantPath('me/quiet-hours')));
}
await settled();
check(refused.length === 0, `no API call refused (${refused.join('; ') || 'none'})`, problems);
await finish(problems);
