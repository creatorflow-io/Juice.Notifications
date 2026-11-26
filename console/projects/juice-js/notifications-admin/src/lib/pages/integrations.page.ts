import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { Integration } from '../core/models';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';
import { whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { Badge, TenantLinkPipe, problemText } from '../shared/ui';
import { tenantRights } from '../shared/rights';
import { IntegrationForm } from './integration-form';

/**
 * The tenant's integrations (phase-4c-plan §4): each provider connection by channel, in failover order, with its targets. A new one is
 * created here and opened to add its targets.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatProgressBarModule, Badge, TenantLinkPipe, IntegrationForm],
  template: `
    <div class="na-head"><h1>Integrations</h1><span class="na-spacer"></span>
      @if (!creating()) { @if (can.configure()) { <button mat-flat-button type="button" (click)="creating.set(true)" [disabled]="!providers.value() || !integrations.hasValue()" data-testid="integration-new">
        New integration</button> } }
      <button mat-stroked-button type="button" (click)="refresh()" data-testid="refresh">Refresh</button></div>
    <!-- Only with the list read: a PUT creates or replaces, so a new key is checked against the tenant's. -->
    @if (creating() && integrations.hasValue() && providers.value(); as available) {
      <section class="na-card"><h2>New integration</h2>
        <na-integration-form [tenantId]="tenantId()" [providers]="available" [takenKeys]="keys()" (saved)="created($event)" (cancelled)="creating.set(false)" />
      </section>
    }
    @if (integrations.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (integrations.error() || providers.error()) { <p class="na-error" data-testid="integrations-error">{{ error() }}</p> }
    <table class="na-table" data-testid="integrations">
      <thead><tr><th>Integration</th><th>Channel</th><th>Provider</th><th>Priority</th><th>State</th><th>Targets</th><th>Secret reference</th><th>Updated</th></tr></thead>
      <tbody>
        @for (i of sorted(); track i.key) {
          <tr data-testid="integration-row" [attr.data-key]="i.key" [class.na-off]="!i.enabled">
            <td><a [routerLink]="['integrations', i.key] | naTenant: tenantId()">{{ i.key }}</a>
              @if (i.managedBy !== 'api') { <div class="na-muted na-small">{{ i.managedBy === 'config' ? 'from configuration' : "the service's own" }}</div> }</td>
            <td>{{ i.channel }}</td><td>{{ i.kind }}</td><td>{{ i.priority }}</td>
            <td><na-badge [state]="i.enabled ? 'enabled' : 'disabled'" /></td>
            <td>{{ i.targets.length }}</td>
            <td class="na-mono">{{ i.secretRef ?? '—' }}</td>
            <td class="na-nowrap">{{ i.updatedAt | date: 'MMM d, HH:mm' }}</td>
          </tr>
        } @empty { @if (!integrations.isLoading()) { <tr><td colspan="8" class="na-muted">No integrations yet.</td></tr> } }
      </tbody>
    </table>`,
})
export class IntegrationsPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  readonly tenantId = input.required<string>();
  /** The user's rights here: controls they may not use are hidden (the API refuses them anyway). */
  protected readonly can = tenantRights(() => this.tenantId());

  protected readonly integrations = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.integrations(params) });
  protected readonly providers = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.providers(params) });
  protected readonly creating = signal(false);
  protected readonly keys = computed(() => (this.integrations.value() ?? []).map(i => i.key));
  /** By channel, then in the order the dispatcher tries them. */
  protected readonly sorted = computed(() => [...(this.integrations.value() ?? [])]
    .sort((a, b) => a.channel.localeCompare(b.channel) || a.priority - b.priority || a.key.localeCompare(b.key)));
  protected readonly error = computed(() => problemText(this.integrations.error() ?? this.providers.error()));

  constructor() {
    // Reused for another tenant: a form opened for the last one is closed.
    whenChanged(() => this.tenantId(), () => this.creating.set(false));
  }

  protected refresh(): void {
    this.integrations.reload();
    // New integration needs the providers: a read that failed is asked again.
    if (this.providers.error()) this.providers.reload();
  }

  protected created(integration: Integration): void {
    this.creating.set(false);
    void this.router.navigate([under(this.base, `tenants/${this.tenantId()}`), 'integrations', integration.key]);
  }
}
