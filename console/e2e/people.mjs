// M40 (phase-4c-plan §4, People): who a request reaches, set up in the Console. tess (the tests' tenant's admin) adds a local resolver and a
// topic resolver (an identity resolver naming no directory is refused on its settings), a local user (their page; locale changed; a name
// emptied is refused), links another identity to them (found by it in the list; what was typed and not saved stays) and a second local
// user's (merged in: their page sends edits to dee's, they can't be added again, and adding them to a group adds dee), puts dee in a new
// group and a new topic, and a request naming the group and one naming the topic each reach them. Then she takes it all down again; the local user stays (the API deletes none).
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, adminApi, producerApi, tenantPath, until } from './lib.mjs';

const run = Date.now().toString(36);
const type = `sample.e2e.p${run}`;
const localResolver = `e2e-local-${run}`;
const topicResolver = `e2e-topics-${run}`;
const user = `dee-${run}`;
// A second local user, merged into dee by linking their identity.
const eve = `eve-${run}`;
// An identity linked to dee that has no local user of its own.
const ghost = `ghost-${run}`;
const alias = { source: 'e2e', externalId: `dee/${run}` };
const group = `e2e-${run}`;
const topic = `e2e.t${run}:alerts`;
const problems = [];

await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'E2E people', category: `e2e${run}`, schema: { type: 'object' },
  templates: { inbox: 'Title: For the team\n\nSomething happened.' } });

/** The subscribers a request naming `recipient` was planned for. */
async function reached(recipient, n) {
  const id = (await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `people-${run}-${n}`, recipients: [recipient], data: {} })).id;
  const items = await until(async () => (await adminApi('GET', tenantPath(`notifications/${id}/recipients`))).items, list => list.length > 0, 30);
  return items.map(r => r.subscriberId);
}

const browser = await launch();
// Refused by design below: the identity resolver without a directory.
const refused = [];
let dee = null;
try {
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/resolvers`, onRefused: e => refused.push(e) });
  const go = async page => { await tess.goto(`${base}/tenants/${testTenant}/${page}`); };
  const pick = async (testId, option) => {
    await tess.getByTestId(testId).click();
    await tess.getByRole('option', { name: option, exact: true }).click();
  };

  // Resolvers: the deployment's kinds offered; one that names no directory refused on its settings; a local one and a topic one added.
  await go('resolvers');
  await tess.getByTestId('resolvers').waitFor({ timeout: 10000 });
  await settled();
  const addResolver = async (key, kind) => {
    await tess.getByTestId('resolver-new').click();
    await tess.getByTestId('resolver-key').fill(key);
    await pick('resolver-kind', kind);
    await tess.getByTestId('resolver-save').click();
  };
  await addResolver(`e2e-bad-${run}`, 'identity');
  const settingsError = await tess.locator('[data-testid=field-error][data-pointer="/settings"]').innerText({ timeout: 10000 }).catch(() => '');
  check(settingsError.includes('directory'), `an identity resolver naming no directory is refused on its settings (${settingsError.slice(0, 80)})`, problems);
  await tess.getByRole('button', { name: 'Cancel' }).click();
  await addResolver(localResolver, 'local');
  const localRow = tess.locator(`[data-testid=resolver-row][data-key="${localResolver}"]`);
  await localRow.waitFor({ timeout: 10000 }).catch(() => {});
  check((await localRow.innerText({ timeout: 1000 }).catch(() => '')).includes('group, user'), `the local resolver ${localResolver} handles groups and users`, problems);
  await addResolver(topicResolver, 'topic');
  const topicRow = tess.locator(`[data-testid=resolver-row][data-key="${topicResolver}"]`);
  await topicRow.waitFor({ timeout: 10000 }).catch(() => {});
  check(await topicRow.count() === 1, `the topic resolver ${topicResolver} is added`, problems);

  // A local user: added, on their page; the locale changed; a name emptied refused (the API would keep it).
  await go('subscribers');
  await tess.getByTestId('local-user-new').click();
  await tess.getByTestId('local-user-id').fill(user);
  await tess.getByTestId('local-user-name').fill('Dee E2E');
  await tess.getByTestId('local-user-save').click();
  await tess.waitForURL(/\/subscribers\/[0-9a-f-]{36}$/, { timeout: 10000 }).catch(() => {});
  const deeId = tess.url().split('/').pop();
  check(/^[0-9a-f-]{36}$/.test(deeId), `adding the local user opens their page (${tess.url()})`, problems);
  await tess.getByTestId('subscriber-facts').waitFor({ timeout: 10000 });
  check((await tess.getByTestId('subscriber-facts').innerText()).includes(`local:${user}`), 'their page names their identity', problems);
  await tess.getByTestId('local-user-locale').fill('vi');
  await tess.getByTestId('local-user-save').click();
  await tess.getByTestId('local-user-saved').waitFor({ timeout: 10000 }).catch(() => {});
  dee = await adminApi('GET', tenantPath(`subscribers/${deeId}`));
  check(dee.locale === 'vi' && dee.name === 'Dee E2E', `their locale is changed (${dee.locale}, ${dee.name})`, problems);
  await tess.getByTestId('local-user-locale').fill('vi-VN');
  check(await tess.getByTestId('local-user-saved').count() === 0, 'typing again takes "Saved." away', problems);
  await tess.getByTestId('local-user-locale').fill('vi');
  await tess.getByTestId('local-user-name').fill('');
  await tess.getByTestId('local-user-save').click();
  const nameError = await tess.locator('[data-testid=field-error][data-pointer="/name"]').innerText({ timeout: 5000 }).catch(() => '');
  check(nameError.includes('not cleared'), `an emptied name is refused, not sent (${nameError.slice(0, 60)})`, problems);
  await tess.getByTestId('refresh').click();
  await until(() => tess.locator('[data-testid=field-error][data-pointer="/name"]').count(), n => n === 0, 10);
  check(await tess.locator('[data-testid=field-error][data-pointer="/name"]').count() === 0, 'Refresh takes the refusal away', problems);
  await tess.getByTestId('local-user-name').fill('Dee E2E');

  // Another identity linked to them; the list finds them by it. A time zone typed and not saved stays through the link's reload.
  await tess.getByTestId('local-user-time-zone').fill('Asia/Ho_Chi_Minh');
  await tess.getByTestId('alias-source').fill(alias.source);
  await tess.getByTestId('alias-id').fill(alias.externalId);
  await tess.getByTestId('alias-link').click();
  const aliasRow = tess.locator(`[data-testid=alias-row][data-identity="${alias.source}:${alias.externalId}"]`);
  await aliasRow.waitFor({ timeout: 10000 }).catch(() => {});
  check(await aliasRow.count() === 1, `the identity ${alias.source}:${alias.externalId} is linked`, problems);
  const typed = await tess.getByTestId('local-user-time-zone').inputValue();
  check(typed === 'Asia/Ho_Chi_Minh', `the time zone typed and not saved stays after the link (${typed || 'emptied'})`, problems);

  // A second local user linked in: merged into dee. Their page sends their details to dee's; "New local user" knows they exist.
  const eveUser = await adminApi('PUT', tenantPath(`local-users/${eve}`), { name: 'Eve E2E' });
  await tess.getByTestId('alias-source').fill('local');
  await tess.getByTestId('alias-id').fill(eve);
  await tess.getByTestId('alias-link').click();
  await tess.locator(`[data-testid=alias-row][data-identity="local:${eve}"]`).waitFor({ timeout: 10000 }).catch(() => {});
  check((await adminApi('GET', tenantPath(`subscribers/${eveUser.id}`))).mergedInto === deeId, `${eve} is merged into ${user}`, problems);
  await go(`subscribers/${eveUser.id}`);
  await tess.getByTestId('subscriber-merged').waitFor({ timeout: 10000 }).catch(() => {});
  check(await tess.getByTestId('subscriber-merged-details').count() === 1 && await tess.getByTestId('local-user-form').count() === 0,
    `${eve}'s page offers no details to change (dee's apply)`, problems);
  await go('subscribers');
  await tess.getByTestId('local-user-new').click();
  await tess.getByTestId('local-user-id').fill(eve);
  await tess.getByTestId('local-user-save').click();
  const again = await tess.locator('[data-testid=field-error][data-pointer="/id"]').innerText({ timeout: 10000 }).catch(() => '');
  check(again.includes('already'), `${eve} can't be added again (${again.slice(0, 60)})`, problems);
  check((await adminApi('GET', tenantPath(`subscribers/${eveUser.id}`))).name === 'Eve E2E', `${eve}'s details are untouched`, problems);
  // An identity linked to dee with no local user of its own: not added (the API would refuse it), the field says whose it is.
  await adminApi('POST', tenantPath(`subscribers/${deeId}/aliases`), { source: 'local', externalId: ghost });
  await tess.getByTestId('local-user-id').fill(ghost);
  await tess.getByTestId('local-user-save').click();
  const linkedTo = await until(() => tess.locator('[data-testid=field-error][data-pointer="/id"]').innerText({ timeout: 1000 }).catch(() => ''),
    t => t.includes('linked to'), 10);
  check(linkedTo.includes('Dee E2E'), `an identity linked to dee isn't added as a local user (${linkedTo.slice(0, 70)})`, problems);
  await adminApi('DELETE', `${tenantPath(`subscribers/${deeId}/aliases`)}?source=local&externalId=${ghost}`);
  await tess.goto(`${base}/tenants/${testTenant}/subscribers?source=${alias.source}&externalId=${encodeURIComponent(alias.externalId)}`);
  const found = await until(() => tess.locator('[data-testid=subscriber-row]').evaluateAll(rows => rows.map(r => r.dataset.id)), ids => ids.length > 0, 10);
  check(found.length === 1 && found[0] === deeId, `the list finds them by the linked identity (${found.join(', ')})`, problems);

  // A group with them in it: a request naming the group through the local resolver reaches them.
  await go('groups');
  await tess.getByTestId('group-new').click();
  await tess.getByTestId('group-key').fill(group);
  await tess.getByTestId('group-name').fill(`E2E team ${run}`);
  await tess.getByTestId('group-save').click();
  await tess.getByTestId('members').waitFor({ timeout: 10000 });
  // Added by the merged identity: who it reaches (dee) goes in.
  await tess.getByTestId('member-id').fill(eve);
  await tess.getByTestId('member-add').click();
  const member = tess.locator(`[data-testid=member-row][data-id="${deeId}"]`);
  await member.waitFor({ timeout: 10000 }).catch(() => {});
  check(await member.count() === 1 && await tess.locator(`[data-testid=member-row][data-id="${eveUser.id}"]`).count() === 0,
    `added as ${eve}, ${user} is in ${group}`, problems);
  await tess.getByTestId('member-id').fill(`nobody-${run}`);
  await tess.getByTestId('member-add').click();
  const nobody = await tess.locator('[data-testid=field-error][data-pointer="/externalId"]').innerText({ timeout: 10000 }).catch(() => '');
  check(nobody.includes('Nobody here'), `someone unknown can't be added (${nobody.slice(0, 60)})`, problems);
  const groupRow = tess.locator(`[data-testid=group-row][data-key="${group}"]`);
  const members = await until(() => groupRow.locator('td').nth(2).innerText({ timeout: 1000 }).catch(() => ''), t => t === '1', 10);
  check(members === '1', `the group counts one member (${members})`, problems);
  const byGroup = await reached({ kind: 'group', id: group, resolver: localResolver }, 1);
  check(byGroup.includes(deeId), `a request naming the group reaches ${user} (${byGroup.join(', ')})`, problems);

  // A topic with them on it: a request naming it through the topic resolver reaches them.
  await go('topics');
  await tess.getByTestId('topic-new').click();
  await tess.getByTestId('topic-key').fill(topic);
  await tess.getByTestId('topic-name').fill(`E2E alerts ${run}`);
  await tess.getByTestId('topic-save').click();
  await tess.getByTestId('members').waitFor({ timeout: 10000 });
  await tess.getByTestId('member-id').fill(user);
  await tess.getByTestId('member-add').click();
  await tess.locator(`[data-testid=member-row][data-id="${deeId}"]`).waitFor({ timeout: 10000 }).catch(() => {});
  const subscribers = await adminApi('GET', tenantPath(`topics/${encodeURIComponent(topic)}/subscribers`));
  check(subscribers.length === 1 && subscribers[0].id === deeId, `${user} subscribes to ${topic}`, problems);
  const byTopic = await reached({ kind: 'topic', key: topic, resolver: topicResolver }, 2);
  check(byTopic.includes(deeId), `a request naming the topic reaches ${user} (${byTopic.join(', ')})`, problems);
  await shot(tess, 'people-topics');

  // Taken down in the Console: off the topic and the topic deleted, out of the group and the group deleted, the identity unlinked, the
  // resolvers deleted.
  await tess.locator(`[data-testid=member-row][data-id="${deeId}"] [data-testid=member-remove]`).click();
  await tess.locator(`[data-testid=member-row][data-id="${deeId}"] [data-testid=member-remove-confirm]`).click();
  await tess.locator(`[data-testid=member-row][data-id="${deeId}"]`).waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  check((await adminApi('GET', tenantPath(`topics/${encodeURIComponent(topic)}/subscribers`))).length === 0, `${user} is off the topic`, problems);
  const topicListRow = tess.locator(`[data-testid=topic-row][data-key="${topic}"]`);
  await topicListRow.getByTestId('topic-delete').click();
  await topicListRow.getByTestId('topic-delete-confirm').click();
  await topicListRow.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  check(await topicListRow.count() === 0, 'the topic is deleted', problems);

  await go('groups');
  await groupRow.waitFor({ timeout: 10000 });
  await groupRow.getByTestId('group-members').click();
  await member.getByTestId('member-remove').click();
  await member.getByTestId('member-remove-confirm').click();
  await member.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  check(await member.count() === 0, `${user} is out of the group`, problems);
  await groupRow.getByTestId('group-delete').click();
  await groupRow.getByTestId('group-delete-confirm').click();
  await groupRow.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  check(await groupRow.count() === 0, 'the group is deleted', problems);

  await go(`subscribers/${deeId}`);
  for (const row of [aliasRow, tess.locator(`[data-testid=alias-row][data-identity="local:${eve}"]`)]) {
    await row.waitFor({ timeout: 10000 });
    await row.getByTestId('alias-unlink').click();
    await row.getByTestId('alias-unlink-confirm').click();
    await row.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  }
  check((await adminApi('GET', tenantPath(`subscribers/${deeId}`))).aliases.length === 0, 'the identities are unlinked', problems);

  await go('resolvers');
  for (const row of [localRow, topicRow]) {
    await row.waitFor({ timeout: 10000 });
    await row.getByTestId('resolver-delete').click();
    await row.getByTestId('resolver-delete-confirm').click();
    await row.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  }
  const left = (await adminApi('GET', tenantPath('resolvers'))).filter(r => r.key.includes(run));
  check(left.length === 0, `the resolvers are deleted (${left.map(r => r.key).join(', ') || 'none left'})`, problems);

  // Only the refusal asked for: the identity resolver's save.
  const unexpected = refused.filter(e => !(e.includes(' 400 ') && e.includes(`/resolvers/e2e-bad-${run}`)));
  check(unexpected.length === 0, `no other API call refused (${unexpected.join('; ') || 'none'})`, problems);
  check(refused.length === 1, `the identity resolver's save was refused once (${refused.length})`, problems);
  await close(tess);
} finally {
  // Whatever the journey left (it failed midway).
  await adminApi('DELETE', tenantPath(`topics/${encodeURIComponent(topic)}`), undefined, { allow: [404] }).catch(() => {});
  await adminApi('DELETE', tenantPath(`groups/${group}`), undefined, { allow: [404] }).catch(() => {});
  for (const key of [localResolver, topicResolver, `e2e-bad-${run}`]) {
    await adminApi('DELETE', tenantPath(`resolvers/${key}`), undefined, { allow: [404] }).catch(() => {});
  }
  if (dee) {
    for (const a of [alias, { source: 'local', externalId: eve }, { source: 'local', externalId: ghost }]) {
      await adminApi('DELETE', `${tenantPath(`subscribers/${dee.id}/aliases`)}?source=${a.source}&externalId=${encodeURIComponent(a.externalId)}`,
        undefined, { allow: [404] }).catch(() => {});
    }
  }
  await browser.close();
}
await finish(problems);
