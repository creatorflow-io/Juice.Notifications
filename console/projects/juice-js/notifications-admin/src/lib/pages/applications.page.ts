import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';
import { NotificationsAdminApi } from '../core/api.service';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, Pending, submit } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';

/** An application's key, as the API takes it: its types' namespace. */
export const APPLICATION_KEY_PATTERN = '[a-z][a-z0-9\\-]{1,62}';

/** The service's own application (contact verification): provisioned at start, never changed through the API. */
export const SERVICE_APPLICATION = 'notifications';

/**
 * The applications that send notifications (platform admins, design §5): each signs in as its OAuth client, may publish over RabbitMQ as its
 * broker user, and sends for the tenants linked to it. A tenant is administered here once an application is linked to it.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, FieldErrorsComponent,
    FormErrorsComponent],
  styles: `:host { display: block; padding: 16px; }`,
  template: `
    <div class="na-head"><h1>Applications</h1><span class="na-spacer"></span>
      @if (!adding()) { <button mat-flat-button type="button" (click)="startAdding()" [disabled]="!apps.hasValue()" data-testid="application-new">New application</button> }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">What sends notifications, and for which tenants. Its types are named under its key (<code>billing.invoice-paid</code> for
      <code>billing</code>); it signs in as its client id, and publishes over RabbitMQ as its broker user, if it has one.</p>
    @if (adding()) {
      <form class="na-form na-card" (ngSubmit)="add()" data-testid="application-form">
        <h2>New application</h2>
        <div class="na-filters">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" required [pattern]="keyPattern" spellcheck="false" data-testid="application-key">
            <mat-hint>Lowercase: its types' namespace. Fixed once added.</mat-hint></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
            <input matInput name="name" [(ngModel)]="draft.name" required maxlength="200" data-testid="application-name"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Client id</mat-label>
            <input matInput name="clientId" [(ngModel)]="draft.clientId" required spellcheck="false" data-testid="application-client-id">
            <mat-hint>The OAuth client it signs in as.</mat-hint></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Broker user</mat-label>
            <input matInput name="brokerUser" [(ngModel)]="draft.brokerUser" spellcheck="false" data-testid="application-broker-user">
            <mat-hint>Optional: the RabbitMQ user it publishes as.</mat-hint></mat-form-field>
        </div>
        @for (p of fields; track p) { <na-field-errors [messages]="errors.at(p)" [pointer]="p" /> }
        <na-form-errors [errors]="errors" [shown]="fields" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="application-save">Add</button>
          <button mat-button type="button" (click)="adding.set(false)">Cancel</button>
        </div>
      </form>
    }
    @if (apps.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (apps.error()) { <p class="na-error" data-testid="applications-error">{{ error() }}</p> }
    <table class="na-table" data-testid="applications">
      <thead><tr><th>Application</th><th>Name</th><th>Client id</th><th>Broker user</th><th>Tenants</th><th>State</th></tr></thead>
      <tbody>
        @for (a of apps.value() ?? []; track a.key) {
          <tr data-testid="application-row" [attr.data-key]="a.key">
            <td class="na-mono"><a [routerLink]="link(a.key)">{{ a.key }}</a>
              @if (a.key === serviceApp) { <div class="na-muted na-small">the service's own</div> }</td>
            <td>{{ a.name }}</td>
            <td class="na-mono na-small">{{ a.clientId }}</td>
            <td class="na-mono na-small">{{ a.brokerUser ?? '—' }}</td>
            <td>{{ a.tenants.length }}</td>
            <td>{{ a.enabled ? 'enabled' : 'disabled' }}</td>
          </tr>
        } @empty { @if (!apps.isLoading()) { <tr><td colspan="6" class="na-muted">No applications yet.</td></tr> } }
      </tbody>
    </table>`,
})
export class ApplicationsPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;

  protected readonly apps = apiResource({ params: () => true, stream: () => this.api.applications() });
  protected readonly error = computed(() => problemText(this.apps.error()));
  protected readonly keyPattern = APPLICATION_KEY_PATTERN;
  protected readonly serviceApp = SERVICE_APPLICATION;
  protected readonly fields = ['/key', '/name', '/clientId', '/brokerUser'];
  protected readonly errors = new FormErrors();
  protected readonly adding = signal(false);
  protected readonly busy = signal(false);
  protected readonly reloadFn = () => this.reload();
  protected draft = { key: '', name: '', clientId: '', brokerUser: '' };
  /** An add still running when the user leaves: its answer must not take them to the new application from wherever they went. */
  private readonly pending = new Pending();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.pending.cancel());
  }

  protected link(key: string): string {
    return under(this.base, `applications/${encodeURIComponent(key)}`);
  }

  protected reload(): void {
    this.errors.clear();
    this.apps.reload();
  }

  protected startAdding(): void {
    this.draft = { key: '', name: '', clientId: '', brokerUser: '' };
    this.errors.clear();
    this.adding.set(true);
  }

  protected add(): void {
    const d = { key: this.draft.key.trim(), name: this.draft.name.trim(), clientId: this.draft.clientId.trim(), brokerUser: this.draft.brokerUser.trim() };
    // The API names no field in its refusals of these: each is said on its own field first.
    if (!new RegExp(`^${APPLICATION_KEY_PATTERN}$`).test(d.key)) {
      this.errors.local('/key', 'The key is 2–63 lowercase letters, digits or hyphens, starting with a letter.');
      return;
    }
    if (this.apps.value()?.some(a => a.key === d.key)) {
      this.errors.local('/key', `There is an application ${d.key} already.`);
      return;
    }
    if (!d.name) {
      this.errors.local('/name', 'Give the application a name.');
      return;
    }
    if (!/^[\x21-\x7E]{1,200}$/.test(d.clientId)) {
      this.errors.local('/clientId', 'The client id is 1–200 printable characters without spaces.');
      return;
    }
    if (d.brokerUser && !/^[\x21-\x7E]{1,200}$/.test(d.brokerUser)) {
      this.errors.local('/brokerUser', 'The broker user is 1–200 printable characters without spaces; empty for none.');
      return;
    }
    this.pending.track(submit(this.api.createApplication({ ...d, brokerUser: d.brokerUser || null }), this.busy, this.errors, a => {
      this.adding.set(false);
      void this.router.navigateByUrl(this.link(a.key));
    }));
  }
}
