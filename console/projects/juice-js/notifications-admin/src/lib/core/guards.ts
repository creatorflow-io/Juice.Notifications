import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs';
import { Access, AccessService } from './access.service';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from './options';

/**
 * Why the no-access page is shown: nothing to administer, not the user's tenant (or not a tenant id), or the access couldn't be read; or,
 * from the API's tenant refusals, a tenant the deployment doesn't know, one not active, or one still starting.
 */
export type NoAccessReason = 'none' | 'tenant' | 'unavailable' | 'unknown' | 'inactive' | 'starting';

/** The no-access page, saying why, with the link the user opened (its "try again" goes back there). */
function noAccess(router: Router, base: string, access: Access, reason: NoAccessReason, returnUrl: string): UrlTree {
  const tree = router.parseUrl(under(base, 'no-access'));
  tree.queryParams = { reason: access.unavailable ? 'unavailable' : reason, returnUrl };
  return tree;
}

/** The Console's home: the tenant being worked in (or the first the user administers), else the no-access page. */
export const homeGuard: CanActivateFn = (_, state) => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  return access.load().pipe(map(a => {
    const tenant = access.pick(a);
    return tenant ? router.parseUrl(under(base, `tenants/${tenant}`)) : noAccess(router, base, a, 'none', state.url);
  }));
};

/**
 * A menu link (`go/deliveries`): the same page in the tenant being worked in. The menu is one list for every tenant, so its links name no
 * tenant; the URL a page ends up on always does (phase-4c-plan p4c-d2).
 */
export const goGuard: CanActivateFn = (route, state) => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  // The wildcard under `go`: its segments are the page's path.
  const page = route.url.map(s => s.path).join('/');
  return access.load().pipe(map(a => {
    const tenant = access.pick(a);
    if (!tenant) return noAccess(router, base, a, 'none', state.url);
    const tree = router.parseUrl(under(base, `tenants/${tenant}/${page}`));
    tree.queryParams = route.queryParams;
    return tree;
  }));
};

/** A tenant's pages: only one the user administers (the API refuses the rest anyway); it becomes the tenant being worked in. */
export const tenantGuard: CanActivateFn = (route, state) => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  return access.load().pipe(map(a => {
    const tenant = access.resolve(a, route.paramMap.get('tenantId'));
    if (!tenant) return noAccess(router, base, a, 'tenant', state.url);
    access.use(tenant);
    return true;
  }));
};
