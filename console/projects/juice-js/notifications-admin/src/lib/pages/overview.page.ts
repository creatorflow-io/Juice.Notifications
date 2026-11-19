import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A tenant's home. Its counts (deliveries by status and channel, failing integrations, open storms, unfinished requests) come with the
 * overview read, phase-4c-plan M37; until then it names the tenant the pages in the menu act on.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Overview</h1>
    <p data-testid="overview-tenant">Notifications of tenant <code>{{ tenantId() }}</code>.</p>`,
})
export class OverviewPage {
  readonly tenantId = input.required<string>();
}
