import { ComponentFixture } from '@angular/core/testing';
import { NEVER, of, throwError } from 'rxjs';
import { MutesPage } from '../../../projects/juice-js/notifications-admin/src/lib/pages/mutes.page';
import { byId, click, fieldErrors, formError, hasId, inner, refused, render, settle, submitForm, type } from './harness';

const MUTE = { id: 'm1', match: {}, startsAt: '2026-11-28T00:00:00Z', endsAt: null, reason: null, createdBy: null, createdAt: '2026-11-28T00:00:00Z',
  cancelledAt: null, appliesToMandatory: false };

/** The mutes page with its New mute form open; `addMute` is what the form sends through. */
async function open(addMute = vi.fn(() => of(MUTE))): Promise<{ f: ComponentFixture<MutesPage>; addMute: ReturnType<typeof vi.fn> }> {
  const f = await render(MutesPage, { addMute });
  await click(f, 'mute-new');
  return { f, addMute };
}

/** Ticks "Mute everything" the way the checkbox's model does. */
async function tick(f: ComponentFixture<MutesPage>, on = true): Promise<void> {
  inner(f)['draft'].everything = on;
  f.detectChanges();
  await settle(f);
}

describe('mutes form', () => {
  it('Mutes_EmptyForm_Save_NothingSent_ScopeErrorAtMatch', async () => {
    const { f, addMute } = await open();
    await submitForm(f, 'mute-form');
    expect(addMute).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors).toHaveLength(1);
    expect(errors[0]).toMatchObject({ pointer: '/match', text: 'Name a type or a label, or choose Mute everything.', inField: true, tag: 'mat-error' });
    expect(formError(f)?.textContent).toContain('Fix the 1 marked field.');
  });

  it('Mutes_Everything_ConfirmStep_OnlyConfirmSends_BackSendsNothing', async () => {
    const { f, addMute } = await open();
    await tick(f);
    await submitForm(f, 'mute-form');
    expect(byId(f, 'mute-confirm-title').textContent).toContain('Mute every notification for this tenant?');
    expect(addMute).not.toHaveBeenCalled();

    await click(f, 'mute-back');
    expect(hasId(f, 'mute-confirm')).toBe(false);
    expect(hasId(f, 'mute-save')).toBe(true);
    expect(inner(f)['draft'].everything).toBe(true);
    expect(addMute).not.toHaveBeenCalled();

    await submitForm(f, 'mute-form');
    await click(f, 'mute-confirm');
    expect(addMute).toHaveBeenCalledTimes(1);
    expect(addMute.mock.calls[0][1]).toMatchObject({ match: {} });
  });

  it('Mutes_EverythingChecked_ThenTypeTyped_ScopeNotWidened', async () => {
    const { f, addMute } = await open();
    await tick(f);
    await type(f, 'match-types', 'sample.alert.*');
    await submitForm(f, 'mute-form');
    // The scope typed is what is sent, with no "everything?" step, and the box is cleared.
    expect(hasId(f, 'mute-confirm')).toBe(false);
    expect(addMute).toHaveBeenCalledTimes(1);
    expect(addMute.mock.calls[0][1]).toMatchObject({ match: { type: 'sample.alert.*' } });
    expect(inner(f)['draft'].everything).toBe(false);
  });

  it('Mutes_TypesOnlyWhitespaceOrCommas_Counts_AsEmpty', async () => {
    const { f, addMute } = await open();
    inner(f)['draft'].match.types = '  , ,\n';
    f.detectChanges();
    await submitForm(f, 'mute-form');
    expect(addMute).not.toHaveBeenCalled();
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/match']);

    // A label row with an empty name names no label either.
    inner(f)['draft'].match.labels.push({ name: '', matcher: '' });
    f.detectChanges();
    await submitForm(f, 'mute-form');
    expect(addMute).not.toHaveBeenCalled();
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/match']);
  });

  it('Mutes_Everything_ApiRefusal_StaysOnConfirmStep_ShowsApiError', async () => {
    const addMute = vi.fn(() => throwError(() => refused([{ pointer: '/match', message: 'The API refuses a mute of everything here.' }])));
    const { f } = await open(addMute);
    await tick(f);
    await submitForm(f, 'mute-form');
    await click(f, 'mute-confirm');
    expect(addMute).toHaveBeenCalledTimes(1);
    expect(hasId(f, 'mute-confirm')).toBe(true);
    expect(root(f)).toContain('The API refuses a mute of everything here.');
  });

  it('Mutes_ReasonAt500AndControlChar_ErrorOnReason', async () => {
    const { f, addMute } = await open();
    await type(f, 'match-types', 'a.b');
    // 500 is the limit: allowed.
    await type(f, 'mute-reason', 'x'.repeat(500));
    await submitForm(f, 'mute-form');
    expect(addMute).toHaveBeenCalledTimes(1);

    const second = await open();
    await type(second.f, 'match-types', 'a.b');
    await type(second.f, 'mute-reason', 'x'.repeat(501));
    await submitForm(second.f, 'mute-form');
    expect(second.addMute).not.toHaveBeenCalled();
    expect(fieldErrors(second.f)).toMatchObject([{ pointer: '/reason', inField: true }]);

    const third = await open();
    await type(third.f, 'match-types', 'a.b');
    await type(third.f, 'mute-reason', 'bell\u0007here');
    await submitForm(third.f, 'mute-form');
    expect(third.addMute).not.toHaveBeenCalled();
    expect(fieldErrors(third.f).map(e => e.pointer)).toEqual(['/reason']);
  });

  it('Mutes_EndsBeforeStarts_ErrorOnEnds', async () => {
    const { f, addMute } = await open();
    await type(f, 'match-types', 'a.b');
    await type(f, 'mute-starts', '2026-12-02T10:00');
    await type(f, 'mute-ends', '2026-12-01T10:00');
    await submitForm(f, 'mute-form');
    expect(addMute).not.toHaveBeenCalled();
    expect(fieldErrors(f)).toMatchObject([{ pointer: '/endsAt', text: 'Ends: must be after Starts.', inField: true }]);
  });

  it('Mutes_DoubleSubmit_SendsOnce_AndANewFormStartsClean', async () => {
    const addMute = vi.fn(() => NEVER);
    const { f } = await open(addMute);
    await type(f, 'match-types', 'a.b');
    await submitForm(f, 'mute-form');
    await submitForm(f, 'mute-form');
    expect(addMute).toHaveBeenCalledTimes(1);

    // A draft left behind (everything ticked, at the confirm step) is gone after Cancel and New.
    const g = await open();
    await tick(g.f);
    await submitForm(g.f, 'mute-form');
    expect(hasId(g.f, 'mute-confirm')).toBe(true);
    await click(g.f, 'mute-back');
    byId(g.f, 'mute-form').querySelector<HTMLButtonElement>('.na-actions button:not([type=submit])')!.click();
    await settle(g.f);
    await click(g.f, 'mute-new');
    expect(inner(g.f)['draft'].everything).toBe(false);
    expect(hasId(g.f, 'mute-confirm')).toBe(false);
    await submitForm(g.f, 'mute-form');
    expect(g.addMute).not.toHaveBeenCalled();
  });

  it('FormOpen_TenantChanges_ErrorsAndDraftCleared', async () => {
    const { f, addMute } = await open();
    await tick(f);
    await submitForm(f, 'mute-form');
    expect(hasId(f, 'mute-confirm')).toBe(true);
    f.componentRef.setInput('tenantId', 't2');
    await settle(f);
    expect(hasId(f, 'mute-form')).toBe(false);
    expect(inner(f)['draft'].everything).toBe(false);
    expect(addMute).not.toHaveBeenCalled();
  });
});

const root = (f: ComponentFixture<unknown>) => (f.nativeElement as HTMLElement).textContent ?? '';
