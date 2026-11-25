import { DatePipe, JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { NotificationsAdminApi } from '../core/api.service';
import { Resolver } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, KEY_PATTERN, Pending, SECRET_REF_HINT, managedNote, rowAction, submit, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';

/** What each kind's `settings` hold, as a new resolver's starting point. */
const RESOLVER_SETTINGS: Record<string, Record<string, unknown>> = { identity: { directory: '' }, local: {}, topic: {} };

/** What each kind reads, for the form's hint. */
const KIND_HINTS: Record<string, string> = {
  identity: "People, groups and roles from one of the deployment's identity directories (settings.directory names it).",
  local: 'Local users and local groups, kept on the subscribers and groups pages.',
  topic: "Topics' subscribers, kept on the topics page.",
};

/**
 * The tenant's recipient resolvers (design §6.1, D20): a request names people (`user`, `group`, `role`, `topic`) through one of these by
 * its key, and the resolver says who they are. Each is one of the deployment's kinds and handles some of the recipient kinds that kind can.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, JsonPipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    MatSelectModule, FieldErrorsComponent, FormErrorsComponent],
  template: `
    <div class="na-head"><h1>Resolvers</h1><span class="na-spacer"></span>
      @if (!editing()) {
        <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!resolvers.hasValue() || !kinds.hasValue()" data-testid="resolver-new">New resolver</button>
      }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">A request names people through one of these: <code>{{ example }}</code>. Routes and addresses need none.</p>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="resolver-form">
        <h2>{{ e.isNew ? 'New resolver' : 'Resolver ' + draft.key }}</h2>
        <!-- Made afresh on each edit: ngModel writes nothing to a select whose model value is unchanged. -->
        @for (g of [generation()]; track g) {
        <div class="na-filters">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!e.isNew" required [pattern]="keyPattern" data-testid="resolver-key">
            <mat-hint>Requests name it.</mat-hint></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Kind</mat-label>
            <mat-select name="kind" [(ngModel)]="draft.kind" (ngModelChange)="kindChanged()" [disabled]="!e.isNew" required data-testid="resolver-kind">
              @for (k of kindOptions(); track k.kind) {
                <mat-option [value]="k.kind">{{ k.kind }}@if (k.unlisted) { <span class="na-muted"> (not in this deployment)</span> }</mat-option>
              }
            </mat-select>
            @if (!e.isNew) { <mat-hint>Fixed: another kind is another resolver.</mat-hint> }</mat-form-field>
          <mat-checkbox name="enabled" [(ngModel)]="draft.enabled" data-testid="resolver-enabled">Enabled</mat-checkbox>
        </div>
        }
        @if (kindHint()) { <p class="na-hint">{{ kindHint() }}</p> }
        <na-field-errors [messages]="errors.at('/key')" pointer="/key" />
        <na-field-errors [messages]="errors.at('/kind')" pointer="/kind" />
        <div class="na-filters" data-testid="resolver-handles">
          <span class="na-small">Handles</span>
          @for (h of handleOptions(); track h) {
            <mat-checkbox [name]="'handles-' + h" [ngModel]="draft.handles.includes(h)" (ngModelChange)="toggle(h, $event)" [attr.data-handle]="h">{{ h }}</mat-checkbox>
          } @empty { <span class="na-muted na-small">Pick a kind.</span> }
        </div>
        <na-field-errors [messages]="errors.at('/handles')" pointer="/handles" />
        <mat-form-field subscriptSizing="dynamic"><mat-label>Settings (JSON)</mat-label>
          <textarea matInput name="settings" [(ngModel)]="draft.settings" rows="4" spellcheck="false" data-testid="resolver-settings"></textarea>
        </mat-form-field>
        <na-field-errors [messages]="errors.at('/settings')" pointer="/settings" />
        <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Secret reference</mat-label>
          <input matInput name="secretRef" [(ngModel)]="draft.secretRef" autocomplete="off" spellcheck="false" data-testid="resolver-secret-ref"></mat-form-field>
        <p class="na-hint">Only if the kind needs one. {{ secretHint }}</p>
        <na-field-errors [messages]="errors.at('/secretRef')" pointer="/secretRef" />
        <na-form-errors [errors]="errors" [shown]="shown" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="resolver-save">{{ e.isNew ? 'Create' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (resolvers.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (resolvers.error()) { <p class="na-error" data-testid="resolvers-error">{{ error() }}</p> }
    @if (kinds.error()) { <p class="na-error" data-testid="resolver-kinds-error">The deployment's resolver kinds couldn't be read, so none can be added: {{ kindsError() }}</p> }
    <table class="na-table" data-testid="resolvers">
      <thead><tr><th>Resolver</th><th>Kind</th><th>Handles</th><th>Settings</th><th>Secret</th><th>State</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (r of resolvers.value() ?? []; track r.key) {
          <tr data-testid="resolver-row" [attr.data-key]="r.key">
            <td class="na-mono">{{ r.key }}@if (r.managedBy === 'config') { <div class="na-muted na-small">from configuration</div> }</td>
            <td>{{ r.kind }}@if (kinds.hasValue() && !installed(r.kind)) { <div class="na-error na-small">not in this deployment</div> }</td>
            <td>{{ r.handles.join(', ') }}</td>
            <td><code class="na-small">{{ r.settings | json }}</code></td>
            <td class="na-mono na-small">{{ r.secretRef ?? '—' }}</td>
            <td>{{ r.enabled ? 'enabled' : 'disabled' }}</td>
            <td class="na-nowrap">{{ r.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              @if (r.managedBy === 'api') {
                <button mat-button type="button" (click)="edit(r)" [disabled]="!kinds.hasValue()" data-testid="resolver-edit">Edit</button>
                @if (confirming() === r.key) {
                  <span class="na-confirm">Requests naming it reach nobody through it.
                    <button mat-flat-button type="button" (click)="delete(r.key)" [disabled]="busy()" data-testid="resolver-delete-confirm">Delete</button>
                    <button mat-button type="button" (click)="confirming.set(null)">Keep</button></span>
                } @else {
                  <button mat-button type="button" (click)="confirming.set(r.key)" data-testid="resolver-delete">Delete…</button>
                }
              } @else { <span class="na-muted na-small" [title]="note(r)">read only</span> }
            </td>
          </tr>
        } @empty { @if (!resolvers.isLoading()) { <tr><td colspan="8" class="na-muted">No resolvers yet: requests can name routes and addresses only.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="resolver-row-error">{{ rowError() }}</p> }`,
})
export class ResolversPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly resolvers = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.resolvers(params) });
  protected readonly kinds = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.resolverKinds(params) });
  protected readonly error = computed(() => problemText(this.resolvers.error()));
  protected readonly kindsError = computed(() => problemText(this.kinds.error()));
  protected readonly keyPattern = KEY_PATTERN;
  protected readonly secretHint = SECRET_REF_HINT;
  protected readonly example = '{ "kind": "user", "id": "ann", "resolver": "people" }';
  protected readonly shown = ['/key', '/kind', '/handles', '/settings', '/secretRef'];
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean; stored: Resolver | null } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected readonly generation = signal(0);
  /** The kind picked, as a signal: the handles offered follow it. */
  private readonly kind = signal('');
  protected draft = this.blank();
  protected readonly note = (r: Resolver) => managedNote(r.managedBy);
  protected readonly reloadFn = () => this.reload();
  private readonly pending = new Pending();

  /** The deployment's kinds, and a stored resolver's own when the deployment no longer has it (shown, so the select never hides it). */
  protected readonly kindOptions = computed(() => {
    const listed = (this.kinds.value() ?? []).map(k => ({ kind: k.kind, unlisted: false }));
    const stored = this.editing()?.stored?.kind;
    return stored && !listed.some(k => k.kind === stored) ? [...listed, { kind: stored, unlisted: true }] : listed;
  });
  /** What the kind can handle, and what the stored resolver handles beyond that (to take off). */
  protected readonly handleOptions = computed(() => {
    const offered = this.kinds.value()?.find(k => k.kind === this.kind())?.handles ?? [];
    const stored = this.editing()?.stored?.handles ?? [];
    return [...offered, ...stored.filter(h => !offered.includes(h))];
  });
  protected readonly kindHint = computed(() => KIND_HINTS[this.kind()] ?? '');

  constructor() {
    // Reused for another tenant: what was open or sent for the last one is closed or dropped.
    whenChanged(() => this.tenantId(), () => {
      this.pending.cancel();
      this.busy.set(false);
      this.close();
    });
  }

  private blank() {
    return { key: '', kind: '', handles: [] as string[], settings: '{}', secretRef: '', enabled: true };
  }

  private close(): void {
    this.editing.set(null);
    this.confirming.set(null);
    this.errors.clear();
    this.rowError.set('');
    this.draft = this.blank();
    this.kind.set('');
  }

  protected installed(kind: string): boolean {
    return (this.kinds.value() ?? []).some(k => k.kind === kind);
  }

  protected reload(): void {
    this.close();
    this.resolvers.reload();
    this.kinds.reload();
  }

  protected edit(r: Resolver | null): void {
    this.draft = r ? { key: r.key, kind: r.kind, handles: [...r.handles], settings: JSON.stringify(r.settings ?? {}, null, 2), secretRef: r.secretRef ?? '',
      enabled: r.enabled } : this.blank();
    this.kind.set(this.draft.kind);
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !r, stored: r });
    this.generation.update(g => g + 1);
  }

  /** A new resolver of another kind handles everything that kind can, and starts from its settings, unless some were typed. */
  protected kindChanged(): void {
    this.kind.set(this.draft.kind);
    this.draft.handles = [...(this.kinds.value()?.find(k => k.kind === this.draft.kind)?.handles ?? [])];
    const typed = this.draft.settings.trim();
    const isExample = typed === '' || Object.values(RESOLVER_SETTINGS).some(e => JSON.stringify(e, null, 2) === typed);
    if (isExample) this.draft.settings = JSON.stringify(RESOLVER_SETTINGS[this.draft.kind] ?? {}, null, 2);
  }

  protected toggle(handle: string, on: boolean): void {
    this.draft.handles = on ? [...this.draft.handles.filter(h => h !== handle), handle] : this.draft.handles.filter(h => h !== handle);
  }

  protected save(): void {
    const e = this.editing();
    const key = this.draft.key.trim();
    if (!key) {
      // Sent empty, the PUT would go to the resolvers collection, which takes none.
      this.errors.local('/key', 'Give the resolver a key.');
      return;
    }
    // A PUT creates or replaces: a new resolver is checked against the list, which must have been read.
    if (e?.isNew && !this.resolvers.hasValue()) {
      this.errors.local('/key', "The resolvers couldn't be read, so this key can't be checked as new: refresh first.");
      return;
    }
    if (e?.isNew && this.resolvers.value()?.some(r => r.key === key)) {
      this.errors.local('/key', `There is a resolver ${key} already: edit it instead.`);
      return;
    }
    if (!this.draft.kind) {
      this.errors.local('/kind', 'Pick what kind of resolver it is.');
      return;
    }
    if (this.draft.handles.length === 0) {
      this.errors.local('/handles', 'A resolver handles at least one recipient kind.');
      return;
    }
    let settings: unknown;
    try {
      settings = this.draft.settings.trim() ? JSON.parse(this.draft.settings) : {};
    } catch (err) {
      this.errors.local('/settings', `The settings aren't JSON: ${(err as Error).message}`);
      return;
    }
    // The API takes an object only; a null would read as "left out" and keep the stored settings without a word.
    if (typeof settings !== 'object' || settings === null || Array.isArray(settings)) {
      this.errors.local('/settings', 'Settings are a JSON object: { "name": value, … }.');
      return;
    }
    // Left out (null), the stored reference stays: an emptied field would look removed and not be.
    const secretRef = this.draft.secretRef.trim();
    if (!secretRef && e?.stored?.secretRef) {
      this.errors.local('/secretRef', 'A secret reference can be changed but not removed: the stored one stays. Point it at another, or create a resolver without one.');
      return;
    }
    // In the order the kind lists them, as the API stores them.
    const handles = this.handleOptions().filter(h => this.draft.handles.includes(h));
    this.pending.track(submit(this.api.putResolver(this.tenantId(), key, { kind: this.draft.kind, handles, settings, secretRef: secretRef || null, enabled: this.draft.enabled }),
      this.busy, this.errors, () => this.reload()));
  }

  protected delete(key: string): void {
    this.pending.track(rowAction(this.api.deleteResolver(this.tenantId(), key),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.reload() }));
  }
}
