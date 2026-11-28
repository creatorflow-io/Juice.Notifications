import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { NotificationsAdminApi } from '../core/api.service';
import { FormErrors, FormErrorsComponent, NaFieldError, Pending, submit, validateAll, whenChanged } from '../shared/forms';
import { LOCAL_SOURCE, personName } from '../shared/people';
import { cursorList } from '../shared/resource';
import { TenantLinkPipe, problemText } from '../shared/ui';

/** Subscribers per page; a full page may have more after it. */
const PAGE = 50;

/** A local user's id, as the API takes it. */
const LOCAL_ID_PATTERN = '[A-Za-z0-9][A-Za-z0-9._@+\\-]{0,199}';

/**
 * The people the tenant notifies (design D23), in the order they were first seen: by the identity a request named them by (an identity
 * directory's), or local users added here. Found by source, or by an identity (their own or one linked to them).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, TenantLinkPipe,
    NaFieldError, FormErrorsComponent],
  template: `
    <div class="na-head"><h1>Subscribers</h1><span class="na-spacer"></span>
      @if (!adding()) { <button mat-flat-button type="button" (click)="startAdding()" data-testid="local-user-new">New local user</button> }
      <button mat-stroked-button type="button" (click)="list.first.reload()" data-testid="refresh">Refresh</button></div>
    @if (adding()) {
      <form class="na-form na-card" (ngSubmit)="add()" data-testid="local-user-form">
        <h2>New local user</h2>
        <p class="na-hint">Someone the tenant keeps itself (no identity directory): requests name them as <code>{{ example }}</code> through a
          <code>local</code> resolver.</p>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Id</mat-label>
            <input matInput name="id" [(ngModel)]="draft.id" required spellcheck="false" data-testid="local-user-id" [naError]="errors" naPointer="/id">
            @for (e of errors.of('/id'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
            <input matInput name="name" [(ngModel)]="draft.name" data-testid="local-user-name" [naError]="errors" naPointer="/name">
            @for (e of errors.of('/name'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Locale</mat-label>
            <input matInput name="locale" [(ngModel)]="draft.locale" placeholder="en" data-testid="local-user-locale" [naError]="errors" naPointer="/locale">
            @for (e of errors.of('/locale'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Time zone</mat-label>
            <input matInput name="timeZone" [(ngModel)]="draft.timeZone" placeholder="Asia/Ho_Chi_Minh" data-testid="local-user-time-zone"
              [naError]="errors" naPointer="/timeZone">
            @for (e of errors.of('/timeZone'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
        </div>
        <na-form-errors [errors]="errors" [shown]="fields" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="local-user-save">Add</button>
          <button mat-button type="button" (click)="adding.set(false)">Cancel</button>
        </div>
      </form>
    }
    <form class="na-filters" (ngSubmit)="apply()" data-testid="subscriber-filters">
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Source</mat-label>
        <input matInput name="source" [(ngModel)]="filterDraft.source" spellcheck="false" data-testid="filter-source"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>External id</mat-label>
        <input matInput name="externalId" [(ngModel)]="filterDraft.externalId" spellcheck="false" data-testid="filter-external-id">
        <mat-hint>Exactly: their own, or one linked to them.</mat-hint></mat-form-field>
      <button mat-flat-button type="submit" data-testid="filter-apply">Find</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>
    @if (list.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (list.first.error()) { <p class="na-error" data-testid="subscribers-error">{{ error() }}</p> }
    <table class="na-table" data-testid="subscribers">
      <thead><tr><th>Person</th><th>Identity</th><th>Linked identities</th><th>Locale</th><th>Time zone</th><th>First seen</th></tr></thead>
      <tbody>
        @for (s of list.items(); track s.id) {
          <tr data-testid="subscriber-row" [attr.data-id]="s.id" [attr.data-identity]="s.source + ':' + s.externalId">
            <td><a [routerLink]="['subscribers', s.id] | naTenant: tenantId()">{{ name(s) }}</a>
              @if (s.mergedInto) { <div class="na-muted na-small">linked into another</div> }</td>
            <td class="na-mono na-small">{{ s.source }}:{{ s.externalId }}</td>
            <td class="na-small">@for (a of s.aliases; track a.source + a.externalId; let last = $last) {
              <span class="na-mono">{{ a.source }}:{{ a.externalId }}</span>{{ last ? '' : ', ' } } @empty { <span class="na-muted">none</span> }</td>
            <td>{{ s.locale ?? '—' }}</td>
            <td>{{ s.timeZone ?? '—' }}</td>
            <td class="na-nowrap">{{ s.createdAt | date: 'MMM d, y, HH:mm' }}</td>
          </tr>
        } @empty { @if (!list.first.isLoading()) { <tr><td colspan="6" class="na-muted">No subscribers match.</td></tr> } }
      </tbody>
    </table>
    <div class="na-more">
      <span class="na-muted">{{ list.items().length }} shown</span>
      @if (list.hasMore()) { <button mat-stroked-button type="button" (click)="list.more()" [disabled]="list.loadingMore()" data-testid="more">More</button> }
    </div>`,
})
export class SubscribersPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly tenantId = input.required<string>();
  readonly source = input<string>();
  readonly externalId = input<string>();

  private readonly filter = computed(() => ({ source: this.source(), externalId: this.externalId() }));
  // The API pages by the last id seen; a full page may have more after it.
  protected readonly list = cursorList(() => ({ tenantId: this.tenantId(), filter: this.filter() }),
    (p, cursor) => this.api.subscribers(p.tenantId, { ...p.filter, after: cursor, limit: PAGE })
      .pipe(map(items => ({ items, next: items.length === PAGE ? items[items.length - 1].id : null }))));
  protected readonly error = computed(() => problemText(this.list.first.error()));
  protected readonly name = personName;
  protected readonly idPattern = LOCAL_ID_PATTERN;
  protected readonly example = '{ "kind": "user", "id": "dee", "resolver": "…" }';
  protected readonly fields = ['/id', '/name', '/locale', '/timeZone'];
  protected readonly errors = new FormErrors();
  protected readonly adding = signal(false);
  protected readonly busy = signal(false);
  protected filterDraft = { source: '', externalId: '' };
  protected draft = { id: '', name: '', locale: '', timeZone: '' };
  /** The add running (the lookup, then the PUT). */
  private readonly pending = new Pending();

  constructor() {
    effect(() => {
      const f = this.filter();
      untracked(() => this.filterDraft = { source: f.source ?? '', externalId: f.externalId ?? '' });
    });
    // Reused for another tenant: a local user half typed (or being added) for the last one isn't added to, or opened in, the next.
    whenChanged(() => this.tenantId(), () => {
      this.pending.cancel();
      this.busy.set(false);
      this.adding.set(false);
      this.errors.clear();
    });
  }

  protected apply(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { source: this.filterDraft.source.trim() || null,
      externalId: this.filterDraft.externalId.trim() || null } });
  }

  protected clear(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: {} });
  }

  protected startAdding(): void {
    this.draft = { id: '', name: '', locale: '', timeZone: '' };
    this.errors.clear();
    this.adding.set(true);
  }

  /** A new local user: the PUT would change one already there, so the id is looked up first. Then their page. */
  protected add(): void {
    const id = this.draft.id.trim();
    const d = this.draft;
    const issues = validateAll([
      () => !id ? { pointer: '/id', message: 'Id: give the local user an id, as requests name them.' }
        : !new RegExp(`^(?:${LOCAL_ID_PATTERN})$`).test(id)
          ? { pointer: '/id', message: 'Id: letters, digits and . _ @ + - only, starting with a letter or digit, up to 200 characters.' } : null,
      () => d.name.trim().length > 200 ? { pointer: '/name', message: `Name: up to 200 characters (${d.name.trim().length} typed).` } : null,
      () => d.locale.trim().length > 35 ? { pointer: '/locale', message: `Locale: up to 35 characters (${d.locale.trim().length} typed).` } : null,
      () => d.timeZone.trim().length > 64 ? { pointer: '/timeZone', message: `Time zone: up to 64 characters (${d.timeZone.trim().length} typed).` } : null,
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    const tenantId = this.tenantId();
    // At most two match: the local user, and a subscriber their identity is linked to (who comes first when the local user is merged in).
    this.pending.track(submit(this.api.subscribers(tenantId, { source: LOCAL_SOURCE, externalId: id, limit: 2 }), this.busy, this.errors, found => {
      if (found.some(s => s.source === LOCAL_SOURCE && s.externalId === id)) {
        this.errors.local('/id', 'Id: there is a local user with this id already; open them to change their details.');
        return;
      }
      // Linked to someone as an alias: requests naming it reach them, and the API keeps it theirs.
      const linked = found.find(s => s.aliases.some(a => a.source === LOCAL_SOURCE && a.externalId === id));
      if (linked) {
        this.errors.local('/id', `${LOCAL_SOURCE}:${id} is linked to ${personName(linked)}, who receives for it: unlink it on their page first.`);
        return;
      }
      this.pending.track(submit(this.api.putLocalUser(tenantId, id, { name: d.name.trim() || null, locale: d.locale.trim() || null,
        timeZone: d.timeZone.trim() || null }), this.busy, this.errors, s => {
        this.adding.set(false);
        void this.router.navigate([s.id], { relativeTo: this.route });
      }));
    }));
  }
}
