import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { interval } from 'rxjs';
import { NotificationsAdminApi } from '../core/api.service';
import { apiResource } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText } from '../shared/ui';

/**
 * A tenant's home (phase-4c-plan p4c-d6): deliveries of the last 24 hours by channel and status, the integrations that failed, the open
 * storms and the notifications still under way, from the API (no metrics backend). Each count links to the list it counts. Read again
 * every 30 seconds while open (the API keeps it as long).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatProgressBarModule, Badge, TenantLinkPipe],
  template: `
    <div class="na-head">
      <h1>Overview</h1>
      <span class="na-spacer"></span>
      @if (overview.value(); as o) { <span class="na-muted na-small" data-testid="overview-at">Last 24 hours, as of {{ o.at | date: 'MMM d, HH:mm:ss' }}</span> }
      <button mat-stroked-button type="button" (click)="overview.reload()">Refresh</button>
    </div>
    @if (overview.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (overview.error()) { <p class="na-error" data-testid="overview-error">{{ error() }}</p> }
    @if (overview.value(); as o) {
      <div class="na-tiles" data-testid="overview-tiles">
        <a class="na-card na-tile" [routerLink]="['deliveries'] | naTenant: tenantId()" [queryParams]="{ from: o.since }" data-testid="tile-deliveries">
          <span class="na-muted">Deliveries</span><strong>{{ total() }}</strong></a>
        <a class="na-card na-tile" [routerLink]="['deliveries'] | naTenant: tenantId()" [queryParams]="{ status: 'failed', from: o.since }" data-testid="tile-failed">
          <span class="na-muted">Failed</span><strong [class.na-error]="failed() > 0">{{ failed() }}</strong></a>
        <a class="na-card na-tile" [routerLink]="['notifications'] | naTenant: tenantId()" [queryParams]="{ state: 'planning,running' }" data-testid="tile-unfinished">
          <span class="na-muted">Notifications under way</span><strong>{{ o.unfinished }}</strong></a>
        <a class="na-card na-tile" [routerLink]="['storms'] | naTenant: tenantId()" data-testid="tile-storms">
          <span class="na-muted">Open storms</span><strong [class.na-error]="o.openStorms > 0">{{ o.openStorms }}</strong></a>
      </div>
      <div class="na-grid-2">
        <section class="na-card">
          <h2>Deliveries by channel</h2>
          <table class="na-table" data-testid="overview-deliveries">
            <thead><tr><th>Channel</th><th>Status</th><th>Count</th></tr></thead>
            <tbody>
              @for (d of o.deliveries; track d.channel + d.status) {
                <tr>
                  <td>{{ d.channel }}</td>
                  <td><na-badge [state]="d.status" /></td>
                  <td><a [routerLink]="['deliveries'] | naTenant: tenantId()" [queryParams]="{ channel: d.channel, status: d.status, from: o.since }">{{ d.count }}</a></td>
                </tr>
              } @empty { <tr><td colspan="3" class="na-muted">No deliveries in the last 24 hours.</td></tr> }
            </tbody>
          </table>
        </section>
        <section class="na-card">
          <h2>Failing integrations</h2>
          <table class="na-table" data-testid="overview-failing">
            <thead><tr><th>Integration</th><th>Failed</th><th>Latest</th></tr></thead>
            <tbody>
              @for (i of o.failingIntegrations; track i.integration) {
                <tr>
                  <td><a [routerLink]="['deliveries'] | naTenant: tenantId()" [queryParams]="{ integration: i.integration, status: 'failed', from: o.since }">{{ i.integration }}</a></td>
                  <td>{{ i.failed }}</td>
                  <td><span class="na-nowrap">{{ i.lastFailedAt | date: 'MMM d, HH:mm' }}</span>
                    @if (i.lastError) { <div class="na-muted na-small">{{ i.lastError }}</div> }</td>
                </tr>
              } @empty { <tr><td colspan="3" class="na-muted">None failed in the last 24 hours.</td></tr> }
            </tbody>
          </table>
        </section>
      </div>
    }`,
})
export class OverviewPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly overview = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.overview(params) });
  protected readonly error = computed(() => problemText(this.overview.error()));
  protected readonly total = computed(() => (this.overview.value()?.deliveries ?? []).reduce((n, d) => n + d.count, 0));
  protected readonly failed = computed(() => (this.overview.value()?.deliveries ?? []).filter(d => d.status === 'failed').reduce((n, d) => n + d.count, 0));

  constructor() {
    interval(30_000).pipe(takeUntilDestroyed(inject(DestroyRef))).subscribe(() => this.overview.reload());
  }
}
