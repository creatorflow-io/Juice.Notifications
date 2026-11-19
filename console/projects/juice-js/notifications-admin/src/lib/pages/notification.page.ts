import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { Recipient, RecipientOutcome } from '../core/models';
import { apiResource, cursorList } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText, words } from '../shared/ui';

/** Why a recipient got what they got (deferred #36): one sentence per outcome. */
const WHY: Record<RecipientOutcome, string> = {
  pending: 'Still being planned.',
  planned: 'Planned: its deliveries are below.',
  muted: 'Muted: a mute matched (their own, or the tenant\'s), so nothing was sent.',
  optedOut: 'Opted out: their preferences turn this type or channel off, so nothing was sent.',
  deferred: 'Held by quiet hours: their inbox has it; other channels wait until quiet hours end.',
  cancelled: 'Cancelled before it was sent: its correlation closed, or a workflow condition didn\'t hold.',
  truncated: 'The name reached more people than the limit: not everyone it names was planned.',
  resolverUnavailable: 'Its resolver stayed unavailable: no one was reached through this name.',
  failed: 'Every destination ended without going out.',
};

/** What the rules a mandatory type went past are called. */
const BYPASSED: Record<string, string> = {
  'tenant-mute': 'a tenant mute', 'opt-out': 'their opt-out', 'personal-mute': 'their mute', 'quiet-hours': 'their quiet hours',
};

/**
 * One notification (phase-4c-plan §4): what was sent, by which application, and each recipient with what came of them and why, the ones
 * that got no delivery included (muted, opted out, held by quiet hours: proof point 3), with their deliveries.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatProgressBarModule, Badge, TenantLinkPipe],
  template: `
    <div class="na-head">
      <a [routerLink]="['notifications'] | naTenant: tenantId()">Notifications</a><span class="na-muted">/</span>
      <h1 class="na-mono" data-testid="notification-id">{{ id() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="reload()">Refresh</button>
    </div>
    @if (detail.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (detail.error()) { <p class="na-error" data-testid="notification-error">{{ error() }}</p> }
    @if (detail.value(); as n) {
      <section class="na-card">
        <div class="na-actions"><na-badge [state]="n.state" data-testid="notification-state" />
          @if (n.error) { <span class="na-error">{{ n.error }}</span> }</div>
        <table class="na-kv"><tbody>
          <tr><th>Type</th><td>{{ n.type }} <span class="na-muted">version {{ n.typeVersion }}</span></td></tr>
          <tr><th>Application</th><td>{{ n.application }}</td></tr>
          <tr><th>Priority</th><td>{{ n.priority }}</td></tr>
          <tr><th>Correlation</th><td>@if (n.correlationKey && n.application) {
            <a [routerLink]="['correlations', n.application, n.correlationKey] | naTenant: tenantId()" data-testid="notification-correlation">{{ n.correlationKey }}</a>
          } @else { — }</td></tr>
          <tr><th>Idempotency key</th><td class="na-mono">{{ n.idempotencyKey }}</td></tr>
          <tr><th>Actor</th><td>{{ n.actor ?? '—' }}</td></tr>
          <tr><th>Accepted</th><td>{{ n.acceptedAt | date: 'MMM d, y, HH:mm:ss' }}</td></tr>
          @if (n.expiresAt) { <tr><th>Expires</th><td>{{ n.expiresAt | date: 'MMM d, y, HH:mm:ss' }}</td></tr> }
          @for (l of entries(n.labels); track l[0]) { <tr><th>Label {{ l[0] }}</th><td>{{ l[1] }}</td></tr> }
        </tbody></table>
      </section>
    }
    <section class="na-card">
      <h2>Recipients</h2>
      @if (recipients.first.error()) { <p class="na-error" data-testid="recipients-error">{{ recipientsError() }}</p> }
      <table class="na-table" data-testid="recipients">
        <thead><tr><th>Recipient</th><th>Outcome</th><th>Why</th><th>Deliveries</th></tr></thead>
        <tbody>
          @for (r of recipients.items(); track r.id) {
            <tr data-testid="recipient" [attr.data-outcome]="r.outcome" [attr.data-named]="r.namedAs">
              <td>
                <div>{{ who(r) }}</div>
                <div class="na-muted na-small">{{ label(r.kind) }} · {{ r.resolver }}@if (r.escalationStep) { · escalation step {{ r.escalationStep }} }</div>
                @if (r.via.length) { <div class="na-muted na-small">via {{ r.via.join(', ') }}</div> }
              </td>
              <td><na-badge [state]="r.outcome" /></td>
              <td class="na-small" data-testid="recipient-why">
                {{ why(r) }}
                @if (r.bypassed.length) { <div data-testid="recipient-bypassed">Mandatory: sent past {{ bypassed(r) }}.</div> }
                @if (r.digest; as g) {
                  <div data-testid="recipient-digest">In a digest ({{ label(g.state) }}){{ g.state === 'open' ? ', closing ' : ', closed ' }}{{ (g.closedAt ?? g.closesAt) | date: 'MMM d, HH:mm' }}.</div>
                }
              </td>
              <td>
                @for (d of r.deliveries; track d.id) {
                  <div><a class="na-mono" [routerLink]="['deliveries', d.id] | naTenant: tenantId()">{{ d.channel }}</a> <na-badge [state]="d.status" /></div>
                } @empty { <span class="na-muted" data-testid="recipient-no-delivery">None</span> }
              </td>
            </tr>
          } @empty { @if (!recipients.first.isLoading()) { <tr><td colspan="4" class="na-muted">No recipients yet.</td></tr> } }
        </tbody>
      </table>
      @if (recipients.hasMore()) {
        <div class="na-more"><span class="na-muted">{{ recipients.items().length }} shown</span>
          <button mat-stroked-button type="button" (click)="recipients.more()" [disabled]="recipients.loadingMore()">More</button></div>
      }
    </section>`,
})
export class NotificationPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  readonly id = input.required<string>();

  protected readonly label = words;
  protected readonly detail = apiResource({ params: () => ({ tenantId: this.tenantId(), id: this.id() }),
    stream: ({ params }) => this.api.notification(params.tenantId, params.id) });
  protected readonly recipients = cursorList(() => ({ tenantId: this.tenantId(), id: this.id() }),
    (p, cursor) => this.api.recipients(p.tenantId, p.id, cursor));
  protected readonly error = computed(() => problemText(this.detail.error()));
  protected readonly recipientsError = computed(() => problemText(this.recipients.first.error()));

  protected reload(): void {
    this.detail.reload();
    this.recipients.first.reload();
  }

  protected entries(values: Record<string, string>): [string, string][] {
    return Object.entries(values ?? {}).sort((a, b) => a[0].localeCompare(b[0]));
  }

  protected who(r: Recipient): string {
    switch (r.kind) {
      case 'subscriber': return r.name || (r.via.find(v => v.startsWith('user:'))?.slice(5) ?? `subscriber ${r.subscriberId}`);
      case 'route': return `route ${r.route}`;
      case 'external': return `${r.channel} ${r.address}`;
      default: return r.namedAs;
    }
  }

  protected why(r: Recipient): string {
    if (r.outcome === 'planned' && r.digest && r.deliveries.length === 0) return 'Planned into a digest: sent with the others when it closes.';
    return WHY[r.outcome] ?? words(r.outcome);
  }

  protected bypassed(r: Recipient): string {
    return r.bypassed.map(b => BYPASSED[b] ?? b).join(', ');
  }
}
