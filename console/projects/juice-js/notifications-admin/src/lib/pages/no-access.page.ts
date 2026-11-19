import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AccessService } from '../core/access.service';

/**
 * For someone signed in who administers no tenant here (phase-4c-plan p4c-d3). The Console is for administration: a person's own
 * notifications are in their product's bell. A platform admin lands here only while no tenant has a linked application.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `:host { display: block; max-width: 640px; padding: 32px 16px; }`,
  template: `
    <h1>No access here</h1>
    @if (access().platformAdmin) {
      <p data-testid="no-access-platform">No tenant has a linked application yet. Register an application and link it to a tenant; its
        notifications are then administered here.</p>
    } @else {
      <p data-testid="no-access">Your account doesn't administer the notifications of any tenant. Your own notifications are in the bell of
        the product you use; to administer a tenant's, ask its administrator for the tenant admin role.</p>
    }`,
})
export class NoAccessPage {
  protected readonly access = toSignal(inject(AccessService).load(), { initialValue: { platformAdmin: false, tenants: [] } });
}
