import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { apiResource } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText, words } from '../shared/ui';

/**
 * One correlation (design §5.3): its state, who acknowledged and resolved it, how its recipients and deliveries stand, and its latest
 * notifications. An admin acknowledges, resolves or cancels it as themselves (the API records who), which stops what is still due.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, Badge, TenantLinkPipe],
  template: `
    <div class="na-head">
      <a [routerLink]="['correlations'] | naTenant: tenantId()">Correlations</a><span class="na-muted">/</span>
      <span class="na-muted">{{ application() }}</span><span class="na-muted">/</span>
      <h1 data-testid="correlation-key">{{ key() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="correlation.reload()">Refresh</button>
    </div>
    @if (correlation.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (correlation.error()) { <p class="na-error" data-testid="correlation-error">{{ error() }}</p> }
    @if (correlation.value(); as c) {
      <section class="na-card">
        <div class="na-actions"><na-badge [state]="c.state" data-testid="correlation-state" />
          @if (c.reopenedAt) { <span class="na-muted na-small">reopened {{ c.reopenedAt | date: 'MMM d, HH:mm' }}</span> }</div>
        <table class="na-kv"><tbody>
          <tr><th>Opened</th><td>{{ c.createdAt | date: 'MMM d, y, HH:mm:ss' }}</td></tr>
          <tr><th>Last notification</th><td>{{ c.lastRequestAt | date: 'MMM d, y, HH:mm:ss' }}</td></tr>
          <tr><th>Acknowledged</th><td data-testid="correlation-acknowledged">@if (c.acknowledgedAt) { by {{ c.acknowledgedBy ?? '—' }}, {{ c.acknowledgedAt | date: 'MMM d, HH:mm:ss' }} } @else { — }</td></tr>
          <tr><th>Resolved</th><td>@if (c.resolvedAt) { {{ c.outcome ?? (c.state === 'cancelled' ? 'cancelled' : '') }} by {{ c.resolvedBy ?? '—' }}, {{ c.resolvedAt | date: 'MMM d, HH:mm:ss' }} } @else { — }</td></tr>
          <tr><th>Recipients</th><td>{{ counts(c.recipients) || '—' }}</td></tr>
          <tr><th>Deliveries</th><td>{{ counts(c.deliveries) || '—' }}</td></tr>
        </tbody></table>
        @if (c.state === 'open' || c.state === 'acknowledged') {
          <form class="na-actions" (ngSubmit)="change('resolve')">
            @if (c.state === 'open') {
              <button mat-stroked-button type="button" (click)="change('acknowledge')" [disabled]="busy()" data-testid="acknowledge">Acknowledge</button>
            }
            <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Outcome</mat-label>
              <input matInput name="outcome" [(ngModel)]="outcome" maxlength="500" data-testid="resolve-outcome"></mat-form-field>
            <button mat-flat-button type="submit" [disabled]="busy()" data-testid="resolve">Resolve</button>
            <button mat-button type="button" (click)="change('cancel')" [disabled]="busy()" data-testid="cancel">Cancel it</button>
            <span class="na-muted na-small">Resolving or cancelling stops what's still due (escalations, waiting steps).</span>
          </form>
        }
        @if (changeError()) { <p class="na-error" data-testid="correlation-change-error">{{ changeError() }}</p> }
      </section>
      <section class="na-card">
        <h2>Notifications</h2>
        <table class="na-table" data-testid="correlation-notifications">
          <thead><tr><th>Notification</th><th>Type</th><th>State</th><th>Created</th></tr></thead>
          <tbody>
            @for (n of c.notifications; track n.id) {
              <tr>
                <td><a class="na-mono" [routerLink]="['notifications', n.requestId] | naTenant: tenantId()">{{ n.requestId.slice(0, 8) }}…</a></td>
                <td>{{ n.type }}</td><td><na-badge [state]="n.state" /></td>
                <td class="na-nowrap">{{ n.createdAt | date: 'MMM d, HH:mm:ss' }}</td>
              </tr>
            }
          </tbody>
        </table>
        <p class="na-small"><a [routerLink]="['notifications'] | naTenant: tenantId()" [queryParams]="{ application: application(), correlation: key() }">
          All its notifications</a></p>
      </section>
    }`,
})
export class CorrelationPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  readonly application = input.required<string>();
  readonly key = input.required<string>();

  protected readonly correlation = apiResource({ params: () => ({ tenantId: this.tenantId(), application: this.application(), key: this.key() }),
    stream: ({ params }) => this.api.correlation(params.tenantId, params.application, params.key) });
  protected readonly error = computed(() => problemText(this.correlation.error()));
  protected readonly busy = signal(false);
  protected readonly changeError = signal('');
  protected outcome = '';

  protected counts(values: Record<string, number>): string {
    return Object.entries(values ?? {}).filter(e => e[1] > 0).map(([k, n]) => `${n} ${words(k)}`).join(', ');
  }

  protected change(action: 'acknowledge' | 'resolve' | 'cancel'): void {
    this.busy.set(true);
    this.changeError.set('');
    this.api.changeCorrelation(this.tenantId(), this.application(), this.key(), action, this.outcome.trim()).subscribe({
      next: () => { this.busy.set(false); this.correlation.reload(); },
      error: e => { this.busy.set(false); this.changeError.set(problemText(e)); },
    });
  }
}
