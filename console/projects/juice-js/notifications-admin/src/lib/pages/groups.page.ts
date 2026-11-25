import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { NotificationsAdminApi } from '../core/api.service';
import { Group } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, KEY_PATTERN, Pending, rowAction, submit, whenChanged } from '../shared/forms';
import { MembersComponent, MembersOf } from '../shared/people';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';

/**
 * The tenant's local groups (phase-2-plan M11): a request naming `{ "kind": "group", "id": "sre" }` through the `local` resolver reaches its
 * members. Members are any of the tenant's subscribers, added by identity.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, FieldErrorsComponent, FormErrorsComponent,
    MembersComponent],
  template: `
    <div class="na-head"><h1>Groups</h1><span class="na-spacer"></span>
      @if (!editing()) { <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!groups.hasValue()" data-testid="group-new">New group</button> }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">Local groups: a request names one as <code>{{ example }}</code> through a <code>local</code> resolver. An identity directory's
      groups are its own and aren't listed here.</p>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="group-form">
        <h2>{{ e.isNew ? 'New group' : 'Group ' + draft.key }}</h2>
        <div class="na-filters">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!e.isNew" required [pattern]="keyPattern" data-testid="group-key">
            <mat-hint>Requests name it.</mat-hint></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
            <input matInput name="name" [(ngModel)]="draft.name" required maxlength="200" data-testid="group-name"></mat-form-field>
        </div>
        <na-field-errors [messages]="errors.at('/key')" pointer="/key" />
        <na-field-errors [messages]="errors.at('/name')" pointer="/name" />
        <na-form-errors [errors]="errors" [shown]="['/key', '/name']" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="group-save">{{ e.isNew ? 'Add' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (groups.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (groups.error()) { <p class="na-error" data-testid="groups-error">{{ error() }}</p> }
    <table class="na-table" data-testid="groups">
      <thead><tr><th>Group</th><th>Name</th><th>Members</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (g of groups.value() ?? []; track g.key) {
          <tr data-testid="group-row" [attr.data-key]="g.key" [class.na-selected]="open() === g.key">
            <td class="na-mono">{{ g.key }}</td>
            <td>{{ g.name }}</td>
            <td>{{ g.members }}</td>
            <td class="na-nowrap">{{ g.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              <button mat-button type="button" (click)="open.set(open() === g.key ? null : g.key)" data-testid="group-members">
                {{ open() === g.key ? 'Hide members' : 'Members' }}</button>
              <button mat-button type="button" (click)="edit(g)" data-testid="group-edit">Rename</button>
              @if (confirming() === g.key) {
                <span class="na-confirm">{{ g.members ? g.members + ' member(s) leave it; requests' : 'Requests' }} naming it reach nobody through it.
                  <button mat-flat-button type="button" (click)="delete(g.key)" [disabled]="busy()" data-testid="group-delete-confirm">Delete</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button></span>
              } @else {
                <button mat-button type="button" (click)="confirming.set(g.key)" data-testid="group-delete">Delete…</button>
              }
            </td>
          </tr>
        } @empty { @if (!groups.isLoading()) { <tr><td colspan="5" class="na-muted">No groups yet.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="group-row-error">{{ rowError() }}</p> }
    @if (members(); as m) { <na-members [tenantId]="tenantId()" [title]="'Members of ' + open()" [of]="m" (changed)="groups.reload()" /> }`,
})
export class GroupsPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly groups = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.groups(params) });
  protected readonly error = computed(() => problemText(this.groups.error()));
  protected readonly keyPattern = KEY_PATTERN;
  protected readonly example = '{ "kind": "group", "id": "sre", "resolver": "…" }';
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  /** The group whose members are shown. */
  protected readonly open = signal<string | null>(null);
  protected readonly members = computed<MembersOf | null>(() => {
    const key = this.open();
    const tenantId = this.tenantId();
    return key === null ? null : {
      key: `${tenantId}|${key}`,
      list: () => this.api.groupMembers(tenantId, key),
      add: id => this.api.addGroupMember(tenantId, key, id),
      remove: id => this.api.removeGroupMember(tenantId, key, id),
    };
  });
  protected draft = { key: '', name: '' };
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
    this.draft = { key: '', name: '' };
  }

  protected reload(): void {
    this.close();
    this.groups.reload();
  }

  protected edit(g: Group | null): void {
    this.draft = g ? { key: g.key, name: g.name } : { key: '', name: '' };
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !g });
  }

  protected save(): void {
    const key = this.draft.key.trim();
    const isNew = this.editing()?.isNew;
    if (!key) {
      // Sent empty, the PUT would go to the groups collection, which takes none.
      this.errors.local('/key', 'Give the group a key.');
      return;
    }
    // A PUT adds or renames: a new group is checked against the list, which must have been read.
    if (isNew && !this.groups.hasValue()) {
      this.errors.local('/key', "The groups couldn't be read, so this key can't be checked as new: refresh first.");
      return;
    }
    if (isNew && this.groups.value()?.some(g => g.key === key)) {
      this.errors.local('/key', `There is a group ${key} already: rename it instead.`);
      return;
    }
    const name = this.draft.name.trim();
    if (!name) {
      this.errors.local('/name', 'Give the group a name.');
      return;
    }
    this.pending.track(submit(this.api.putGroup(this.tenantId(), key, { name }), this.busy, this.errors, () => {
      this.reload();
      if (isNew) this.open.set(key);
    }));
  }

  protected delete(key: string): void {
    this.pending.track(rowAction(this.api.deleteGroup(this.tenantId(), key), { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => {
      if (this.open() === key) this.open.set(null);
      this.reload();
    } }));
  }
}
