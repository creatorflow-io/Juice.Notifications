import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { apiResource } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText, words } from '../shared/ui';
import { tenantRights } from '../shared/rights';

/**
 * One delivery (phase-4c-plan §4): where it went and through what, the recipients it carries, every attempt with its outcome, and, when
 * it failed, "Retry": the same delivery (its id and content), sent again with a fresh attempt budget (design D6).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatProgressBarModule, Badge, TenantLinkPipe],
  template: `
    <div class="na-head">
      <a [routerLink]="['deliveries'] | naTenant: tenantId()">Deliveries</a><span class="na-muted">/</span>
      <h1 class="na-mono" data-testid="delivery-id">{{ id() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="detail.reload()">Refresh</button>
    </div>
    @if (detail.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (detail.error()) { <p class="na-error" data-testid="delivery-error">{{ error() }}</p> }
    @if (detail.value(); as d) {
      <section class="na-card">
        <div class="na-actions">
          <na-badge [state]="d.delivery.status" data-testid="delivery-status" />
          @if (d.delivery.failureCategory) { <span class="na-muted">{{ label(d.delivery.failureCategory) }}</span> }
          <span class="na-spacer"></span>
          @if (d.delivery.status === 'failed') {
            @if (can.operate()) { <button mat-flat-button type="button" (click)="retry()" [disabled]="retrying()" data-testid="retry">Retry</button> }
          }
          @if (retried(); as r) { <span data-testid="retry-result">Sent again: {{ label(r.status) }}, retry {{ r.replays }}.</span> }
          @if (retryError()) { <span class="na-error" data-testid="retry-error">{{ retryError() }}</span> }
        </div>
        <table class="na-kv"><tbody>
          <tr><th>Notification</th><td>
            @if (d.delivery.requestId; as request) {
              <a class="na-mono" [routerLink]="['notifications', request] | naTenant: tenantId()" data-testid="delivery-notification">{{ request }}</a>
            } @else { <span class="na-muted">A digest of several</span> }</td></tr>
          <tr><th>Type</th><td>{{ d.delivery.type ?? '—' }}</td></tr>
          <tr><th>Channel</th><td>{{ d.delivery.channel }}</td></tr>
          <tr><th>Integration</th><td>{{ d.delivery.integration }}@if (d.delivery.failovers) { <span class="na-muted"> (after {{ d.delivery.failovers }} failover{{ d.delivery.failovers > 1 ? 's' : '' }})</span> }</td></tr>
          <tr><th>Destination</th><td class="na-mono">{{ d.destinationSnapshot }}</td></tr>
          <tr><th>Template</th><td>version {{ d.templateVersion }}, {{ d.locale }}</td></tr>
          <tr><th>Created</th><td>{{ d.delivery.createdAt | date: 'MMM d, y, HH:mm:ss' }}</td></tr>
          <tr><th>Completed</th><td>{{ (d.delivery.completedAt | date: 'MMM d, y, HH:mm:ss') ?? '—' }}</td></tr>
          @if (d.providerMessageId) { <tr><th>Provider's id</th><td class="na-mono">{{ d.providerMessageId }}</td></tr> }
        </tbody></table>
      </section>
      <section class="na-card">
        <h2>Attempts</h2>
        <table class="na-table" data-testid="attempts">
          <thead><tr><th>#</th><th>At</th><th>Outcome</th><th>Integration</th><th>Status</th><th>Took</th><th>Error</th></tr></thead>
          <tbody>
            @for (a of d.attempts; track a.number) {
              <tr data-testid="attempt">
                <td>{{ a.number }}</td>
                <td class="na-nowrap">{{ a.at | date: 'MMM d, HH:mm:ss' }}</td>
                <td><na-badge [state]="a.outcome === 'delivered' || a.outcome === 'accepted' ? a.outcome : 'failed'" />
                  <div class="na-muted na-small">{{ label(a.outcome) }}</div></td>
                <td>{{ a.integration }}</td>
                <td>{{ a.statusCode ?? '—' }}</td>
                <td>{{ a.durationMs }} ms</td>
                <td class="na-small">{{ a.error }}</td>
              </tr>
            } @empty { <tr><td colspan="7" class="na-muted">Not attempted yet.</td></tr> }
          </tbody>
        </table>
      </section>
      <section class="na-card">
        <h2>Recipients carried</h2>
        <table class="na-table" data-testid="items">
          <thead><tr><th>Kind</th><th>Named as</th><th>Route</th><th>Resolver</th></tr></thead>
          <tbody>
            @for (i of d.items; track i.recipientId) {
              <tr><td>{{ label(i.kind) }}</td><td class="na-mono">{{ i.namedAs }}</td><td>{{ i.route ?? '—' }}</td><td>{{ i.resolver }}</td></tr>
            }
          </tbody>
        </table>
      </section>
    }`,
})
export class DeliveryPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  /** The user's rights here: controls they may not use are hidden (the API refuses them anyway). */
  protected readonly can = tenantRights(() => this.tenantId());
  readonly id = input.required<string>();

  protected readonly label = words;
  protected readonly detail = apiResource({ params: () => ({ tenantId: this.tenantId(), id: this.id() }),
    stream: ({ params }) => this.api.delivery(params.tenantId, params.id) });
  protected readonly error = computed(() => problemText(this.detail.error()));
  protected readonly retrying = signal(false);
  protected readonly retried = signal<{ status: string; replays: number } | null>(null);
  protected readonly retryError = signal('');

  protected retry(): void {
    this.retrying.set(true);
    this.retryError.set('');
    this.api.retry(this.tenantId(), this.id()).subscribe({
      next: r => { this.retrying.set(false); this.retried.set(r); this.detail.reload(); },
      error: e => { this.retrying.set(false); this.retryError.set(problemText(e)); },
    });
  }
}
