import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
// From the library's source, as the pages are: its services must be the same classes the pages inject.
import { AccessService, readAccess } from '../../../projects/juice-js/notifications-admin/src/lib/core/access.service';
import { NotificationsAdminApi } from '../../../projects/juice-js/notifications-admin/src/lib/core/api.service';
import { NOTIFICATIONS_ADMIN_PAGES } from '../../../projects/juice-js/notifications-admin/src/lib/core/pages';

export const TENANT = 't1';

/** The API as the forms read it: nothing there yet, and the settings as a fresh tenant has them. */
export const emptyApi = {
  routes: () => of([]),
  integrations: () => of([]),
  providers: () => of([{ channel: 'webhook', kind: 'webhook', signs: true, registersCallbacks: false }]),
  mutes: () => of([]),
  allowlist: () => of([]),
  escalationPolicies: () => of([]),
  resolvers: () => of([]),
  settings: () => of({ stormThreshold: null, defaultStormThreshold: 60, requestRetentionDays: null, defaultRequestRetentionDays: 30,
    contentRetentionDays: null, defaultContentRetentionDays: 7, updatedAt: null }),
};

/** A page rendered for an administrator of the tenant, its inputs set, the API as `api` says (over {@link emptyApi}). */
export async function render<T>(page: Type<T>, api: Record<string, unknown> = {}, inputs: Record<string, unknown> = {}): Promise<ComponentFixture<T>> {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]), provideNoopAnimations(),
      { provide: NotificationsAdminApi, useValue: { ...emptyApi, ...api } },
      { provide: AccessService, useValue: { load: () => of(readAccess({ platformAdmin: false, allTenants: false, mode: 'embedded',
        pages: NOTIFICATIONS_ADMIN_PAGES.embedded, tenants: [{ id: TENANT, name: null, rights: ['read', 'operate', 'configure'] }] })) } },
    ],
  });
  const fixture = TestBed.createComponent(page);
  if (!('tenantId' in inputs)) fixture.componentRef.setInput('tenantId', TENANT);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.autoDetectChanges();
  await settle(fixture);
  return fixture;
}

/** Lets what is pending (reads, the focus timer) run, and the view catch up. */
export async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await fixture.whenStable();
  await new Promise(resolve => setTimeout(resolve, 5));
  fixture.detectChanges();
  await fixture.whenStable();
}

export const root = (f: ComponentFixture<unknown>) => f.nativeElement as HTMLElement;

/** The element with this data-testid. */
export function byId(f: ComponentFixture<unknown>, id: string): HTMLElement {
  const el = root(f).querySelector<HTMLElement>(`[data-testid="${id}"]`);
  if (!el) throw new Error(`No element with data-testid ${id}`);
  return el;
}

export const hasId = (f: ComponentFixture<unknown>, id: string) => !!root(f).querySelector(`[data-testid="${id}"]`);

/** The component's own (protected) members, for what a test sets or reads directly. */
export const inner = <T>(f: ComponentFixture<T>) => f.componentInstance as unknown as Record<string, any>;

export async function click(f: ComponentFixture<unknown>, id: string): Promise<void> {
  byId(f, id).click();
  await settle(f);
}

/** Types into an input (or textarea), as a user: the model follows, and the field's errors go. */
export async function type(f: ComponentFixture<unknown>, id: string, value: string): Promise<void> {
  const el = byId(f, id) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input', { bubbles: true }));
  await settle(f);
}

/** Submits the form with this data-testid, as Enter in a field would (a disabled Save button doesn't stop it). */
export async function submitForm(f: ComponentFixture<unknown>, id: string): Promise<void> {
  byId(f, id).dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  await settle(f);
}

/** Every field error the form shows: its text and pointer, and whether it sits inside its own `mat-form-field`. */
export function fieldErrors(f: ComponentFixture<unknown>) {
  return [...root(f).querySelectorAll<HTMLElement>('[data-testid="field-error"]')].map(e => ({
    text: (e.textContent ?? '').trim(), pointer: e.getAttribute('data-pointer') ?? '', inField: !!e.closest('mat-form-field'), tag: e.tagName.toLowerCase(),
  }));
}

export const formError = (f: ComponentFixture<unknown>) => root(f).querySelector<HTMLElement>('[data-testid="form-error"]');

/** A 400 as the API sends it: problem details with the fields it refused. */
export const refused = (errors: { pointer: string; message: string }[], extra: Record<string, unknown> = {}) =>
  ({ status: 400, error: { status: 400, title: 'Validation failed', detail: 'One or more fields were refused.', errors, ...extra } });
