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
import { CORRELATION_STATES } from '../core/models';
import { cursorList } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText, words } from '../shared/ui';

/** The tenant's correlations, newest first (phase-4c-plan I52): open, acknowledged, resolved, cancelled; keys are per application. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, Badge,
    TenantLinkPipe],
  template: `
    <div class="na-head"><h1>Correlations</h1><span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="list.first.reload()">Refresh</button></div>
    <form class="na-filters" (ngSubmit)="apply()" data-testid="correlation-filters">
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>State</mat-label>
        <mat-select name="state" [(ngModel)]="draft.state" data-testid="filter-state">
          <mat-option value="">Any</mat-option>
          @for (s of states; track s) { <mat-option [value]="s">{{ label(s) }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Application</mat-label>
        <input matInput name="application" [(ngModel)]="draft.application" data-testid="filter-application"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
        <input matInput name="key" [(ngModel)]="draft.key" data-testid="filter-key"></mat-form-field>
      <button mat-flat-button type="submit" data-testid="filter-apply">Apply</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>
    @if (list.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (list.first.error()) { <p class="na-error" data-testid="correlations-error">{{ error() }}</p> }
    <table class="na-table" data-testid="correlations">
      <thead><tr><th>Key</th><th>Application</th><th>State</th><th>Acknowledged</th><th>Resolved</th><th>Last notification</th></tr></thead>
      <tbody>
        @for (c of list.items(); track c.id) {
          <tr data-testid="correlation-row" [attr.data-key]="c.key">
            <td><a [routerLink]="['correlations', c.application, c.key] | naTenant: tenantId()">{{ c.key }}</a></td>
            <td>{{ c.application }}</td>
            <td><na-badge [state]="c.state" />@if (c.heldAt) { <na-badge state="held" data-testid="correlation-held-badge" /> }</td>
            <td>@if (c.acknowledgedAt) { {{ c.acknowledgedBy }} <span class="na-muted na-small">{{ c.acknowledgedAt | date: 'MMM d, HH:mm' }}</span> } @else { — }</td>
            <td>@if (c.resolvedAt) { {{ c.outcome ?? '' }} <span class="na-muted na-small">{{ c.resolvedAt | date: 'MMM d, HH:mm' }}</span> } @else { — }</td>
            <td class="na-nowrap">{{ c.lastRequestAt | date: 'MMM d, HH:mm:ss' }}</td>
          </tr>
        } @empty { @if (!list.first.isLoading()) { <tr><td colspan="6" class="na-muted">No correlations match.</td></tr> } }
      </tbody>
    </table>
    <div class="na-more">
      <span class="na-muted">{{ list.items().length }} shown</span>
      @if (list.hasMore()) { <button mat-stroked-button type="button" (click)="list.more()" [disabled]="list.loadingMore()" data-testid="more">More</button> }
    </div>`,
})
export class CorrelationsPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly tenantId = input.required<string>();
  readonly state = input<string>();
  readonly application = input<string>();
  readonly key = input<string>();

  protected readonly states = CORRELATION_STATES;
  protected readonly label = words;
  protected draft = { state: '', application: '', key: '' };
  private readonly filter = computed(() => ({ state: this.state(), application: this.application(), key: this.key() }));
  protected readonly list = cursorList(() => ({ tenantId: this.tenantId(), filter: this.filter() }),
    (p, cursor) => this.api.correlations(p.tenantId, p.filter, cursor));
  protected readonly error = computed(() => problemText(this.list.first.error()));

  constructor() {
    effect(() => {
      const f = this.filter();
      untracked(() => this.draft = { state: f.state ?? '', application: f.application ?? '', key: f.key ?? '' });
    });
  }

  protected apply(): void {
    const d = this.draft;
    void this.router.navigate([], { relativeTo: this.route,
      queryParams: { state: d.state || null, application: d.application.trim() || null, key: d.key.trim() || null } });
  }

  protected clear(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: {} });
  }
}
