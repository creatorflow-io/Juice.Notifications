import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AccessService, sameId } from '../core/access.service';
import { NotificationsAdminApi } from '../core/api.service';
import { Application } from '../core/models';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, Pending, rowAction, submit, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';
import { SERVICE_APPLICATION } from './applications.page';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function blank() {
  return { name: '', clientId: '', brokerUser: '', enabled: true };
}

/**
 * One application (platform admins): its name, client id, broker user and whether it may send; and the tenants it sends for. Linking a
 * tenant lets the application send for it, and makes the tenant administrable here; a link stays once the application has sent for the
 * tenant (disable the application instead). The service's own application is provisioned at start and only shown.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    FieldErrorsComponent, FormErrorsComponent],
  styles: `:host { display: block; padding: 16px; }`,
  template: `
    <div class="na-head">
      <a [routerLink]="listLink">Applications</a><span class="na-muted">/</span>
      <h1 data-testid="application-title">{{ app.value()?.name ?? key() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button>
    </div>
    @if (app.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (app.error()) { <p class="na-error" data-testid="application-error">{{ error() }}</p> }
    @if (app.value(); as a) {
      @if (a.key === serviceApp) {
        <p class="na-notice" data-testid="application-service-owned">The service's own application: it sends the contact verifications, is
          provisioned at start and can't be changed here.</p>
      }
      <div class="na-grid-2">
        <section class="na-card">
          <h2>Application</h2>
          <table class="na-kv" data-testid="application-facts"><tbody>
            <tr><th>Key</th><td class="na-mono">{{ a.key }}</td></tr>
            <tr><th>State</th><td>{{ a.enabled ? 'enabled' : 'disabled: its new requests are refused' }}</td></tr>
            <tr><th>Added</th><td>{{ a.createdAt | date: 'MMM d, y, HH:mm' }}</td></tr>
            <tr><th>Changed</th><td>{{ a.updatedAt | date: 'MMM d, y, HH:mm' }}</td></tr>
            <tr><th>Id</th><td class="na-mono na-small">{{ a.id }}</td></tr>
          </tbody></table>
        </section>
        @if (a.key !== serviceApp) {
          <form class="na-card na-form" (ngSubmit)="save(a)" (input)="saved.set(false)" data-testid="application-form">
            <h2>Details</h2>
            <div class="na-filters">
              <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
                <input matInput name="name" [(ngModel)]="draft.name" required maxlength="200" data-testid="application-name"></mat-form-field>
              <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Client id</mat-label>
                <input matInput name="clientId" [(ngModel)]="draft.clientId" required spellcheck="false" data-testid="application-client-id"></mat-form-field>
              <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Broker user</mat-label>
                <input matInput name="brokerUser" [(ngModel)]="draft.brokerUser" spellcheck="false" data-testid="application-broker-user">
                <mat-hint>Empty: it sends over HTTP only.</mat-hint></mat-form-field>
              <mat-checkbox name="enabled" [(ngModel)]="draft.enabled" (change)="saved.set(false)" data-testid="application-enabled">Enabled</mat-checkbox>
            </div>
            @for (p of fields; track p) { <na-field-errors [messages]="errors.at(p)" [pointer]="p" /> }
            <na-form-errors [errors]="errors" [shown]="fields" [reload]="reloadFn" />
            <div class="na-actions">
              <button mat-flat-button type="submit" [disabled]="busy()" data-testid="application-save">Save</button>
              @if (saved()) { <span role="status" data-testid="application-saved">Saved.</span> }
            </div>
          </form>
        }
      </div>

      <section class="na-card" data-testid="application-tenants">
        <h2>Tenants</h2>
        <p class="na-hint">The application sends for these; each is administered here once linked. A link stays once the application has sent for
          the tenant: disable the application to stop it.</p>
        @if (a.key !== serviceApp) {
          <form class="na-filters" (ngSubmit)="link(a)" data-testid="tenant-link-form">
            <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Tenant id</mat-label>
              <input matInput name="tenant" [(ngModel)]="tenantDraft" spellcheck="false" list="na-known-tenants" placeholder="0193f0aa-…"
                data-testid="tenant-link-id">
              <mat-hint>From the tenants directory; a tenant still being set up can be linked too.</mat-hint></mat-form-field>
            <datalist id="na-known-tenants">
              @for (t of unlinked(); track t.id) { <option [value]="t.id">{{ t.name ?? t.id }}</option> }
            </datalist>
            <button mat-flat-button type="submit" [disabled]="busy() || !tenantDraft.trim()" data-testid="tenant-link">Link</button>
          </form>
          <na-field-errors [messages]="linkErrors.at('/tenantId')" pointer="/tenantId" />
          <na-form-errors [errors]="linkErrors" [shown]="['/tenantId']" [reload]="reloadFn" />
        }
        <table class="na-table">
          <thead><tr><th>Tenant</th><th>Id</th><th></th></tr></thead>
          <tbody>
            @for (t of a.tenants; track t) {
              <tr data-testid="tenant-row" [attr.data-tenant]="t">
                <td>{{ tenantName(t) ?? '—' }}</td>
                <td class="na-mono na-small"><a [routerLink]="tenantLink(t)">{{ t }}</a></td>
                <td class="na-nowrap">
                  @if (a.key !== serviceApp) {
                    @if (confirming() === t) {
                      <span class="na-confirm">{{ a.name }} can no longer send for it.
                        <button mat-flat-button type="button" (click)="unlink(a, t)" [disabled]="busy()" data-testid="tenant-unlink-confirm">Unlink</button>
                        <button mat-button type="button" (click)="confirming.set(null)">Keep</button></span>
                    } @else {
                      <button mat-button type="button" (click)="confirming.set(t)" data-testid="tenant-unlink">Unlink…</button>
                    }
                  }
                </td>
              </tr>
            } @empty { <tr><td colspan="3" class="na-muted">None yet: it can't send for any tenant.</td></tr> }
          </tbody>
        </table>
        @if (rowError()) { <p class="na-error" data-testid="tenant-row-error">{{ rowError() }}</p> }
      </section>
    }`,
})
export class ApplicationPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly access = inject(AccessService);
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  readonly key = input.required<string>();

  protected readonly app = apiResource({ params: () => this.key(), stream: ({ params }) => this.api.application(params) });
  /** The tenants the user may administer, for their names; read again after a link or unlink changes them. */
  private readonly accessRead = signal(0);
  private readonly known = apiResource({ params: () => this.accessRead(), stream: () => this.access.load() });
  protected readonly error = computed(() => problemText(this.app.error()));
  protected readonly unlinked = computed(() => (this.known.value()?.tenants ?? []).filter(t => !this.app.value()?.tenants.some(l => sameId(l, t.id))));
  protected readonly serviceApp = SERVICE_APPLICATION;
  protected readonly listLink = under(this.base, 'applications');
  protected readonly fields = ['/name', '/clientId', '/brokerUser'];
  protected readonly errors = new FormErrors();
  protected readonly linkErrors = new FormErrors();
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected readonly reloadFn = () => this.reload();
  protected draft = blank();
  protected tenantDraft = '';
  /** The details as last read: a field still showing them takes what is read next; one typed over keeps the typing. */
  private loaded = blank();
  private readonly pending = new Pending();

  constructor() {
    // Another application (the page reused): nothing typed, armed or sent for the last one carries over.
    whenChanged(() => this.key(), () => {
      this.pending.cancel();
      this.busy.set(false);
      this.errors.clear();
      this.linkErrors.clear();
      this.saved.set(false);
      this.confirming.set(null);
      this.rowError.set('');
      this.draft = blank();
      this.loaded = blank();
      this.tenantDraft = '';
    });
    whenChanged(() => this.app.value(), () => {
      const a = this.app.value();
      if (!a) return;
      const read = { name: a.name, clientId: a.clientId, brokerUser: a.brokerUser ?? '', enabled: a.enabled };
      const next = { ...this.draft };
      for (const k of ['name', 'clientId', 'brokerUser', 'enabled'] as const) if (this.draft[k] === this.loaded[k]) (next as Record<string, unknown>)[k] = read[k];
      this.draft = next;
      this.loaded = read;
    });
  }

  protected tenantName(id: string): string | null {
    return this.known.value()?.tenants.find(t => sameId(t.id, id))?.name ?? null;
  }

  protected tenantLink(id: string): string {
    return under(this.base, `tenants/${id}`);
  }

  protected reload(): void {
    this.errors.clear();
    this.linkErrors.clear();
    this.saved.set(false);
    this.confirming.set(null);
    this.rowError.set('');
    this.app.reload();
  }

  /** The tenants changed: what the user may administer (the switcher, the names) is read afresh. */
  private tenantsChanged(): void {
    this.access.refresh();
    this.accessRead.update(n => n + 1);
    this.app.reload();
  }

  /** Only what changed is sent: the API changes the fields given. An emptied broker user is sent as "" (removed). */
  protected save(a: Application): void {
    this.saved.set(false);
    const name = this.draft.name.trim();
    const clientId = this.draft.clientId.trim();
    const brokerUser = this.draft.brokerUser.trim();
    if (!name) {
      this.errors.local('/name', 'Give the application a name.');
      return;
    }
    if (!/^[\x21-\x7E]{1,200}$/.test(clientId)) {
      this.errors.local('/clientId', 'The client id is 1–200 printable characters without spaces.');
      return;
    }
    if (brokerUser && !/^[\x21-\x7E]{1,200}$/.test(brokerUser)) {
      this.errors.local('/brokerUser', 'The broker user is 1–200 printable characters without spaces; empty for none.');
      return;
    }
    const body: { name?: string; clientId?: string; enabled?: boolean; brokerUser?: string } = {};
    if (name !== a.name) body.name = name;
    if (clientId !== a.clientId) body.clientId = clientId;
    if (brokerUser !== (a.brokerUser ?? '')) body.brokerUser = brokerUser;
    if (this.draft.enabled !== a.enabled) body.enabled = this.draft.enabled;
    this.pending.track(submit(this.api.updateApplication(a.key, body), this.busy, this.errors, () => {
      this.saved.set(true);
      this.app.reload();
    }));
  }

  protected link(a: Application): void {
    // A refused unlink's sentence, or a row armed, is about the table as it was: not the link now being made.
    this.rowError.set('');
    this.confirming.set(null);
    const id = this.tenantDraft.trim().toLowerCase();
    if (!GUID.test(id) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(id)) {
      this.linkErrors.local('/tenantId', "A tenant's id is a GUID such as 0193f0aa-0000-7000-8000-0000000000a1.");
      return;
    }
    if (a.tenants.some(t => sameId(t, id))) {
      this.linkErrors.local('/tenantId', 'The application sends for this tenant already.');
      return;
    }
    this.pending.track(submit(this.api.linkTenant(a.key, id), this.busy, this.linkErrors, () => {
      this.tenantDraft = '';
      this.tenantsChanged();
    }));
  }

  protected unlink(a: Application, tenantId: string): void {
    this.pending.track(rowAction(this.api.unlinkTenant(a.key, tenantId),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.tenantsChanged() }));
  }
}
