// M39 (phase-4c-plan §4, Content): a type's templates and display group, changed in the Console and seen in a person's inbox. tess (the
// tests' tenant's admin) finds a fresh type of the sample application (two versions), reads its versions and schema, and edits its inbox
// template beside the preview: a template that doesn't parse shows its error in the preview and is refused on save; a good one previews with
// the schema's sample data, is saved as the tenant's own, and ben's next item has its title. She adds a display group, moves the type into it
// (ben's next item lands there), then puts everything back: the application's template again, the group deleted (the type back in its own).
import { launch, signIn, close, check, shot, finish, settled, base, testTenant, adminApi, producerApi, personApi, tenantPath, until } from './lib.mjs';

const run = Date.now().toString(36);
const type = `sample.e2e.t${run}`;
const group = `e2e-${run}`;
const groupName = `E2E builds ${run}`;
const problems = [];

await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await adminApi('PUT', tenantPath('resolvers/people'), { kind: 'identity', handles: ['user', 'group', 'role'], settings: { directory: 'keycloak' } });
const declare = name => producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name, category: `e2e${run}`,
  schema: { type: 'object', properties: { build: { type: 'integer', examples: [42] } } },
  templates: { inbox: 'Title: App build {{ data.build }}\n\nFrom the application.', email: { en: 'Subject: Build {{ data.build }}\n\nDone.', vi: 'Subject: Bản dựng {{ data.build }}\n\nXong.' } } });
await declare('E2E content');
await declare('E2E content, renamed');

const ben = (await personApi('ben', 'GET', tenantPath('me'))).externalId;
/** Sends one to ben and answers his inbox item of it. */
async function sendToBen(build) {
  const id = (await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: `content-${run}-${build}`,
    recipients: [{ kind: 'user', id: ben, resolver: 'people' }], data: { build } })).id;
  const items = await until(async () => {
    const answer = await personApi('ben', 'GET', `${tenantPath('me/inbox')}?limit=50`);
    return (answer.items ?? answer).filter(i => i.type === type);
  }, list => list.some(i => i.title.includes(String(build))), 30);
  return { id, item: items.find(i => i.title.includes(String(build))) };
}

const browser = await launch();
// Refused by design below: the template that doesn't parse, saved.
const refused = [];
try {
  const tess = await signIn(browser, 'tess', problems, { path: `/tenants/${testTenant}/types`, onRefused: e => refused.push(e) });
  const go = async page => { await tess.goto(`${base}/tenants/${testTenant}/${page}`); };
  const pick = async (testId, option) => {
    await tess.getByTestId(testId).click();
    await tess.getByRole('option', { name: option, exact: true }).click();
  };
  const part = name => tess.locator(`[data-testid=preview-part][data-part="${name}"] pre`);

  // The list finds it; its page shows the latest version, both versions and the schema.
  await go('types');
  await tess.getByTestId('types').waitFor({ timeout: 10000 });
  await tess.getByTestId('types-find').fill(run);
  const row = tess.locator(`[data-testid=type-row][data-key="${type}"]`);
  await row.waitFor({ timeout: 10000 }).catch(() => {});
  check(await row.count() === 1 && (await row.innerText()).includes('inbox'), `the types list finds ${type}, with its inbox template`, problems);
  await row.getByRole('link').click();
  await tess.getByTestId('type-facts').waitFor({ timeout: 10000 });
  check((await tess.getByTestId('type-title').innerText()) === type, 'its page names it', problems);
  check(await tess.locator('[data-testid=type-versions] tbody tr').count() === 2, 'its two versions are listed', problems);
  check((await tess.getByTestId('type-schema').innerText()).includes('"build"'), 'its schema is shown', problems);

  // The inbox template: the application's, previewed with the schema's example (42).
  await pick('template-channel', 'inbox');
  const appTitle = await until(() => part('Title').innerText({ timeout: 1000 }).catch(() => ''), t => t === 'App build 42', 15);
  check(appTitle === 'App build 42', `the application's template previews with the sample data (${appTitle})`, problems);
  check((await tess.getByTestId('template-source').innerText()).includes("The application's"), "the source says it's the application's", problems);

  // One that doesn't parse: the preview says why, and saving it is refused on the field.
  await tess.getByTestId('template-body').fill('Title: {{ data.build }');
  const broken = await until(() => tess.getByTestId('preview-error').innerText({ timeout: 1000 }).catch(() => ''), t => t.length > 0, 15);
  check(broken.length > 0, `a template that doesn't parse shows its error in the preview (${broken.slice(0, 80)})`, problems);
  await tess.getByTestId('template-save').click();
  const bodyError = await tess.locator('[data-testid=field-error][data-pointer="/body"]').innerText({ timeout: 10000 }).catch(() => '');
  check(bodyError.length > 0, `saving it is refused on the template's field (${bodyError.slice(0, 80)})`, problems);

  // A good one: previewed, saved as the tenant's own; ben's next item has its title.
  await tess.getByTestId('template-body').fill('Title: Ours {{ data.build }}\n\nFrom the tenant.');
  const ourTitle = await until(() => part('Title').innerText({ timeout: 1000 }).catch(() => ''), t => t === 'Ours 42', 15);
  check(ourTitle === 'Ours 42', `the edited template previews as typed (${ourTitle})`, problems);
  await tess.getByTestId('template-save').click();
  const source = await until(() => tess.getByTestId('template-source').innerText({ timeout: 1000 }).catch(() => ''), t => t.startsWith("The tenant's own"), 15);
  check(source.startsWith("The tenant's own"), `saved, it is the tenant's own (${source.slice(0, 60)})`, problems);
  const stored = await until(() => adminApi('GET', tenantPath(`types/${type}/templates/inbox`)), s => s.tenant !== null, 10);
  check(stored.tenant === 'Title: Ours {{ data.build }}\n\nFrom the tenant.', `the API has it as the tenant's template (${JSON.stringify(stored.tenant)})`, problems);
  const first = await sendToBen(7);
  check(first.item?.title === 'Ours 7', `ben's item renders with it (${first.item?.title})`, problems);
  // Another channel's locales: the email's vi template is listed.
  await pick('template-channel', 'email');
  await tess.getByTestId('template-locale').click();
  check(await tess.getByRole('option', { name: 'vi', exact: true }).count() === 1, "the email's declared vi locale is offered", problems);
  await tess.getByRole('option', { name: 'vi', exact: true }).click();
  const viSubject = await until(() => part('Subject').innerText({ timeout: 1000 }).catch(() => ''), t => t === 'Bản dựng 42', 15);
  check(viSubject === 'Bản dựng 42', `the vi template previews in vi (${viSubject})`, problems);
  // Unsaved text survives a Refresh; Revert puts back the stored one.
  await tess.getByTestId('template-body').fill('Subject: Unsaved\n\nNot yet.');
  await tess.getByTestId('refresh').click();
  await settled();
  check((await tess.getByTestId('template-body').inputValue()) === 'Subject: Unsaved\n\nNot yet.', 'a Refresh keeps the unsaved text', problems);
  await tess.getByTestId('template-revert').click();
  const reverted = await until(() => tess.getByTestId('template-body').inputValue({ timeout: 1000 }).catch(() => ''), t => t.startsWith('Subject: Bản dựng'), 10);
  check(reverted.startsWith('Subject: Bản dựng'), `Revert puts back the stored template (${JSON.stringify(reverted.slice(0, 40))})`, problems);
  await shot(tess, 'content-type');

  // A display group: added on its page, the type moved into it on the type's page; ben's next item lands there.
  await go('display-groups');
  await tess.getByTestId('group-new').click();
  await tess.getByTestId('group-key').fill(group);
  await tess.getByTestId('group-name').fill(groupName);
  await tess.getByTestId('group-save').click();
  const groupRow = tess.locator(`[data-testid=group-row][data-key="${group}"]`);
  await groupRow.waitFor({ timeout: 10000 }).catch(() => {});
  check(await groupRow.count() === 1, `the group ${group} is added`, problems);
  check(await tess.locator('[data-testid=group-row][data-key="general"] [data-testid=group-delete]').count() === 0,
    'a built-in group offers no delete', problems);
  await go(`types/${encodeURIComponent(type)}`);
  await tess.getByTestId('type-group').waitFor({ timeout: 10000 });
  await settled();
  await pick('type-group-select', `${groupName} (${group})`);
  const now = await until(() => tess.getByTestId('type-group-now').innerText({ timeout: 1000 }).catch(() => ''), t => t.includes(group), 15);
  check(now.includes(group), `the type's items go to ${group} now (${now})`, problems);
  const second = await sendToBen(8);
  check(second.item?.group === group, `ben's next item lands in the group (${second.item?.group})`, problems);
  await go('display-groups');
  const listed = await until(() => groupRow.innerText({ timeout: 1000 }).catch(() => ''), t => t.includes(type), 10);
  check(listed.includes(type), 'the group lists the type moved to it', problems);

  // Put back: the application's template again (on the type's page), the group deleted (the type back in its own).
  await go(`types/${encodeURIComponent(type)}`);
  await tess.getByTestId('templates').waitFor({ timeout: 10000 });
  await pick('template-channel', 'inbox (own)');
  await tess.getByTestId('template-remove').click();
  await tess.getByTestId('template-remove-confirm').click();
  const back = await until(() => tess.getByTestId('template-source').innerText({ timeout: 1000 }).catch(() => ''), t => t.includes("The application's"), 15);
  check(back.includes("The application's"), `the application's template is in effect again (${back.slice(0, 60)})`, problems);
  check((await adminApi('GET', tenantPath(`types/${type}/templates/inbox`))).tenant === null, 'the API has no tenant template any more', problems);
  await go('display-groups');
  await groupRow.waitFor({ timeout: 10000 });
  await groupRow.getByTestId('group-delete').click();
  await groupRow.getByTestId('group-delete-confirm').click();
  await groupRow.waitFor({ state: 'detached', timeout: 10000 }).catch(() => {});
  check(await groupRow.count() === 0, 'the group is deleted', problems);
  const detail = await adminApi('GET', tenantPath(`types/${type}`));
  check(!detail.groupChosen && detail.group !== group, `the type is back in its own group (${detail.group})`, problems);
  const third = await sendToBen(9);
  check(third.item?.title === 'App build 9', `ben's next item renders with the application's template again (${third.item?.title})`, problems);

  // Only the refusal asked for: the broken template's save.
  const unexpected = refused.filter(e => !(e.includes(' 400 ') && e.includes(`/types/${encodeURIComponent(type)}/templates/inbox`)));
  check(unexpected.length === 0, `no other API call refused (${unexpected.join('; ') || 'none'})`, problems);
  check(refused.length === 1, `the broken template's save was refused once (${refused.length})`, problems);
  await close(tess);
} finally {
  // Whatever the journey left (it failed midway): the tenant's template and the group go.
  await adminApi('DELETE', tenantPath(`types/${type}/templates/inbox`), undefined, { allow: [404] });
  await adminApi('DELETE', tenantPath(`types/${type}/display-group`), undefined, { allow: [404] }).catch(() => {});
  await adminApi('DELETE', tenantPath(`display-groups/${group}`), undefined, { allow: [404] }).catch(() => {});
  await browser.close();
}
await finish(problems);
