import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { NotificationsAdminApi } from '../core/api.service';
import { AllowlistEntry } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, managedNote, rowAction, submit, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';

/**
 * The tenant's destination allowlist (design §13.1): the private hosts and networks its webhooks may reach, and on which ports. Public
 * hosts need no entry (https on 443 only); loopback, link-local and cloud metadata addresses are refused whatever is listed.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, FieldErrorsComponent, FormErrorsComponent],
  template: `
    <div class="na-head"><h1>Destination allowlist</h1><span class="na-spacer"></span>
      @if (!editing()) { <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!entries.hasValue()" data-testid="allow-new">Allow a destination</button> }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">Private hosts and networks webhooks may reach. Public hosts need no entry: they are reached over https on port 443.</p>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="allow-form">
        <div class="na-filters">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Host, address or network</mat-label>
            <input matInput name="destination" [(ngModel)]="draft.destination" [disabled]="!e.isNew" required placeholder="10.20.0.0/16" spellcheck="false"
              data-testid="allow-destination"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Ports</mat-label>
            <input matInput name="ports" [(ngModel)]="draft.ports" placeholder="443, 8443" data-testid="allow-ports">
            <mat-hint>Comma-separated. Empty: 443 only.</mat-hint></mat-form-field>
        </div>
        <na-field-errors [messages]="errors.at('/destination')" pointer="/destination" />
        <na-field-errors [messages]="errors.at('/ports')" pointer="/ports" />
        <na-form-errors [errors]="errors" [shown]="['/destination', '/ports']" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="allow-save">{{ e.isNew ? 'Allow' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (entries.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (entries.error()) { <p class="na-error" data-testid="allowlist-error">{{ error() }}</p> }
    <table class="na-table" data-testid="allowlist">
      <thead><tr><th>Destination</th><th>Ports</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (a of entries.value() ?? []; track a.destination) {
          <tr data-testid="allow-row" [attr.data-destination]="a.destination">
            <td class="na-mono">{{ a.destination }}@if (a.managedBy === 'config') { <div class="na-muted na-small">from configuration</div> }</td>
            <td>{{ a.ports.length ? a.ports.join(', ') : '443' }}</td>
            <td class="na-nowrap">{{ a.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              @if (a.managedBy === 'api') {
                <button mat-button type="button" (click)="edit(a)" data-testid="allow-edit">Edit</button>
                @if (confirming() === a.destination) {
                  <button mat-flat-button type="button" (click)="delete(a.destination)" [disabled]="busy()" data-testid="allow-delete-confirm">Remove</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button>
                } @else {
                  <button mat-button type="button" (click)="confirming.set(a.destination)" data-testid="allow-delete">Remove…</button>
                }
              } @else { <span class="na-muted na-small" [title]="note(a)">read only</span> }
            </td>
          </tr>
        } @empty { @if (!entries.isLoading()) { <tr><td colspan="4" class="na-muted">No private destinations are allowed.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="allow-row-error">{{ rowError() }}</p> }`,
})
export class AllowlistPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly entries = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.allowlist(params) });
  protected readonly error = computed(() => problemText(this.entries.error()));
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected draft = { destination: '', ports: '' };
  protected readonly note = (a: AllowlistEntry) => managedNote(a.managedBy);
  protected readonly reloadFn = () => this.reload();

  constructor() {
    // Reused for another tenant: what was open for the last one is closed.
    whenChanged(() => this.tenantId(), () => this.close());
  }

  private close(): void {
    this.editing.set(null);
    this.confirming.set(null);
    this.errors.clear();
    this.rowError.set('');
    this.draft = { destination: '', ports: '' };
  }

  protected reload(): void {
    this.close();
    this.entries.reload();
  }

  protected edit(a: AllowlistEntry | null): void {
    this.draft = a ? { destination: a.destination, ports: a.ports.join(', ') } : { destination: '', ports: '' };
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !a });
  }

  protected save(): void {
    const parts = this.draft.ports.split(/[\s,]+/).filter(Boolean);
    const ports = parts.map(Number);
    if (ports.some(p => !Number.isInteger(p))) {
      this.errors.local('/ports', 'Ports are whole numbers, comma-separated.');
      return;
    }
    // A PUT to a listed destination would replace its ports: a new entry can't name one (the API keeps an address as its /32 or /128).
    // Only a single address's own prefix is dropped: /32 for IPv4, /128 for IPv6 (an IPv6 /32 is a network). A spelling the API would
    // normalize differently (an expanded IPv6 address) isn't caught here; the API's PUT then changes that entry, as it always would.
    const wanted = this.draft.destination.trim().toLowerCase();
    const host = (d: string) => d.includes(':') ? d.replace(/\/128$/, '') : d.replace(/\/32$/, '');
    if (this.editing()?.isNew && !this.entries.hasValue()) {
      this.errors.local('/destination', "The allowlist couldn't be read, so this can't be checked as new: refresh first.");
      return;
    }
    if (this.editing()?.isNew && this.entries.value()?.some(a => a.destination === wanted || host(a.destination) === wanted)) {
      this.errors.local('/destination', `${wanted} is allowed already: edit it to change its ports.`);
      return;
    }
    submit(this.api.putAllowlistEntry(this.tenantId(), { destination: this.draft.destination.trim(), ports }), this.busy, this.errors, () => this.reload());
  }

  protected delete(destination: string): void {
    rowAction(this.api.deleteAllowlistEntry(this.tenantId(), destination),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.reload() });
  }
}
