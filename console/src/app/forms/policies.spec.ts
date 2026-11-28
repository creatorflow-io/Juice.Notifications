import { of } from 'rxjs';
import { EscalationPoliciesPage } from '../../../projects/juice-js/notifications-admin/src/lib/pages/escalation-policies.page';
import { click, fieldErrors, formError, inner, render, submitForm, type } from './harness';

async function open(putEscalationPolicy = vi.fn(() => of({}))) {
  const f = await render(EscalationPoliciesPage, { putEscalationPolicy });
  await click(f, 'policy-new');
  return { f, putEscalationPolicy };
}

describe('escalation policy form', () => {
  it('PolicyForm_AllInvalid_ShowsEveryErrorAtOnce_EachInsideItsFormField', async () => {
    const { f, putEscalationPolicy } = await open();
    // An empty key, a step whose wait was cleared, a second step with a negative wait, and a third with no recipient.
    inner(f)['draft'].steps = [
      { afterMinutes: null, recipients: [{ kind: 'route', id: '', key: '', resolver: '' }] },
      { afterMinutes: -5, recipients: [{ kind: 'route', id: '', key: '', resolver: '' }] },
      { afterMinutes: 10, recipients: [] },
    ];
    f.detectChanges();
    await submitForm(f, 'policy-form');
    expect(putEscalationPolicy).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors.map(e => e.pointer).sort()).toEqual(['/key', '/steps/0/afterMinutes', '/steps/1/afterMinutes', '/steps/2/recipients']);
    // The ones with a field sit in it; the empty step has none, so it is listed with its pointer, not lost.
    expect(errors.filter(e => e.inField).map(e => e.pointer).sort()).toEqual(['/key', '/steps/0/afterMinutes', '/steps/1/afterMinutes']);
    expect(errors.find(e => e.pointer === '/steps/1/afterMinutes')!.text).toBe('Step 2, After: a whole number of minutes, 0 or more (0 for at once).');
    expect(errors.find(e => e.pointer === '/steps/2/recipients')!.text).toContain('/steps/2/recipients');
    expect(formError(f)?.textContent).toContain('Fix the 4 marked fields.');
  });

  it('PolicyForm_FixingOneStep_LeavesTheOthersMarked_ThenSendsOnce', async () => {
    const { f, putEscalationPolicy } = await open();
    inner(f)['draft'].steps = [
      { afterMinutes: null, recipients: [{ kind: 'route', id: '', key: '', resolver: '' }] },
      { afterMinutes: null, recipients: [{ kind: 'route', id: '', key: '', resolver: '' }] },
    ];
    f.detectChanges();
    await type(f, 'policy-key', 'escalate');
    await submitForm(f, 'policy-form');
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/steps/0/afterMinutes', '/steps/1/afterMinutes']);
    const afters = (f.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>('[data-testid="step-after"]');
    afters[0].value = '5';
    afters[0].dispatchEvent(new Event('input', { bubbles: true }));
    await new Promise(resolve => setTimeout(resolve, 5));
    f.detectChanges();
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/steps/1/afterMinutes']);
    afters[1].value = '15';
    afters[1].dispatchEvent(new Event('input', { bubbles: true }));
    await submitForm(f, 'policy-form');
    expect(putEscalationPolicy).toHaveBeenCalledTimes(1);
  });
});
