import { Signal, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AccessService, sameId } from '../core/access.service';
import { AdminRight } from '../core/pages';

/** What the signed-in user may do in a tenant's pages (p5-d6): change its setup, and retry or replay its deliveries. */
export interface TenantRights {
  readonly configure: Signal<boolean>;
  readonly operate: Signal<boolean>;
}

/**
 * The user's rights in the tenant <paramref name="tenantId"/> names, from the access read (phase 5, M48; m44-architecture §11 Q6): its
 * <c>rights</c>; every right for a tenant opened under <c>allTenants</c> (a platform admin); none until the access is read. Pages hide the
 * controls the user may not use; the API refuses them anyway (<c>not_allowed</c>). Call in an injection context (a field initializer).
 */
export function tenantRights(tenantId: () => string): TenantRights {
  const access = toSignal(inject(AccessService).load());
  const rights = computed<ReadonlySet<AdminRight>>(() => {
    const a = access();
    if (!a) return new Set();
    const tenant = a.tenants.find(t => sameId(t.id, tenantId()));
    return new Set(tenant ? tenant.rights : a.allTenants ? ['read', 'operate', 'configure'] : []);
  });
  return { configure: computed(() => rights().has('configure')), operate: computed(() => rights().has('operate')) };
}
