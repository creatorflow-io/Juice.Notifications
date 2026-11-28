import { of } from 'rxjs';
import { SettingsPage } from '../../../projects/juice-js/notifications-admin/src/lib/pages/settings.page';
import { fieldErrors, formError, render, submitForm, type } from './harness';

describe('settings form', () => {
  it('SettingsForm_AllInvalid_TwoBadThresholds_GiveTwoMessagesEachNamingItsFieldAndRange', async () => {
    const putSettings = vi.fn(() => of({}));
    const f = await render(SettingsPage, { putSettings });
    await type(f, 'settings-storm-threshold', 'many');
    await type(f, 'settings-request-days', '0');
    await submitForm(f, 'settings-form');
    expect(putSettings).not.toHaveBeenCalled();
    const errors = fieldErrors(f);
    expect(errors.map(e => e.pointer)).toEqual(['/stormThreshold', '/requestRetentionDays']);
    expect(errors.every(e => e.inField && e.tag === 'mat-error')).toBe(true);
    expect(errors[0].text).toBe('Storm threshold: a whole number of notifications a minute, 0 (never storms) to 100000; empty for the default.');
    expect(errors[1].text).toBe('Requests and deliveries: whole days, 1 to 3650; empty for the default.');
    expect(formError(f)?.textContent).toContain('Fix the 2 marked fields.');
  });

  it('SettingsForm_ContentLongerThanRequests_NamesTheRequestsDays', async () => {
    const f = await render(SettingsPage, { putSettings: () => of({}) });
    await type(f, 'settings-request-days', '10');
    await type(f, 'settings-content-days', '11');
    await submitForm(f, 'settings-form');
    expect(fieldErrors(f)).toMatchObject([{ pointer: '/contentRetentionDays', text: "Rendered content: whole days, 1 to the requests' 10; empty for the default." }]);
  });

  it('SettingsForm_RequestsInvalid_ContentIsNotCheckedAgainstTheDefaultDays', async () => {
    const f = await render(SettingsPage, { putSettings: () => of({}) });
    await type(f, 'settings-request-days', 'abc');
    // 40 is above the default requests' 30 days: a second, misleading error if it were held against that.
    await type(f, 'settings-content-days', '40');
    await submitForm(f, 'settings-form');
    expect(fieldErrors(f).map(e => e.pointer)).toEqual(['/requestRetentionDays']);
  });

  it('SettingsForm_Valid_SendsEmptyAsTheDefault_Once', async () => {
    const putSettings = vi.fn((..._args: unknown[]) => of({}));
    const f = await render(SettingsPage, { putSettings });
    await type(f, 'settings-storm-threshold', '120');
    await submitForm(f, 'settings-form');
    expect(putSettings).toHaveBeenCalledTimes(1);
    expect(putSettings.mock.calls[0][1]).toEqual({ stormThreshold: 120, requestRetentionDays: null, contentRetentionDays: null });
  });
});
