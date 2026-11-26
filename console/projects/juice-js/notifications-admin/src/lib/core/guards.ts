import { inject } from '@angular/core';
import { CanActivateChildFn, CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs';
import { Access, AccessService, hasPage } from './access.service';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from './options';

/**
 * Why the no-access page is shown: nothing to administer, not the user's tenant (or not a tenant id), the platform's pages without being a
 * platform admin, or the access couldn't be read; or, from the API's tenant refusals, a tenant the deployment doesn't know, one not active,
 * or one still starting; or a page this deployment doesn't have (`page`: an embedded host has no people pages).
 */
export type NoAccessReason = 'none' | 'tenant' | 'platform-only' | 'unavailable' | 'unknown' | 'inactive' | 'starting' | 'page';

/** The no-access page, saying why, with the link the user opened (its "try again" goes back there). */
function noAccess(router: Router, base: string, access: Access, reason: NoAccessReason, returnUrl: string): UrlTree {
  const tree = router.parseUrl(under(base, 'no-access'));
  tree.queryParams = { reason: access.unavailable ? 'unavailable' : reason, returnUrl };
  return tree;
}

/**
 * The Console's home: the tenant being worked in (or the first the user administers); for a platform admin with none yet (no tenant has a
 * linked application), the applications, where one is linked; else the no-access page.
 */
export const homeGuard: CanActivateFn = (_, state) => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  return access.load().pipe(map(a => {
    const tenant = access.pick(a);
    if (tenant) return router.parseUrl(under(base, `tenants/${tenant}`));
    return a.platformAdmin ? router.parseUrl(under(base, 'applications')) : noAccess(router, base, a, 'none', state.url);
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

/** The platform's pages (applications): platform admins only, as the API requires. */
export const platformGuard: CanActivateFn = (_, state) => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  return access.load().pipe(map(a => a.platformAdmin || noAccess(router, base, a, 'platform-only', state.url)));
};

/**
 * A tenant page the deployment has (phase 5, M44): its route's `data.page` (an id of `NOTIFICATIONS_ADMIN_PAGES`) must be in the access
 * read's `pages`, else the no-access page says it isn't part of this deployment. The menu shows only those pages; this covers a link.
 */
export const pageGuard: CanActivateChildFn = (route, state) => {
  const page = route.data['page'] as string | undefined;
  if (!page) return true;
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  return access.load().pipe(map(a => a.unavailable ? noAccess(router, base, a, 'unavailable', state.url)
    : hasPage(a, page) || noAccess(router, base, a, 'page', state.url)));
};
