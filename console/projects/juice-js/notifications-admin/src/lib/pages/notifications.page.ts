import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { NOTIFICATION_STATES } from '../core/models';
import { cursorList } from '../shared/resource';
import { Badge, TenantLinkPipe, instant, localInput, problemText, words } from '../shared/ui';

/** The tenant's notifications, newest first (phase-4c-plan I52): by type, application, correlation, state and time, the filter in the URL. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, Badge,
    TenantLinkPipe],
  template: `
    <div class="na-head"><h1>Notifications</h1><span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="list.first.reload()">Refresh</button></div>
    <form class="na-filters" (ngSubmit)="apply()" data-testid="notification-filters">
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Type</mat-label>
        <input matInput name="type" [(ngModel)]="draft.type" data-testid="filter-type"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Application</mat-label>
        <input matInput name="application" [(ngModel)]="draft.application" data-testid="filter-application"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Correlation key</mat-label>
        <input matInput name="correlation" [(ngModel)]="draft.correlation" data-testid="filter-correlation"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>State</mat-label>
        <mat-select name="state" [(ngModel)]="draft.state" data-testid="filter-state">
          <mat-option value="">Any</mat-option>
          @for (s of states; track s) { <mat-option [value]="s">{{ label(s) }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>From</mat-label>
        <input matInput type="datetime-local" name="from" [(ngModel)]="draft.from"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>To</mat-label>
        <input matInput type="datetime-local" name="to" [(ngModel)]="draft.to"></mat-form-field>
      <button mat-flat-button type="submit" data-testid="filter-apply">Apply</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>
    @if (list.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (list.first.error()) { <p class="na-error" data-testid="notifications-error">{{ error() }}</p> }
    <table class="na-table" data-testid="notifications">
      <thead><tr><th>Notification</th><th>Type</th><th>Application</th><th>State</th><th>Correlation</th><th>Priority</th><th>Accepted</th><th>Actor</th></tr></thead>
      <tbody>
        @for (n of list.items(); track n.id) {
          <tr data-testid="notification-row" [attr.data-id]="n.id">
            <td><a class="na-mono" [routerLink]="['notifications', n.id] | naTenant: tenantId()">{{ n.id.slice(0, 8) }}…</a></td>
            <td>{{ n.type }} <span class="na-muted na-small">v{{ n.typeVersion }}</span></td>
            <td>{{ n.application }}</td>
            <td><na-badge [state]="n.state" />@if (n.error) { <div class="na-error na-small">{{ n.error }}</div> }</td>
            <td>@if (n.correlationKey) {
              <a [routerLink]="['correlations', n.application, n.correlationKey] | naTenant: tenantId()">{{ n.correlationKey }}</a> } @else { — }</td>
            <td>{{ n.priority }}</td>
            <td class="na-nowrap">{{ n.acceptedAt | date: 'MMM d, HH:mm:ss' }}</td>
            <td>{{ n.actor }}</td>
          </tr>
        } @empty { @if (!list.first.isLoading()) { <tr><td colspan="8" class="na-muted">No notifications match.</td></tr> } }
      </tbody>
    </table>
    <div class="na-more">
      <span class="na-muted">{{ list.items().length }} shown</span>
      @if (list.hasMore()) { <button mat-stroked-button type="button" (click)="list.more()" [disabled]="list.loadingMore()" data-testid="more">More</button> }
    </div>`,
})
export class NotificationsPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly tenantId = input.required<string>();
  readonly type = input<string>();
  readonly application = input<string>();
  readonly correlation = input<string>();
  readonly state = input<string>();
  readonly from = input<string>();
  readonly to = input<string>();

  protected readonly states = NOTIFICATION_STATES;
  protected readonly label = words;
  protected draft = { type: '', application: '', correlation: '', state: '', from: '', to: '' };
  private readonly filter = computed(() => ({ type: this.type(), application: this.application(), correlation: this.correlation(),
    state: this.state(), from: this.from(), to: this.to() }));
  protected readonly list = cursorList(() => ({ tenantId: this.tenantId(), filter: this.filter() }),
    (p, cursor) => this.api.notifications(p.tenantId, p.filter, cursor));
  protected readonly error = computed(() => problemText(this.list.first.error()));

  constructor() {
    effect(() => {
      const f = this.filter();
      untracked(() => this.draft = { type: f.type ?? '', application: f.application ?? '', correlation: f.correlation ?? '', state: f.state ?? '',
        from: localInput(f.from), to: localInput(f.to) });
    });
  }

  protected apply(): void {
    const d = this.draft;
    void this.router.navigate([], { relativeTo: this.route, queryParams: { type: d.type.trim() || null, application: d.application.trim() || null,
      correlation: d.correlation.trim() || null, state: d.state || null, from: instant(d.from) ?? null, to: instant(d.to) ?? null } });
  }

  protected clear(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: {} });
  }
}
