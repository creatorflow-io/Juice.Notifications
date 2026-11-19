import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AccessService, sameId } from './access.service';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from './options';

/** The Console's home: the tenant being worked in (or the first the user administers), else the no-access page. */
export const homeGuard: CanActivateFn = () => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  return access.load().pipe(map(a => {
    const tenant = access.pick(a);
    return router.parseUrl(tenant ? under(base, `tenants/${tenant}`) : under(base, 'no-access'));
  }));
};

/**
 * A menu link (`go/deliveries`): the same page in the tenant being worked in. The menu is one list for every tenant, so its links name no
 * tenant; the URL a page ends up on always does (phase-4c-plan p4c-d2).
 */
export const goGuard: CanActivateFn = route => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  // The wildcard under `go`: its segments are the page's path.
  const page = route.url.map(s => s.path).join('/');
  return access.load().pipe(map(a => {
    const tenant = access.pick(a);
    const tree = router.parseUrl(tenant ? under(base, `tenants/${tenant}/${page}`) : under(base, 'no-access'));
    tree.queryParams = route.queryParams;
    return tree;
  }));
};

/** A tenant's pages: only one the user administers (the API refuses the rest anyway); it becomes the tenant being worked in. */
export const tenantGuard: CanActivateFn = route => {
  const access = inject(AccessService);
  const router = inject(Router);
  const base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  const id = route.paramMap.get('tenantId');
  return access.load().pipe(map(a => {
    const tenant = a.tenants.find(t => sameId(t.id, id));
    if (!tenant) {
      return router.parseUrl(under(base, 'no-access'));
    }
    access.use(tenant.id);
    return true;
  }));
};
