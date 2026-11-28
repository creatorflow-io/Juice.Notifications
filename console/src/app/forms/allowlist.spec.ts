import { of } from 'rxjs';
import { AllowlistPage, destinationIssue, portIssues } from '../../../projects/juice-js/notifications-admin/src/lib/pages/allowlist.page';
import { click, fieldErrors, formError, render, submitForm, type } from './harness';

async function open(putAllowlistEntry = vi.fn((..._args: unknown[]) => of({}))) {
  const f = await render(AllowlistPage, { putAllowlistEntry });
  await click(f, 'allow-new');
  return { f, putAllowlistEntry };
}

describe('allowlist form', () => {
  it('AllowlistForm_AllInvalid_ShowsEveryErrorAtOnce_NamingEachPortEntry', async () => {
    const { f, putAllowlistEntry } = await open();
    await type(f, 'allow-ports', '80, 99999, abc');
    await submitForm(f, 'allow-form');
    expect(putAllowlistEntry).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors.map(e => e.pointer)).toEqual(['/destination', '/ports', '/ports']);
    expect(errors.every(e => e.inField && e.tag === 'mat-error')).toBe(true);
    expect(errors[1].text).toBe('Port 99999 (entry 2): a whole number from 1 to 65535.');
    expect(errors[2].text).toBe('Port abc (entry 3): a whole number from 1 to 65535.');
    expect(formError(f)?.textContent).toContain('Fix the 2 marked fields.');
  });

  it('Allowlist_NonCanonicalInput_IsAnError_ThenTheFixedFormSendsTrimmed', async () => {
    const { f, putAllowlistEntry } = await open();
    for (const bad of ['HTTPS://Host.Example.COM:443/', '10.0.0.0/33', '::1/129', 'host.example.com:8443', '<script>&"\'', 'a/b/c']) {
      await type(f, 'allow-destination', bad);
      await submitForm(f, 'allow-form');
      const errors = fieldErrors(f);
      expect(errors, JSON.stringify(bad)).toHaveLength(1);
      expect(errors[0].pointer).toBe('/destination');
    }
    // A valid host, with its case and spaces as typed, is sent trimmed.
    await type(f, 'allow-destination', '  10.20.0.0/16 ');
    await submitForm(f, 'allow-form');
    expect(putAllowlistEntry).toHaveBeenCalledTimes(1);
    expect(putAllowlistEntry.mock.calls[0][1]).toEqual({ destination: '10.20.0.0/16', ports: [] });
  });

  it('Allowlist_MarkupInPorts_IsShownAsText_NeverAsMarkup', async () => {
    const { f } = await open();
    await type(f, 'allow-ports', '<img/src=x/onerror=alert(1)>');
    await submitForm(f, 'allow-form');
    const host = f.nativeElement as HTMLElement;
    expect(host.querySelector('img')).toBeNull();
    expect(host.textContent).toContain('<img/src=x/onerror=alert(1)>');
  });

  it('Allowlist_Validators_CoverTheEdges', () => {
    expect(destinationIssue('10.20.0.5')).toBeNull();
    expect(destinationIssue('10.20.0.0/32')).toBeNull();
    expect(destinationIssue('fd00::/8')).toBeNull();
    expect(destinationIssue('::1/128')).toBeNull();
    expect(destinationIssue('intranet.example.com')).toBeNull();
    // What the API takes: a trailing dot, an international name.
    expect(destinationIssue('hooks.example.com.')).toBeNull();
    expect(destinationIssue('bücher.example')).toBeNull();
    expect(destinationIssue('')).not.toBeNull();
    expect(portIssues(['443', '8443'])).toEqual([]);
    expect(portIssues(['0', '65535', '65536', '-1', '1.5', '1e3', '08']).map(i => i.message.split(' (')[0])).toEqual(
      ['Port 0', 'Port 65536', 'Port -1', 'Port 1.5', 'Port 1e3']);
  });
});
