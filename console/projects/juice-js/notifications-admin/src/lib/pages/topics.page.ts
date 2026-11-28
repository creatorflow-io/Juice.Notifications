import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { NotificationsAdminApi } from '../core/api.service';
import { Topic } from '../core/models';
import { FormErrors, FormErrorsComponent, NaFieldError, Pending, rowAction, submit, validateAll, whenChanged } from '../shared/forms';
import { MembersComponent, MembersOf } from '../shared/people';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';

/** A topic's key, as the API takes it: `observability.service:billing`. */
const TOPIC_KEY_PATTERN = '[A-Za-z0-9][A-Za-z0-9._:\\-]{0,199}';

/**
 * The tenant's topics (phase-2-plan M11): a request naming `{ "kind": "topic", "key": "…" }` through a `topic` resolver reaches its
 * subscribers. An admin adds anyone; people join and leave a self-service topic themselves (in their product's notification settings).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, NaFieldError,
    FormErrorsComponent, MembersComponent],
  template: `
    <div class="na-head"><h1>Topics</h1><span class="na-spacer"></span>
      @if (!editing()) { <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!topics.hasValue()" data-testid="topic-new">New topic</button> }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">A request names one as <code>{{ example }}</code>. People join and leave a self-service topic themselves; only an admin adds
      them to any other.</p>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="topic-form">
        <h2>{{ e.isNew ? 'New topic' : 'Topic ' + draft.key }}</h2>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!e.isNew" required spellcheck="false" data-testid="topic-key"
              [naError]="errors" naPointer="/key">
            <mat-hint>Such as billing.service:alerts.</mat-hint>
            @for (m of errors.of('/key'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
            <input matInput name="name" [(ngModel)]="draft.name" required data-testid="topic-name" [naError]="errors" naPointer="/name">
            @for (m of errors.of('/name'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
          </mat-form-field>
          <mat-checkbox name="selfService" [(ngModel)]="draft.selfService" data-testid="topic-self-service">People join and leave it themselves</mat-checkbox>
        </div>
        <na-form-errors [errors]="errors" [shown]="['/key', '/name']" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="topic-save">{{ e.isNew ? 'Add' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (topics.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (topics.error()) { <p class="na-error" data-testid="topics-error">{{ error() }}</p> }
    <table class="na-table" data-testid="topics">
      <thead><tr><th>Topic</th><th>Name</th><th>Joined by</th><th>Subscribers</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (t of topics.value() ?? []; track t.key) {
          <tr data-testid="topic-row" [attr.data-key]="t.key" [class.na-selected]="open() === t.key">
            <td class="na-mono">{{ t.key }}</td>
            <td>{{ t.name }}</td>
            <td>{{ t.selfService ? 'people themselves, or an admin' : 'an admin only' }}</td>
            <td>{{ t.subscribers }}</td>
            <td class="na-nowrap">{{ t.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              <button mat-button type="button" (click)="open.set(open() === t.key ? null : t.key)" data-testid="topic-subscribers">
                {{ open() === t.key ? 'Hide subscribers' : 'Subscribers' }}</button>
              <button mat-button type="button" (click)="edit(t)" data-testid="topic-edit">Edit</button>
              @if (confirming() === t.key) {
                <span class="na-confirm">{{ t.subscribers ? t.subscribers + ' subscriber(s) lose it; requests' : 'Requests' }} naming it reach nobody.
                  <button mat-flat-button type="button" (click)="delete(t.key)" [disabled]="busy()" data-testid="topic-delete-confirm">Delete</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button></span>
              } @else {
                <button mat-button type="button" (click)="confirming.set(t.key)" data-testid="topic-delete">Delete…</button>
              }
            </td>
          </tr>
        } @empty { @if (!topics.isLoading()) { <tr><td colspan="6" class="na-muted">No topics yet.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="topic-row-error">{{ rowError() }}</p> }
    @if (subscribers(); as s) { <na-members [tenantId]="tenantId()" [title]="'Subscribers of ' + open()" [of]="s" (changed)="topics.reload()" /> }`,
})
export class TopicsPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly topics = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.topics(params) });
  protected readonly error = computed(() => problemText(this.topics.error()));
  protected readonly example = '{ "kind": "topic", "key": "billing.service:alerts", "resolver": "…" }';
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  /** The topic whose subscribers are shown. */
  protected readonly open = signal<string | null>(null);
  protected readonly subscribers = computed<MembersOf | null>(() => {
    const key = this.open();
    const tenantId = this.tenantId();
    return key === null ? null : {
      key: `${tenantId}|${key}`,
      list: () => this.api.topicSubscribers(tenantId, key),
      add: id => this.api.subscribe(tenantId, key, id),
      remove: id => this.api.unsubscribe(tenantId, key, id),
    };
  });
  protected draft = { key: '', name: '', selfService: false };
  protected readonly reloadFn = () => this.reload();
  private readonly pending = new Pending();

  constructor() {
    // Reused for another tenant: what was open or sent for the last one is closed or dropped.
    whenChanged(() => this.tenantId(), () => {
      this.pending.cancel();
      this.busy.set(false);
      this.close();
      this.open.set(null);
    });
  }

  private close(): void {
    this.editing.set(null);
    this.confirming.set(null);
    this.errors.clear();
    this.rowError.set('');
    this.draft = { key: '', name: '', selfService: false };
  }

  protected reload(): void {
    this.close();
    this.topics.reload();
  }

  protected edit(t: Topic | null): void {
    this.draft = t ? { key: t.key, name: t.name, selfService: t.selfService } : { key: '', name: '', selfService: false };
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !t });
  }

  protected save(): void {
    const key = this.draft.key.trim();
    const isNew = this.editing()?.isNew;
    const name = this.draft.name.trim();
    const issues = validateAll([
      // Sent empty, the PUT would go to the topics collection, which takes none.
      () => !key ? { pointer: '/key', message: 'Key: give the topic a key, such as billing.service:alerts (letters, digits and . _ : -, up to 200).' }
        : isNew && !new RegExp(`^(?:${TOPIC_KEY_PATTERN})$`).test(key)
          ? { pointer: '/key', message: 'Key: letters, digits and . _ : - only, starting with a letter or digit, up to 200 characters.' } : null,
      // A PUT adds or changes: a new topic is checked against the list, which must have been read.
      () => isNew && key && !this.topics.hasValue()
        ? { pointer: '/key', message: "Key: the topics couldn't be read, so this key can't be checked as new; refresh first." } : null,
      () => isNew && key && this.topics.value()?.some(t => t.key === key)
        ? { pointer: '/key', message: 'Key: there is a topic with this key already; edit it instead.' } : null,
      () => name ? (name.length > 200 ? { pointer: '/name', message: `Name: up to 200 characters (${name.length} typed).` } : null)
        : { pointer: '/name', message: 'Name: give the topic a name, up to 200 characters.' },
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    this.pending.track(submit(this.api.putTopic(this.tenantId(), key, { name, selfService: this.draft.selfService }), this.busy, this.errors, () => {
      this.reload();
      if (isNew) this.open.set(key);
    }));
  }

  protected delete(key: string): void {
    this.pending.track(rowAction(this.api.deleteTopic(this.tenantId(), key), { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => {
      if (this.open() === key) this.open.set(null);
      this.reload();
    } }));
  }
}
