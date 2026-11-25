import { ChangeDetectionStrategy, Component, WritableSignal, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import { NotificationsAdminApi } from '../core/api.service';
import { Subscriber } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, Pending, rowAction, submit, whenChanged } from './forms';
import { apiResource } from './resource';
import { TenantLinkPipe, problemText } from './ui';

/** The source a local user's identity has (the `local` resolver's people). */
export const LOCAL_SOURCE = 'local';

/** A subscriber as a list names them: their name, or their identity when they have none. */
export function personName(s: Subscriber): string {
  return s.name ?? `${s.source}:${s.externalId}`;
}

/**
 * The subscriber a request naming this identity reaches, from a list found by it: the one it is linked to as an alias (aliases are read
 * first; the identity's own subscriber is then merged into that one), else its own.
 */
export function reachedBy(found: Subscriber[], source: string, externalId: string): Subscriber | undefined {
  return found.find(s => s.aliases.some(a => a.source === source && a.externalId === externalId))
    ?? found.find(s => s.source === source && s.externalId === externalId);
}

/**
 * Sends `act` for the subscriber an identity names (their own, or one linked to them as an alias): the API takes subscribers by id, people
 * know them by identity. Nobody known by it is said on `/externalId`, as the form's own error; the API's refusals as they come. The
 * subscription covers the lookup and the act.
 */
export function forSubscriber(api: NotificationsAdminApi, tenantId: string, identity: { source: string; externalId: string },
  act: (s: Subscriber) => Observable<unknown>, busy: WritableSignal<boolean>, errors: FormErrors, done: () => void): Subscription {
  const { source, externalId } = identity;
  const all = new Subscription();
  // At most two match: the identity's own subscriber and the one it is linked to.
  all.add(submit(api.subscribers(tenantId, { source, externalId, limit: 2 }), busy, errors, found => {
    const s = reachedBy(found, source, externalId);
    if (!s) {
      errors.local('/externalId',
        `Nobody here is known as ${source}:${externalId} yet: add a local user on the subscribers page; others appear once a request names them.`);
      return;
    }
    all.add(submit(act(s), busy, errors, done));
  }));
  return all;
}

/** Where a group's members or a topic's subscribers come from and go to. */
export interface MembersOf {
  /** Read again whenever this changes (another group or topic). */
  key: string;
  list: () => Observable<Subscriber[]>;
  add: (subscriberId: string) => Observable<void>;
  remove: (subscriberId: string) => Observable<void>;
}

/**
 * The people in a local group or following a topic: each links to their subscriber page, and is removed after a confirmation; someone is
 * added by their identity (source and external id; a local user's source is `local`). `changed` says the count moved.
 */
@Component({
  selector: 'na-members',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, TenantLinkPipe, FieldErrorsComponent,
    FormErrorsComponent],
  template: `
    <section class="na-card" data-testid="members">
      <h2>{{ title() }}</h2>
      <form class="na-filters" (ngSubmit)="add()" data-testid="member-add-form">
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Source</mat-label>
          <input matInput name="source" [(ngModel)]="draft.source" spellcheck="false" data-testid="member-source"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>External id</mat-label>
          <input matInput name="externalId" [(ngModel)]="draft.externalId" spellcheck="false" data-testid="member-id"></mat-form-field>
        <button mat-flat-button type="submit" [disabled]="busy() || !draft.externalId.trim() || !draft.source.trim()" data-testid="member-add">Add</button>
      </form>
      <p class="na-hint">By identity: <code>local</code> and a local user's id, or the source a request named them by (an identity directory's).</p>
      <na-field-errors [messages]="errors.at('/externalId')" pointer="/externalId" />
      <na-form-errors [errors]="errors" [shown]="['/externalId']" />
      @if (members.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
      @if (members.error()) { <p class="na-error" data-testid="members-error">{{ error() }}</p> }
      <table class="na-table">
        <thead><tr><th>Person</th><th>Identity</th><th></th></tr></thead>
        <tbody>
          @for (s of members.value() ?? []; track s.id) {
            <tr data-testid="member-row" [attr.data-id]="s.id" [attr.data-identity]="s.source + ':' + s.externalId">
              <td><a [routerLink]="['subscribers', s.id] | naTenant: tenantId()">{{ name(s) }}</a></td>
              <td class="na-mono na-small">{{ s.source }}:{{ s.externalId }}</td>
              <td class="na-nowrap">
                @if (confirming() === s.id) {
                  <button mat-flat-button type="button" (click)="remove(s.id)" [disabled]="busy()" data-testid="member-remove-confirm">Remove</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button>
                } @else {
                  <button mat-button type="button" (click)="confirming.set(s.id)" data-testid="member-remove">Remove…</button>
                }
              </td>
            </tr>
          } @empty { @if (members.hasValue()) { <tr><td colspan="3" class="na-muted">Nobody yet.</td></tr> } }
        </tbody>
      </table>
      @if (rowError()) { <p class="na-error" data-testid="member-row-error">{{ rowError() }}</p> }
    </section>`,
})
export class MembersComponent {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  readonly title = input.required<string>();
  readonly of = input.required<MembersOf>();
  readonly changed = output<void>();

  protected readonly members = apiResource({ params: () => ({ key: this.of().key, of: this.of() }), stream: ({ params }) => params.of.list() });
  protected readonly error = computed(() => problemText(this.members.error()));
  protected readonly errors = new FormErrors();
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected draft = { source: LOCAL_SOURCE, externalId: '' };
  protected readonly name = personName;
  private readonly pending = new Pending();

  constructor() {
    // Another group or topic: nothing typed, armed or sent for the last one carries over.
    whenChanged(() => this.of().key, () => {
      this.pending.cancel();
      this.busy.set(false);
      this.draft = { source: LOCAL_SOURCE, externalId: '' };
      this.errors.clear();
      this.confirming.set(null);
      this.rowError.set('');
    });
  }

  protected add(): void {
    const of = this.of();
    const source = this.draft.source.trim();
    const externalId = this.draft.externalId.trim();
    this.pending.track(forSubscriber(this.api, this.tenantId(), { source, externalId }, s => of.add(s.id), this.busy, this.errors, () => {
      this.draft = { source, externalId: '' };
      this.members.reload();
      this.changed.emit();
    }));
  }

  protected remove(id: string): void {
    this.pending.track(rowAction(this.of().remove(id), { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => {
      this.members.reload();
      this.changed.emit();
    } }));
  }
}
