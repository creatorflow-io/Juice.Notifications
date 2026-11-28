import { of, throwError } from 'rxjs';
import { RoutesPage } from '../../../projects/juice-js/notifications-admin/src/lib/pages/routes.page';
import { byId, click, fieldErrors, formError, inner, refused, render, submitForm, type } from './harness';

const now = '2026-11-28T00:00:00Z';
const integration = { key: 'hooks', channel: 'webhook', kind: 'webhook', settings: {}, secretRef: null, priority: 0, enabled: true, managedBy: 'api', updatedAt: now,
  targets: [{ key: 'ops', address: 'https://hooks.example.com/ops', addressRef: null, enabled: true, managedBy: 'api', updatedAt: now }] };

async function open(putRoute = vi.fn(() => of({}))) {
  const f = await render(RoutesPage, { putRoute, integrations: () => of([integration]) });
  await click(f, 'route-new');
  return { f, putRoute };
}

describe('route form', () => {
  it('RouteForm_AllInvalid_ShowsEveryErrorAtOnce_EachInsideItsFormField', async () => {
    const { f, putRoute } = await open();
    await type(f, 'route-digest', '5000');
    // A label matcher with no label name, and no target chosen.
    inner(f)['draft'].match.labels.push({ name: '', matcher: 'prod' });
    f.detectChanges();
    await submitForm(f, 'route-form');
    expect(putRoute).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors.map(e => e.pointer).sort()).toEqual(['/digestSeconds', '/key', '/match/labels', '/targets']);
    expect(errors.every(e => e.inField && e.tag === 'mat-error')).toBe(true);
    // The digest window's own message says what and how much.
    expect(errors.find(e => e.pointer === '/digestSeconds')!.text).toBe('Digest window: a whole number of seconds, 0 to 3600.');
    expect(formError(f)?.textContent).toContain('Fix the 4 marked fields.');
  });

  it('RouteForm_DigestWindow_LabelIsNotClipped_NoUnitSqueezedIntoIt', async () => {
    const { f } = await open();
    const label = byId(f, 'route-digest').closest('mat-form-field')!.querySelector('mat-label')!;
    expect(label.textContent!.trim()).toBe('Digest window');
  });

  it('RouteForm_SendsWhenValid_AndAnUnplacedApiPointerIsListed', async () => {
    const put = vi.fn(() => throwError(() => refused([{ pointer: '/match/type', message: 'Not a type pattern.' }, { pointer: '/other', message: 'Odd.' }])));
    const { f } = await open(put);
    await type(f, 'route-key', 'all');
    inner(f)['draft'].targets = ['ops'];
    f.detectChanges();
    await submitForm(f, 'route-form');
    expect(put).toHaveBeenCalledTimes(1);
    const errors = fieldErrors(f);
    expect(errors.find(e => e.pointer === '/match/type')).toMatchObject({ inField: true, tag: 'mat-error' });
    expect(errors.find(e => e.pointer === '/other')).toMatchObject({ inField: false });
    expect(errors.find(e => e.pointer === '/other')!.text).toContain('/other');
  });

  it('RouteForm_DuplicateLabelName_IsMarkedOnItsLabel', async () => {
    const { f, putRoute } = await open();
    await type(f, 'route-key', 'all');
    inner(f)['draft'].targets = ['ops'];
    inner(f)['draft'].match.labels.push({ name: 'env', matcher: 'prod' }, { name: 'env', matcher: 'dev' });
    f.detectChanges();
    await submitForm(f, 'route-form');
    expect(putRoute).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.every(e => e.pointer === '/match/labels/env' && e.inField)).toBe(true);
  });
});
