import { DatePipe, JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { NotificationsAdminApi } from '../core/api.service';
import { Integration, Target } from '../core/models';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, KEY_PATTERN, KIND_HINTS, SECRET_REF_HINT, managedNote, rowAction, submit, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText } from '../shared/ui';
import { IntegrationForm } from './integration-form';

/**
 * One integration (phase-4c-plan §4): its settings, secret reference and priority, edited in place; its targets (the shared destinations
 * routes name), each by address or by a reference to a secret one; a provider told where its callbacks go when it is one that asks. Rows
 * from the deployment's configuration and the inbox (the service's own) are shown, not edited.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, JsonPipe, FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, Badge,
    TenantLinkPipe, FieldErrorsComponent, FormErrorsComponent, IntegrationForm],
  template: `
    <div class="na-head">
      <a [routerLink]="['integrations'] | naTenant: tenantId()">Integrations</a><span class="na-muted">/</span>
      <h1 data-testid="integration-title">{{ key() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="reload()">Refresh</button>
    </div>
    @if (integration.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (integration.error() || providers.error()) { <p class="na-error" data-testid="integration-error">{{ error() }}</p> }
    @if (loaded() && !integration.value()) { <p class="na-muted" data-testid="integration-missing">There is no integration {{ key() }} in this tenant.</p> }
    @if (integration.value(); as i) {
      <section class="na-card">
        <div class="na-actions"><na-badge [state]="i.enabled ? 'enabled' : 'disabled'" data-testid="integration-state" />
          <span>{{ i.channel }} · {{ i.kind }} · priority {{ i.priority }}</span>
          <span class="na-muted na-small">updated {{ i.updatedAt | date: 'MMM d, y, HH:mm' }}</span></div>
        @if (managed()) {
          <p class="na-notice" data-testid="integration-managed">{{ managed() }}</p>
          <table class="na-kv"><tbody>
            <tr><th>Settings</th><td><pre class="na-mono">{{ i.settings | json }}</pre></td></tr>
            <tr><th>Secret reference</th><td class="na-mono">{{ i.secretRef ?? '—' }}</td></tr>
          </tbody></table>
        } @else if (providers.value(); as available) {
          <na-integration-form [tenantId]="tenantId()" [providers]="available" [existing]="i" [reloadFn]="reloadFn" (saved)="reload()" />
          <div class="na-actions">
            @if (provider()?.registersCallbacks) {
              <button mat-stroked-button type="button" (click)="registerCallbacks()" [disabled]="busy()" data-testid="integration-callbacks">
                Register callbacks</button>
            }
            @if (!confirmingDelete()) {
              <button mat-button type="button" (click)="confirmingDelete.set(true)" data-testid="integration-delete">Delete…</button>
            } @else {
              <span class="na-confirm">Delete {{ i.key }} and its {{ i.targets.length }} target(s)?
                <button mat-flat-button type="button" (click)="delete()" [disabled]="busy()" data-testid="integration-delete-confirm">Delete</button>
                <button mat-button type="button" (click)="confirmingDelete.set(false)">Keep it</button></span>
            }
          </div>
          @if (actionText()) { <p data-testid="integration-action-result">{{ actionText() }}</p> }
          @if (actionError()) { <p class="na-error" data-testid="integration-action-error">{{ actionError() }}</p> }
        }
      </section>

      @if (i.managedBy !== 'service') {
        <section class="na-card">
          <div class="na-head"><h2>Targets</h2><span class="na-spacer"></span>
            @if (!managed() && !editing()) { <button mat-stroked-button type="button" (click)="edit(null)" data-testid="target-new">Add a target</button> }</div>
          <p class="na-hint">Shared destinations routes name. {{ targetHint() }}</p>
          @if (editing(); as e) {
            <form class="na-form na-card" (ngSubmit)="saveTarget()" data-testid="target-form">
              <div class="na-filters">
                <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
                  <input matInput name="targetKey" [(ngModel)]="target.key" [disabled]="!e.isNew" required [pattern]="keyPattern" data-testid="target-key"></mat-form-field>
                <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Address</mat-label>
                  <input matInput name="address" [(ngModel)]="target.address" spellcheck="false" data-testid="target-address"></mat-form-field>
                <mat-checkbox name="targetEnabled" [(ngModel)]="target.enabled" data-testid="target-enabled">Enabled</mat-checkbox>
              </div>
              <na-field-errors [messages]="targetErrors.at('/key')" pointer="/key" />
              <na-field-errors [messages]="targetErrors.at('/address')" pointer="/address" />
              <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Or an address reference</mat-label>
                <input matInput name="addressRef" [(ngModel)]="target.addressRef" autocomplete="off" spellcheck="false" data-testid="target-address-ref"></mat-form-field>
              <p class="na-hint">For an address that is itself a secret (a webhook URL with a token in it). {{ secretRefHint }}</p>
              <na-field-errors [messages]="targetErrors.at('/addressRef')" pointer="/addressRef" />
              <na-form-errors [errors]="targetErrors" [shown]="['/key', '/address', '/addressRef']" [reload]="reloadFn" />
              <div class="na-actions">
                <button mat-flat-button type="submit" [disabled]="busy()" data-testid="target-save">{{ e.isNew ? 'Add' : 'Save' }}</button>
                <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
              </div>
            </form>
          }
          <table class="na-table" data-testid="targets">
            <thead><tr><th>Target</th><th>Address</th><th>State</th><th>Updated</th><th></th></tr></thead>
            <tbody>
              @for (t of i.targets; track t.key) {
                <tr data-testid="target-row" [attr.data-key]="t.key" [class.na-off]="!t.enabled">
                  <td>{{ t.key }}@if (t.managedBy === 'config') { <div class="na-muted na-small">from configuration</div> }</td>
                  <td class="na-mono">@if (t.addressRef) { <span title="A reference: the address is a secret">{{ t.addressRef }}</span> } @else { {{ t.address }} }</td>
                  <td><na-badge [state]="t.enabled ? 'enabled' : 'disabled'" /></td>
                  <td class="na-nowrap">{{ t.updatedAt | date: 'MMM d, HH:mm' }}</td>
                  <td class="na-nowrap">
                    @if (!managed() && t.managedBy === 'api') {
                      <button mat-button type="button" (click)="edit(t)" data-testid="target-edit">Edit</button>
                      @if (confirmingTarget() === t.key) {
                        <button mat-flat-button type="button" (click)="deleteTarget(t.key)" [disabled]="busy()" data-testid="target-delete-confirm">Delete</button>
                        <button mat-button type="button" (click)="confirmingTarget.set(null)">Keep</button>
                      } @else {
                        <button mat-button type="button" (click)="confirmingTarget.set(t.key)" data-testid="target-delete">Delete…</button>
                      }
                    }
                  </td>
                </tr>
              } @empty { <tr><td colspan="5" class="na-muted">No targets.</td></tr> }
            </tbody>
          </table>
          @if (targetError()) { <p class="na-error" data-testid="target-error">{{ targetError() }}</p> }
        </section>
      }
    }`,
})
export class IntegrationPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  readonly tenantId = input.required<string>();
  readonly key = input.required<string>();

  // The API has no read of one integration: the tenant's list, and this one in it (null when it isn't there).
  protected readonly integration = apiResource({ params: () => ({ tenantId: this.tenantId(), key: this.key() }),
    stream: ({ params }) => this.api.integrations(params.tenantId).pipe(map(list => list.find(i => i.key === params.key) ?? null)) });
  protected readonly providers = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.providers(params) });
  protected readonly loaded = computed(() => this.integration.hasValue() && !this.integration.isLoading());
  protected readonly provider = computed(() => {
    const i = this.integration.value();
    return i ? this.providers.value()?.find(p => p.channel === i.channel && p.kind === i.kind) ?? null : null;
  });
  protected readonly managed = computed(() => managedNote(this.integration.value()?.managedBy));
  protected readonly targetHint = computed(() => KIND_HINTS[this.integration.value()?.kind ?? '']?.target ?? '');
  protected readonly error = computed(() => problemText(this.integration.error() ?? this.providers.error()));
  protected readonly keyPattern = KEY_PATTERN;
  protected readonly secretRefHint = SECRET_REF_HINT;
  protected readonly reloadFn = () => this.reload();

  protected readonly busy = signal(false);
  protected readonly confirmingDelete = signal(false);
  protected readonly actionText = signal('');
  protected readonly actionError = signal('');

  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected target = { key: '', address: '', addressRef: '', enabled: true };
  protected readonly targetErrors = new FormErrors();
  protected readonly confirmingTarget = signal<string | null>(null);
  protected readonly targetError = signal('');

  constructor() {
    // Reused for another integration (or tenant): a target form or a confirmation armed for the last one is closed.
    whenChanged(() => [this.tenantId(), this.key()], () => {
      this.close();
      this.actionText.set('');
      this.actionError.set('');
    });
  }

  private close(): void {
    this.editing.set(null);
    this.confirmingDelete.set(false);
    this.confirmingTarget.set(null);
    this.targetErrors.clear();
    this.targetError.set('');
  }

  protected reload(): void {
    this.close();
    this.integration.reload();
    // The form needs the providers: a read that failed is asked again.
    if (this.providers.error()) this.providers.reload();
  }

  protected registerCallbacks(): void {
    this.run(this.api.registerCallbacks(this.tenantId(), this.key()), () => this.actionText.set('The provider now sends its callbacks here.'));
  }

  protected delete(): void {
    this.run(this.api.deleteIntegration(this.tenantId(), this.key()),
      () => void this.router.navigate([under(this.base, `tenants/${this.tenantId()}`), 'integrations']));
  }

  private run(call: ReturnType<NotificationsAdminApi['registerCallbacks']>, done: () => void): void {
    this.actionText.set('');
    rowAction(call, { busy: this.busy, error: this.actionError, disarm: () => this.confirmingDelete.set(false), done });
  }

  protected edit(t: Target | null): void {
    this.target = t ? { key: t.key, address: t.address ?? '', addressRef: t.addressRef ?? '', enabled: t.enabled }
      : { key: '', address: '', addressRef: '', enabled: true };
    this.targetErrors.clear();
    this.editing.set({ isNew: !t });
  }

  protected saveTarget(): void {
    const key = this.target.key.trim();
    const isNew = this.editing()?.isNew;
    if (!key) {
      // Sent empty, the PUT would go to the targets collection, which takes none.
      this.targetErrors.local('/key', 'Give the target a key.');
      return;
    }
    if (isNew && this.integration.value()?.targets.some(t => t.key === key)) {
      this.targetErrors.local('/key', `This integration has a target ${key} already: edit it instead.`);
      return;
    }
    const address = this.target.address.trim();
    const addressRef = this.target.addressRef.trim();
    // Neither sent keeps the stored address (the API's PUT keeps what isn't named): both emptied, the save would change nothing there.
    if (!isNew && !address && !addressRef) {
      this.targetErrors.local('/address', 'A target keeps an address: give one, or an address reference, to replace it.');
      return;
    }
    submit(this.api.putTarget(this.tenantId(), this.key(), key, { address: address || null, addressRef: addressRef || null, enabled: this.target.enabled }),
      this.busy, this.targetErrors, () => this.reload());
  }

  protected deleteTarget(key: string): void {
    rowAction(this.api.deleteTarget(this.tenantId(), this.key(), key),
      { busy: this.busy, error: this.targetError, disarm: () => this.confirmingTarget.set(null), done: () => this.reload() });
  }
}
