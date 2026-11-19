import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AccessService, EMPTY_ACCESS } from '../core/access.service';
import { NoAccessReason } from '../core/guards';

/**
 * Where the guards send someone with nothing to open here (phase-4c-plan p4c-d3), saying why: the access couldn't be read; the tenant in a
 * link isn't theirs (or isn't a tenant id); or they administer no tenant at all (a platform admin: no tenant has a linked application yet).
 * The Console is for administration: a person's own notifications are in their product's bell.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `:host { display: block; max-width: 640px; padding: 32px 16px; }`,
  template: `
    <h1>No access here</h1>
    @switch (shown()) {
      @case ('unavailable') {
        <p data-testid="no-access-unavailable">What you may administer couldn't be read just now. Reload the page to try again.</p>
      }
      @case ('tenant') {
        <p data-testid="no-access-tenant">The link names a tenant you don't administer, or isn't a tenant's link.</p>
      }
      @case ('platform') {
        <p data-testid="no-access-platform">No tenant has a linked application yet. Register an application and link it to a tenant; its
          notifications are then administered here.</p>
      }
      @default {
        <p data-testid="no-access">Your account doesn't administer the notifications of any tenant. Your own notifications are in the bell of
          the product you use; to administer a tenant's, ask its administrator for the tenant admin role.</p>
      }
    }`,
})
export class NoAccessPage {
  protected readonly access = toSignal(inject(AccessService).load(), { initialValue: EMPTY_ACCESS });
  /** The guard's reason (a query parameter). */
  readonly reason = input<NoAccessReason>();

  protected shown(): 'unavailable' | 'tenant' | 'platform' | 'none' {
    const reason = this.reason();
    if (reason === 'unavailable' || reason === 'tenant') return reason;
    return this.access().allTenants ? 'platform' : 'none';
  }
}
