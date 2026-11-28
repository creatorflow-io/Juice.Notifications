import { of, throwError } from 'rxjs';
import { RoutesPage } from '../../../projects/juice-js/notifications-admin/src/lib/pages/routes.page';
import { byId, click, fieldErrors, formError, inner, refused, render, settle, submitForm, type } from './harness';

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
    expect(errors.map(e => e.pointer).sort()).toEqual(['/digestSeconds', '/key', '/match/labels/#1', '/targets']);
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
    // One message, on the second row only (counted once, shown once).
    expect(errors).toHaveLength(1);
    expect(errors[0]).toMatchObject({ pointer: '/match/labels/#2', inField: true });
  });

  it('RouteForm_NamelessRows_EachKeepsItsOwnError_UntilItsOwnEdit', async () => {
    const { f, putRoute } = await open();
    await type(f, 'route-key', 'all');
    inner(f)['draft'].targets = ['ops'];
    inner(f)['draft'].match.labels.push({ name: '', matcher: 'a' }, { name: '', matcher: 'b' });
    f.detectChanges();
    await submitForm(f, 'route-form');
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/match/labels/#1', '/match/labels/#2']);
    const names = (f.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>('[data-testid="match-label-name"]');
    names[0].value = 'env';
    names[0].dispatchEvent(new Event('input', { bubbles: true }));
    await settle(f);
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/match/labels/#2']);
    expect(putRoute).not.toHaveBeenCalled();
  });

  it('ApiProblem_MatchErrors_EveryCountedFieldIsRendered_SummaryEqualsRenderedFields', async () => {
    const put = vi.fn(() => throwError(() => refused([
      { pointer: '/match/labels', message: 'Up to 8 label matchers.' }, { pointer: '/match/labels', message: 'Up to 2 =~ matchers.' },
      { pointer: '/match/labels/zzz', message: 'Labels map a label name to a matcher.' }, { pointer: '/match/foo', message: 'Unknown part.' },
      { pointer: '/key', message: 'Reserved.' }])));
    const { f } = await open(put);
    await type(f, 'route-key', 'all');
    inner(f)['draft'].targets = ['ops'];
    f.detectChanges();
    await submitForm(f, 'route-form');
    const errors = fieldErrors(f);
    // Every message is on screen (5), over 5 distinct pointers... of which /match/labels twice: 4 fields.
    expect(errors.map(e => e.text).sort()).toEqual(['Labels map a label name to a matcher.', 'Reserved.', 'Unknown part.', 'Up to 2 =~ matchers.',
      'Up to 8 label matchers.'].sort());
    const fields = new Set(errors.map(e => e.pointer)).size;
    expect(fields).toBe(4);
    expect(formError(f)!.textContent!.trim()).toBe(`Fix the ${fields} marked fields.`);
  });
});
