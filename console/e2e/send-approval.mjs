// Sends one approval request (to tess, or `node send-approval.mjs alice`) (Approve / Reject) in the tests' tenant, for trying the bell's "Needs action" filter.
import { testTenant, adminApi, producerApi, personApi, tenantPath, until } from './lib.mjs';

const type = 'sample.approval';
await adminApi('POST', '/api/v1/applications', { key: 'sample', name: 'Sample', clientId: 'sample-producer' }, { allow: [409] });
await adminApi('PUT', `/api/v1/applications/sample/tenants/${testTenant}`);
await adminApi('PUT', tenantPath('resolvers/people'), { kind: 'identity', handles: ['user', 'group', 'role'], settings: { directory: 'keycloak' } });
await producerApi('PUT', '/api/v1/applications/sample/types', { key: type, name: 'Approval request', category: 'approvals', displayGroup: 'approval', correlation: 'required',
  schema: { type: 'object' }, templates: { inbox: 'Title: Approve purchase {{ data.po }}\n\n{{ data.who }} asks to spend {{ data.amount }}.' },
  actions: [{ id: 'approve', label: 'Approve' }, { id: 'reject', label: 'Reject' }] });
const ids = { alice: '5f1c2a10-0000-4000-8000-00000000a11c' };
const who = process.argv[2] ?? 'tess';
const person = ids[who] ?? (await personApi(who, 'GET', tenantPath('me'))).externalId;
const po = `PO-${Date.now().toString(36).toUpperCase()}`;
const sent = await producerApi('POST', tenantPath('notifications'), { type, idempotencyKey: po, correlationKey: `purchase/${po}`,
  recipients: [{ kind: 'user', id: person, resolver: 'people' }], data: { po, who: 'ann', amount: '$1,200' } });
console.log('sent', po, sent.id);
const recipients = await until(() => adminApi('GET', tenantPath(`notifications/${sent.id}/recipients`)), r => r.items?.length && r.items.every(i => i.outcome !== 'pending'), 30).catch(e => e.message);
console.log('recipients:', JSON.stringify(recipients.items?.map(i => [i.name, i.outcome, i.deliveries.map(d => `${d.channel}:${d.status}`)]) ?? recipients));
