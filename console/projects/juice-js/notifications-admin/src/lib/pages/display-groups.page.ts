import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { DisplayGroup } from '../core/models';
import { FormErrors, FormErrorsComponent, NaFieldError, isBlankNumber, keyIssue, rowAction, submit, validateAll, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { TenantLinkPipe, problemText } from '../shared/ui';

/**
 * The tenant's inbox display groups (design D12): the tabs people's inboxes sort items into, in order of position. Every tenant has three
 * built-ins (renamed, never deleted); others are added here. A type's items go to the group it declares unless the tenant moved it (on the
 * type's page); a group deleted sends the types moved to it back to their own, and their open items with them.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, TenantLinkPipe,
    NaFieldError, FormErrorsComponent],
  template: `
    <div class="na-head"><h1>Display groups</h1><span class="na-spacer"></span>
      @if (!editing()) { <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!groups.hasValue()" data-testid="group-new">New group</button> }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">The tabs of people's inboxes, in order. A type's items go to the group it declares; move a type on its page.</p>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="group-form">
        <h2>{{ e.isNew ? 'New group' : 'Group ' + draft.key }}</h2>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!e.isNew" required data-testid="group-key" [naError]="errors" naPointer="/key">
            <mat-hint>Types name it in their declaration.</mat-hint>
            @for (m of errors.of('/key'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
            <input matInput name="name" [(ngModel)]="draft.name" required data-testid="group-name" [naError]="errors" naPointer="/name">
            @for (m of errors.of('/name'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Position</mat-label>
            <input matInput type="number" name="position" [(ngModel)]="draft.position" min="0" max="100000" data-testid="group-position"
              [naError]="errors" naPointer="/position">
            <mat-hint>{{ e.isNew ? 'Empty: after the last.' : 'Lower first.' }}</mat-hint>
            @for (m of errors.of('/position'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
          </mat-form-field>
        </div>
        <na-form-errors [errors]="errors" [shown]="['/key', '/name', '/position']" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="group-save">{{ e.isNew ? 'Add' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (groups.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (groups.error()) { <p class="na-error" data-testid="groups-error">{{ error() }}</p> }
    <table class="na-table" data-testid="groups">
      <thead><tr><th>Position</th><th>Group</th><th>Name</th><th>Types moved here</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (g of groups.value() ?? []; track g.key) {
          <tr data-testid="group-row" [attr.data-key]="g.key">
            <td>{{ g.position }}</td>
            <td class="na-mono">{{ g.key }}@if (g.builtIn) { <div class="na-muted na-small">every tenant's</div> }</td>
            <td>{{ g.name }}</td>
            <td class="na-small">
              @for (t of g.types; track t; let last = $last) { <a class="na-mono" [routerLink]="['types', t] | naTenant: tenantId()">{{ t }}</a>{{ last ? '' : ', ' }} }
              @empty { <span class="na-muted">none</span> }
            </td>
            <td class="na-nowrap">{{ g.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              <button mat-button type="button" (click)="edit(g)" data-testid="group-edit">Edit</button>
              @if (!g.builtIn) {
                @if (confirming() === g.key) {
                  <span class="na-confirm">{{ g.types.length ? g.types.length + ' type(s) go back to their own groups. ' : '' }}
                    <button mat-flat-button type="button" (click)="delete(g.key)" [disabled]="busy()" data-testid="group-delete-confirm">Delete</button>
                    <button mat-button type="button" (click)="confirming.set(null)">Keep</button></span>
                } @else {
                  <button mat-button type="button" (click)="confirming.set(g.key)" data-testid="group-delete">Delete…</button>
                }
              }
            </td>
          </tr>
        } @empty { @if (!groups.isLoading()) { <tr><td colspan="6" class="na-muted">No groups.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="group-row-error">{{ rowError() }}</p> }`,
})
export class DisplayGroupsPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly groups = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.displayGroups(params) });
  protected readonly error = computed(() => problemText(this.groups.error()));
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected draft: { key: string; name: string; position: number | null } = { key: '', name: '', position: null };

  constructor() {
    // Reused for another tenant: what was open for the last one is closed.
    whenChanged(() => this.tenantId(), () => this.close());
  }

  private close(): void {
    this.editing.set(null);
    this.confirming.set(null);
    this.errors.clear();
    this.rowError.set('');
    this.draft = { key: '', name: '', position: null };
  }

  protected reload(): void {
    this.close();
    this.groups.reload();
  }

  protected edit(g: DisplayGroup | null): void {
    this.draft = g ? { key: g.key, name: g.name, position: g.position } : { key: '', name: '', position: null };
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !g });
  }

  protected save(): void {
    const key = this.draft.key.trim();
    const isNew = this.editing()?.isNew;
    const name = this.draft.name.trim();
    // Empty: a new group goes after the last; an existing one keeps its place (null is left out).
    const blank = isBlankNumber(this.draft.position);
    const issues = validateAll([
      // Sent empty, the PUT would go to the groups collection, which takes none.
      () => isNew ? keyIssue(key, 'display group') : null,
      // A PUT adds or changes: a new group is checked against the list, which must have been read.
      () => isNew && key && !this.groups.hasValue()
        ? { pointer: '/key', message: "Key: the groups couldn't be read, so this key can't be checked as new; refresh first." } : null,
      () => isNew && key && this.groups.value()?.some(g => g.key === key)
        ? { pointer: '/key', message: 'Key: there is a group with this key already; edit it instead.' } : null,
      () => name ? (name.length > 100 ? { pointer: '/name', message: `Name: up to 100 characters (${name.length} typed).` } : null)
        : { pointer: '/name', message: 'Name: give the group a name, up to 100 characters.' },
      () => !isNew && blank ? { pointer: '/position', message: 'Position: give a whole number, 0 or more; lower shows first.' } : null,
      () => !blank && (!Number.isInteger(Number(this.draft.position)) || Number(this.draft.position) < 0 || Number(this.draft.position) > 100000)
        ? { pointer: '/position', message: 'Position: a whole number from 0 to 100000; lower shows first.' } : null,
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    submit(this.api.putDisplayGroup(this.tenantId(), key, { name, position: blank ? null : Number(this.draft.position) }), this.busy, this.errors,
      () => this.reload());
  }

  protected delete(key: string): void {
    rowAction(this.api.deleteDisplayGroup(this.tenantId(), key),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.reload() });
  }
}
