import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Type } from '@angular/core';
import { of } from 'rxjs';
// From the library's source, as the pages are: its services must be the same classes the pages inject.
import { AccessService, readAccess } from '../../projects/juice-js/notifications-admin/src/lib/core/access.service';
import { NotificationsAdminApi } from '../../projects/juice-js/notifications-admin/src/lib/core/api.service';
import { AdminRight, NOTIFICATIONS_ADMIN_PAGES } from '../../projects/juice-js/notifications-admin/src/lib/core/pages';
import { RoutesPage } from '../../projects/juice-js/notifications-admin/src/lib/pages/routes.page';
import { IntegrationPage } from '../../projects/juice-js/notifications-admin/src/lib/pages/integration.page';
import { DeliveryPage } from '../../projects/juice-js/notifications-admin/src/lib/pages/delivery.page';
import { DeliveriesPage } from '../../projects/juice-js/notifications-admin/src/lib/pages/deliveries.page';

const tenant = 't1';
const now = '2026-11-26T10:00:00Z';
const integration = {
  key: 'hooks', channel: 'webhook', kind: 'webhook', settings: {}, secretRef: 'env:HOOK', priority: 0, enabled: true, managedBy: 'api', updatedAt: now,
  targets: [{ key: 'ops', address: 'https://hooks.example.com/ops', addressRef: null, enabled: true, managedBy: 'api', updatedAt: now }],
};
const delivery = {
  id: 'd1', requestId: 'r1', type: 'app.thing', channel: 'webhook', integration: 'hooks', destinationKey: 'it:1', status: 'failed', attempts: 1,
  failureCategory: 'destination', lastError: '404', createdAt: now, firstAttemptAt: now, completedAt: now, failovers: 0,
};
const api = {
  routes: () => of([{ key: 'all', match: { type: 'app.*' }, targets: ['ops'], enabled: true, managedBy: 'api', updatedAt: now, digestSeconds: 0 }]),
  integrations: () => of([integration]),
  providers: () => of([{ channel: 'webhook', kind: 'webhook', signs: true, registersCallbacks: false }]),
  delivery: () => of({ delivery, destinationSnapshot: '{}', templateVersion: 1, locale: 'en', rendererVersion: 'x', providerMessageId: null, items: [], attempts: [] }),
  deliveries: () => of({ items: [delivery], next: null }),
};

/** The page, rendered for a user with `rights` in the tenant (embedded), its inputs set; the data-testids it shows. */
async function shown(page: Type<unknown>, rights: AdminRight[], inputs: Record<string, string>): Promise<Set<string>> {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]), provideNoopAnimations(),
      { provide: NotificationsAdminApi, useValue: api },
      { provide: AccessService, useValue: { load: () => of(readAccess({ platformAdmin: false, allTenants: false, mode: 'embedded',
        pages: NOTIFICATIONS_ADMIN_PAGES.embedded, tenants: [{ id: tenant, name: null, rights }] })) } },
    ],
  });
  const fixture = TestBed.createComponent(page);
  fixture.componentRef.setInput('tenantId', tenant);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.autoDetectChanges();
  await fixture.whenStable();
  return new Set([...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid')!));
}

/**
 * The pages hide what the user's rights don't allow (phase 5 M44-4, #17 review): a reader sees the data and no control; an operator gets
 * retry and replay and nothing that configures; an admin gets them all. The API refuses them anyway (not_allowed).
 */
describe('notifications admin pages, per right', () => {
  const pages: [string, Type<unknown>, Record<string, string>, string, string[]][] = [
    ['routes', RoutesPage, {}, 'route-row', ['route-new', 'route-edit', 'route-delete']],
    ['integration', IntegrationPage, { key: 'hooks' }, 'integration-state', ['integration-save', 'integration-delete', 'target-new', 'target-edit']],
    ['delivery', DeliveryPage, { id: 'd1' }, 'delivery-status', ['retry']],
    ['deliveries', DeliveriesPage, { status: 'failed' }, 'delivery-row', ['replay']],
  ];
  const operating = new Set(['retry', 'replay']);

  for (const [name, page, inputs, data, controls] of pages) {
    it(`${name}: a reader sees the data and no control, an operator only what operates, an admin every control`, async () => {
      for (const [rights, expected] of [
        [['read'], () => false],
        [['read', 'operate'], (c: string) => operating.has(c)],
        [['read', 'operate', 'configure'], () => true],
      ] as [AdminRight[], (c: string) => boolean][]) {
        const ids = await shown(page, rights, inputs);
        expect(ids.has(data), `${name} as ${rights}: shows ${data}`).toBe(true);
        for (const control of controls) expect(ids.has(control), `${name} as ${rights}: ${control}`).toBe(expected(control));
      }
    });
  }

  it('shows a reader the integration read-only: what it holds, nothing to submit', async () => {
    const ids = await shown(IntegrationPage, ['read', 'operate'], { key: 'hooks' });
    expect(ids.has('integration-read-only')).toBe(true);
    expect(ids.has('integration-form')).toBe(false);
  });
});
