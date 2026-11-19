import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';
import { of, switchMap } from 'rxjs';
import { AccessService, EMPTY_ACCESS } from '../core/access.service';
import { NoAccessReason } from '../core/guards';
import { NOTIFICATIONS_ADMIN_OPTIONS, normalizeBasePath } from '../core/options';

/**
 * Where the guards send someone with nothing to open here (phase-4c-plan p4c-d3), saying why: the access couldn't be read; the tenant in a
 * link isn't theirs (or isn't a tenant id); or they administer no tenant at all (a platform admin: no tenant has a linked application yet).
 * "Try again" reads the access afresh and goes back to the link they opened. The Console is for administration: a person's own
 * notifications are in their product's bell.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  styles: `:host { display: block; max-width: 640px; padding: 32px 16px; }`,
  template: `
    <h1>No access here</h1>
    @switch (shown()) {
      @case ('unavailable') {
        <p data-testid="no-access-unavailable">What you may administer couldn't be read just now.</p>
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
    }
    <button mat-stroked-button type="button" (click)="tryAgain()" data-testid="no-access-retry">Try again</button>`,
})
export class NoAccessPage {
  private readonly access = inject(AccessService);
  private readonly router = inject(Router);
  private readonly base = normalizeBasePath(inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath);
  /** The guard's reason, and the link the user opened (query parameters). */
  readonly reason = input<NoAccessReason>();
  readonly returnUrl = input<string>();

  // Read only for "nothing to administer", where the answer picks the text: the other reasons name it already.
  private readonly answer = toSignal(toObservable(this.reason).pipe(switchMap(r => r === 'none' || !r ? this.access.load() : of(EMPTY_ACCESS))),
    { initialValue: EMPTY_ACCESS });
  protected readonly shown = computed(() => {
    const reason = this.reason();
    if (reason === 'unavailable' || reason === 'tenant') return reason;
    return this.answer().allTenants ? 'platform' : 'none';
  });

  /** The access afresh (a tenant granted meanwhile, the API back), then the link opened; its guards decide again. */
  protected tryAgain(): void {
    this.access.refresh();
    const back = this.returnUrl();
    // Only a path of this app (a link can't send the user elsewhere), never this page again.
    const safe = back && back.startsWith('/') && !back.startsWith('//') && !back.split('?')[0].endsWith('/no-access') ? back : this.base;
    void this.router.navigateByUrl(safe);
  }
}
