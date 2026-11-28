import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { Route } from '../core/models';
import {
  FormErrors, FormErrorsComponent, MatchDraft, MatchEditorComponent, NaFieldError, isBlankNumber, keyIssue, managedNote, matchDraft, matchIssues,
  matchOf, matchText, rowAction, submit, validateAll, whenChanged,
} from '../shared/forms';
import { apiResource } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText } from '../shared/ui';
import { tenantRights } from '../shared/rights';

/**
 * The tenant's routes (design §6.3): which notifications (by type, labels and priority) reach which targets. A route is edited above the
 * list; one from the deployment's configuration is shown only.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    MatSelectModule, Badge, TenantLinkPipe, NaFieldError, FormErrorsComponent, MatchEditorComponent],
  template: `
    <div class="na-head"><h1>Routes</h1><span class="na-spacer"></span>
      @if (!editing()) { @if (can.configure()) { <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!routes.hasValue()" data-testid="route-new">New route</button> } }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="route-form">
        <h2>{{ e.isNew ? 'New route' : 'Route ' + draft.key }}</h2>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!e.isNew" required data-testid="route-key" [naError]="errors" naPointer="/key">
            @for (e of errors.of('/key'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Digest window</mat-label>
            <input matInput type="number" name="digestSeconds" [(ngModel)]="draft.digestSeconds" min="0" max="3600" step="1" data-testid="route-digest"
              [naError]="errors" naPointer="/digestSeconds">
            <mat-hint>Seconds, 0 to 3600. 0 sends each at once; more sends what reaches the route within the window as one digest per type.</mat-hint>
            @for (e of errors.of('/digestSeconds'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
          <mat-checkbox name="enabled" [(ngModel)]="draft.enabled" data-testid="route-enabled">Enabled</mat-checkbox>
        </div>
        <p class="na-hint">Matches: what the route takes. Leave everything empty for every notification.</p>
        <na-match-editor [draft]="draft.match" [errors]="errors" />
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Targets</mat-label>
            <mat-select name="targets" [(ngModel)]="draft.targets" multiple required data-testid="route-targets" [naError]="errors" naPointer="/targets">
              @for (t of targets(); track t.key) {
                <mat-option [value]="t.key">{{ t.key }} <span class="na-muted">({{ t.integration }}, {{ t.channel }}{{ t.enabled ? '' : ', disabled' }})</span></mat-option>
              }
              <!-- A target the route names that no integration lists (or that couldn't be read): an option still, so toggling another keeps it. -->
              @for (k of unlisted(); track k) { <mat-option [value]="k">{{ k }} <span class="na-muted">(not found)</span></mat-option> }
            </mat-select>
            @for (e of errors.of('/targets'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
        </div>
        @if (integrations.error()) {
          <p class="na-error" data-testid="route-targets-error">The targets couldn't be read: {{ targetsError() }}</p>
        } @else if (!targets().length && !integrations.isLoading()) {
          <p class="na-hint">No targets yet: add one to an <a [routerLink]="['integrations'] | naTenant: tenantId()">integration</a> first.</p>
        }
        <na-form-errors [errors]="errors" [shown]="shown" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="route-save">{{ e.isNew ? 'Create' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (routes.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (routes.error()) { <p class="na-error" data-testid="routes-error">{{ error() }}</p> }
    <table class="na-table" data-testid="routes">
      <thead><tr><th>Route</th><th>Matches</th><th>Targets</th><th>Digest</th><th>State</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (r of routes.value() ?? []; track r.key) {
          <tr data-testid="route-row" [attr.data-key]="r.key" [class.na-off]="!r.enabled">
            <td>{{ r.key }}@if (r.managedBy === 'config') { <div class="na-muted na-small">from configuration</div> }</td>
            <td class="na-small">{{ text(r) }}</td>
            <td>{{ r.targets.join(', ') }}</td>
            <td class="na-nowrap" data-testid="route-digest-shown">@if (r.digestSeconds) { {{ r.digestSeconds }} s } @else { at once }</td>
            <td><na-badge [state]="r.enabled ? 'enabled' : 'disabled'" /></td>
            <td class="na-nowrap">{{ r.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              @if (r.managedBy === 'api') {
                @if (can.configure()) { <button mat-button type="button" (click)="edit(r)" data-testid="route-edit">Edit</button> }
                @if (confirming() === r.key) {
                  <button mat-flat-button type="button" (click)="delete(r.key)" [disabled]="busy()" data-testid="route-delete-confirm">Delete</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button>
                } @else {
                  @if (can.configure()) { <button mat-button type="button" (click)="confirming.set(r.key)" data-testid="route-delete">Delete…</button> }
                }
              } @else { <span class="na-muted na-small" [title]="note(r)">read only</span> }
            </td>
          </tr>
        } @empty { @if (!routes.isLoading()) { <tr><td colspan="7" class="na-muted">No routes yet.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="route-row-error">{{ rowError() }}</p> }`,
})
export class RoutesPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  /** The user's rights here: controls they may not use are hidden (the API refuses them anyway). */
  protected readonly can = tenantRights(() => this.tenantId());

  protected readonly routes = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.routes(params) });
  protected readonly integrations = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.integrations(params) });
  /** Every target of the tenant's integrations (keys are the tenant's, not the integration's). */
  protected readonly targets = computed(() => (this.integrations.value() ?? []).flatMap(i =>
    i.targets.map(t => ({ key: t.key, integration: i.key, channel: i.channel, enabled: t.enabled && i.enabled })))
    .sort((a, b) => a.key.localeCompare(b.key)));
  protected readonly error = computed(() => problemText(this.routes.error()));
  protected readonly targetsError = computed(() => problemText(this.integrations.error()));
  /** The draft's targets no option lists. */
  protected unlisted(): string[] {
    const listed = new Set(this.targets().map(t => t.key));
    return this.draft.targets.filter(k => !listed.has(k));
  }
  protected readonly shown = ['/key', '/match', '/targets', '/digestSeconds'];
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected draft: { key: string; match: MatchDraft; targets: string[]; enabled: boolean; digestSeconds: number } = this.blank();
  protected readonly text = (r: Route) => matchText(r.match);
  protected readonly note = (r: Route) => managedNote(r.managedBy);
  protected readonly reloadFn = () => this.reload();

  constructor() {
    // Reused for another tenant: what was open for the last one is closed.
    whenChanged(() => this.tenantId(), () => this.close());
  }

  private blank() {
    return { key: '', match: matchDraft({}), targets: [] as string[], enabled: true, digestSeconds: 0 };
  }

  private close(): void {
    this.editing.set(null);
    this.confirming.set(null);
    this.errors.clear();
    this.rowError.set('');
    this.draft = this.blank();
  }

  protected reload(): void {
    this.close();
    this.routes.reload();
    this.integrations.reload();
  }

  protected edit(r: Route | null): void {
    this.draft = r ? { key: r.key, match: matchDraft(r.match), targets: [...r.targets], enabled: r.enabled, digestSeconds: r.digestSeconds ?? 0 } : this.blank();
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !r });
  }

  protected save(): void {
    const key = this.draft.key.trim();
    const isNew = !!this.editing()?.isNew;
    const digest = Number(this.draft.digestSeconds);
    const issues = validateAll([
      // Sent empty, the PUT would go to the routes collection, which takes none.
      () => isNew ? keyIssue(key, 'route') : null,
      // A PUT creates or replaces: a new route is checked against the list, which must have been read.
      () => isNew && key && !this.routes.hasValue()
        ? { pointer: '/key', message: "Key: the routes couldn't be read, so this key can't be checked as new; refresh first." } : null,
      () => isNew && key && this.routes.value()?.some(r => r.key === key)
        ? { pointer: '/key', message: 'Key: there is a route with this key already; edit it instead.' } : null,
      () => isBlankNumber(this.draft.digestSeconds) || !Number.isInteger(digest) || digest < 0 || digest > 3600
        ? { pointer: '/digestSeconds', message: 'Digest window: a whole number of seconds, 0 to 3600.' } : null,
      () => matchIssues(this.draft.match),
      () => this.draft.targets.length ? null : { pointer: '/targets', message: 'Targets: choose at least one.' },
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    submit(this.api.putRoute(this.tenantId(), key, { match: matchOf(this.draft.match), targets: this.draft.targets, enabled: this.draft.enabled,
      digestSeconds: digest }),
      this.busy, this.errors, () => this.reload());
  }

  protected delete(key: string): void {
    rowAction(this.api.deleteRoute(this.tenantId(), key),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.reload() });
  }
}
