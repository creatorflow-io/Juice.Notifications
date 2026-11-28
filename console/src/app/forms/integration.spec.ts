import { of, throwError } from 'rxjs';
import { IntegrationForm } from '../../../projects/juice-js/notifications-admin/src/lib/pages/integration-form';
import { byId, fieldErrors, formError, inner, refused, render, settle, submitForm, type } from './harness';

const providers = [{ channel: 'webhook', kind: 'webhook', signs: true, registersCallbacks: false }];
const integration = { key: 'hooks', channel: 'webhook', kind: 'webhook', settings: { a: 1 }, secretRef: 'env:HOOK', priority: 1, enabled: true, managedBy: 'api',
  updatedAt: '2026-11-28T00:00:00Z', targets: [] };

async function form(putIntegration = vi.fn(() => of(integration)), inputs: Record<string, unknown> = {}) {
  const f = await render(IntegrationForm, { putIntegration }, { providers, takenKeys: ['taken'], ...inputs });
  return { f, putIntegration };
}

describe('integration form', () => {
  it('IntegrationForm_AllInvalid_ShowsEveryErrorAtOnce_EachInsideItsFormField', async () => {
    const { f, putIntegration } = await form();
    // Bad JSON, a negative priority, an empty key and no channel or provider chosen.
    await type(f, 'integration-settings', '{ "a": ');
    await type(f, 'integration-priority', '-3');
    await submitForm(f, 'integration-form');
    expect(putIntegration).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors.map(e => e.pointer).sort()).toEqual(['/channel', '/key', '/kind', '/priority', '/settings']);
    expect(errors.every(e => e.inField && e.tag === 'mat-error')).toBe(true);
    expect(errors.find(e => e.pointer === '/settings')!.text).toMatch(/^Settings: not JSON/);
    expect(errors.find(e => e.pointer === '/priority')!.text).toContain('Priority');
    // Each control says so: invalid, and described by its error.
    expect(byId(f, 'integration-key').getAttribute('aria-invalid')).toBe('true');
    expect(byId(f, 'integration-key').getAttribute('aria-describedby')).toBeTruthy();
    expect(formError(f)?.textContent).toContain('Fix the 5 marked fields.');
  });

  it('IntegrationForm_TakenKey_And_KeyThatIsNotAKey_AreErrorsOnTheKey', async () => {
    const { f, putIntegration } = await form();
    inner(f)['draft'].channel = 'webhook';
    inner(f)['draft'].kind = 'webhook';
    for (const bad of ['taken', ' Prod-1 ', 'a'.repeat(256), '<script>&"\'', 'bell\u0007']) {
      await type(f, 'integration-key', bad);
      await submitForm(f, 'integration-form');
      const errors = fieldErrors(f);
      expect(errors, `key ${JSON.stringify(bad)}`).toHaveLength(1);
      expect(errors[0].pointer).toBe('/key');
    }
    expect(putIntegration).not.toHaveBeenCalled();
    // A valid one, trimmed, is sent.
    await type(f, 'integration-key', '  prod-1 ');
    await submitForm(f, 'integration-form');
    expect(putIntegration).toHaveBeenCalledTimes(1);
    expect((putIntegration.mock.calls[0] as unknown[])[1]).toBe('prod-1');
  });

  it('Messages_AreEscaped_NeverMarkupInTheDocument', async () => {
    const { f } = await form();
    inner(f)['draft'].channel = 'webhook';
    inner(f)['draft'].kind = 'webhook';
    await type(f, 'integration-key', '<img src=x onerror=alert(1)>');
    await type(f, 'integration-settings', '<script>alert(1)</script>');
    await submitForm(f, 'integration-form');
    expect((f.nativeElement as HTMLElement).querySelector('script, img[src="x"]')).toBeNull();
    expect(fieldErrors(f).length).toBeGreaterThan(0);
  });

  it('Save_ConcurrentChange_ConflictSummaryShowsReload_FieldsKeptAsTyped', async () => {
    const put = vi.fn(() => throwError(() => ({ status: 412, error: { status: 412, code: 'concurrent_change', detail: 'changed' } })));
    const reload = vi.fn();
    const { f } = await form(put, { existing: integration, reloadFn: reload });
    await type(f, 'integration-priority', '7');
    await submitForm(f, 'integration-form');
    expect(put).toHaveBeenCalledTimes(1);
    expect(formError(f)?.textContent).toContain('Someone changed this meanwhile');
    (byId(f, 'form-reload') as HTMLElement).click();
    expect(reload).toHaveBeenCalled();
    expect((byId(f, 'integration-priority') as HTMLInputElement).value).toBe('7');
    expect(fieldErrors(f)).toHaveLength(0);
  });

  it('DependencyDown_ZeroStatusOr503_OneFormSentence_NoFieldError', async () => {
    for (const failure of [{ status: 0, error: null }, { status: 503, error: { status: 503, title: 'Service Unavailable', detail: 'The store is down.' } }]) {
      const put = vi.fn(() => throwError(() => failure));
      const { f } = await form(put, { existing: integration });
      await submitForm(f, 'integration-form');
      expect(put).toHaveBeenCalledTimes(1);
      expect(fieldErrors(f)).toHaveLength(0);
      expect((f.nativeElement as HTMLElement).querySelectorAll('mat-error')).toHaveLength(0);
      expect(formError(f)?.textContent).toMatch(failure.status === 0 ? /couldn't be reached/ : /store is down/);
    }
  });

  it('ApiProblem_FieldErrors_RenderInTheirFieldsNotBelowTheRow', async () => {
    const put = vi.fn(() => throwError(() => refused([
      { pointer: '/key', message: 'The key is reserved.' }, { pointer: '/settings/server', message: 'No such SMTP server.' },
      { pointer: '/secretRef', message: 'Not under an allowed scheme.' }, { pointer: '/elsewhere/0', message: 'Unplaced refusal.' }])));
    const { f } = await form(put, { existing: integration });
    await submitForm(f, 'integration-form');
    const errors = fieldErrors(f);
    const inFields = errors.filter(e => e.inField);
    expect(inFields.map(e => e.pointer).sort()).toEqual(['/key', '/secretRef', '/settings/server']);
    // The pointer no field shows is listed, with its pointer, never lost.
    const unplaced = errors.filter(e => !e.inField);
    expect(unplaced).toHaveLength(1);
    expect(unplaced[0].text).toContain('/elsewhere/0');
    expect(unplaced[0].text).toContain('Unplaced refusal.');
    // The summary counts all four, and never points below.
    expect(formError(f)?.textContent).toContain('Fix the 4 marked fields.');
    expect(formError(f)?.textContent).not.toMatch(/see below/i);
  });

  it('Summary_SaysSeeBelowNever_AndMatchesCount', async () => {
    const { f } = await form();
    await submitForm(f, 'integration-form');
    const shown = fieldErrors(f).length;
    expect(shown).toBeGreaterThan(1);
    expect(formError(f)!.textContent!.trim()).toBe(`Fix the ${shown} marked fields.`);
    expect(formError(f)!.textContent).not.toMatch(/below/i);
    // Fixing one lowers the count in the same sentence; none left, no sentence.
    await type(f, 'integration-key', 'k');
    expect(formError(f)!.textContent!.trim()).toBe(`Fix the ${shown - 1} marked field${shown - 1 === 1 ? '' : 's'}.`);
  });

  it('Summary_SameFontSizeAsFieldErrors_AndFirstInvalidFieldFocused', async () => {
    const { f } = await form();
    inner(f)['draft'].channel = 'webhook';
    inner(f)['draft'].kind = 'webhook';
    await type(f, 'integration-priority', '-1');
    await submitForm(f, 'integration-form');
    // The summary is small like the field errors (12px: .na-small, and Material's own error text).
    expect(formError(f)!.classList.contains('na-small')).toBe(true);
    // The first marked field in the document (the key, before the priority) has the focus.
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(document.activeElement).toBe(byId(f, 'integration-key'));
  });

  it('Errors_SurviveTheBlurOfAFieldTypedBeforeTheSubmit', async () => {
    // The browser fires `change` on blur: the focus moving to the first marked field must not take another field's error away.
    const { f } = await form();
    inner(f)['draft'].channel = 'webhook';
    inner(f)['draft'].kind = 'webhook';
    await type(f, 'integration-priority', '-9');
    await submitForm(f, 'integration-form');
    byId(f, 'integration-priority').dispatchEvent(new Event('change', { bubbles: true }));
    await settle(f);
    expect(fieldErrors(f).map(e => e.pointer).sort()).toEqual(['/key', '/priority']);
  });

  it('Validate_ErrorsClearOnEdit_ReSubmitSendsOnce', async () => {
    const put = vi.fn(() => of(integration));
    const { f } = await form(put);
    inner(f)['draft'].channel = 'webhook';
    inner(f)['draft'].kind = 'webhook';
    await submitForm(f, 'integration-form');
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/key']);
    // Editing a field takes its own error away, nothing else.
    await type(f, 'integration-settings', '{}');
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/key']);
    await type(f, 'integration-key', 'good-key');
    expect(fieldErrors(f)).toHaveLength(0);
    expect(formError(f)).toBeNull();
    await submitForm(f, 'integration-form');
    await settle(f);
    expect(put).toHaveBeenCalledTimes(1);
  });
});
