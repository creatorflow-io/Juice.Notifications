import { KEY_PATTERN } from '../../projects/juice-js/notifications-admin/src/lib/shared/forms';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import {
  Access, AccessService, NOTIFICATIONS_ADMIN_OPTIONS, NOTIFICATIONS_ADMIN_PAGES, notificationsAdminMenus, pageGuard, readAccess, tenantRights,
} from '@juice-js/notifications-admin';
import { Observable, firstValueFrom, isObservable, of } from 'rxjs';

/** The library's mode-following parts (phase 5, M44-4): the menu per mode, the page guard, and an older access answer read as the service. */
describe('notifications admin, per hosting mode', () => {
  const embedded: Access = readAccess({ platformAdmin: false, allTenants: false, tenants: [], mode: 'embedded', pages: NOTIFICATIONS_ADMIN_PAGES.embedded });

  it('keeps every item without pages, and drops the people and display groups with the embedded pages, keeping the order', () => {
    const all = notificationsAdminMenus('/notifications', 1);
    const ids = (menus: ReturnType<typeof notificationsAdminMenus>) => menus.map(m => m.children?.length ? [m.id, m.children.map(c => c.id)] : m.id);
    expect(all.map(m => m.id)).toContain('notifications-admin-people');
    expect(all.find(m => m.id === 'notifications-admin-content')?.children?.map(c => c.id)).toEqual(['types', 'display-groups']);

    const menu = notificationsAdminMenus('/notifications', 10, NOTIFICATIONS_ADMIN_PAGES.embedded);
    expect(ids(menu)).toEqual([
      'overview', 'deliveries', 'notifications', 'correlations', 'storms',
      ['notifications-admin-delivery', ['integrations', 'routes', 'mutes', 'escalation-policies', 'allowlist']],
      ['notifications-admin-content', ['types']],
      'settings',
    ]);
    // Orders follow on, and a group links to its first page left.
    expect(menu.map(m => m.order)).toEqual([10, 11, 12, 13, 14, 15, 16, 17]);
    expect(menu.find(m => m.id === 'notifications-admin-content')?.routerLink).toBe('/notifications/go/types');
  });

  it('reads an answer without a mode, pages or rights as the service with every page and right', () => {
    const old = readAccess({ platformAdmin: true, allTenants: false, tenants: [{ id: 't1', name: null } as never] });
    expect(old.mode).toBe('service');
    expect(old.pages).toEqual(NOTIFICATIONS_ADMIN_PAGES.service);
    expect(old.tenants[0].rights).toEqual(['read', 'operate', 'configure']);
    expect(readAccess(null)).toMatchObject({ mode: 'service', tenants: [] });
  });

  it('sends a page the deployment lacks to no-access with the page reason, and lets the others through', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: NOTIFICATIONS_ADMIN_OPTIONS, useValue: { apiBase: '', basePath: '/notifications' } },
        { provide: AccessService, useValue: { load: () => of(embedded) } },
      ],
    });
    const run = async (page: string | undefined) => {
      const route = { data: page ? { page } : {} } as unknown as ActivatedRouteSnapshot;
      const state = { url: `/notifications/tenants/t1/${page ?? ''}` } as RouterStateSnapshot;
      const result = TestBed.runInInjectionContext(() => pageGuard(route, state));
      return isObservable(result) ? await firstValueFrom(result as Observable<boolean | UrlTree>) : result;
    };

    const refused = await run('subscribers');
    expect(refused).toBeInstanceOf(UrlTree);
    const router = TestBed.inject(Router);
    expect(router.serializeUrl(refused as UrlTree)).toBe('/notifications/no-access?reason=page&returnUrl=%2Fnotifications%2Ftenants%2Ft1%2Fsubscribers');
    expect(await run('routes')).toBe(true);
    expect(await run(undefined)).toBe(true);
  });

  it('reads the rights of the tenant the page shows, every right under allTenants, and none before the access is read', () => {
    const read = (a: Access | undefined, tenant: string) => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [{ provide: AccessService, useValue: { load: () => (a ? of(a) : new Observable<Access>()) } }] });
      return TestBed.runInInjectionContext(() => tenantRights(() => tenant));
    };
    const operator = readAccess({ platformAdmin: false, allTenants: false, mode: 'embedded', pages: NOTIFICATIONS_ADMIN_PAGES.embedded,
      tenants: [{ id: 'T1', name: null, rights: ['read', 'operate'] }] });
    const rights = read(operator, 't1');
    expect([rights.configure(), rights.operate()]).toEqual([false, true]);
    const other = read(operator, 't2');
    expect([other.configure(), other.operate()]).toEqual([false, false]);
    const platform = read(readAccess({ platformAdmin: true, allTenants: true, tenants: [] }), 'any');
    expect([platform.configure(), platform.operate()]).toEqual([true, true]);
    const unread = read(undefined, 't1');
    expect([unread.configure(), unread.operate()]).toEqual([false, false]);
  });

  it('gives inputs key patterns a browser compiles (the v flag: a bare - in a class is a syntax error and the pattern is dropped)', () => {
    expect(() => new RegExp(`^(?:${KEY_PATTERN})$`, 'v')).not.toThrow();
    expect(['a', 'e2e-hooks'].every(k => new RegExp(`^(?:${KEY_PATTERN})$`, 'v').test(k))).toBe(true);
    expect(['E2e', '2x', 'a_b'].some(k => new RegExp(`^(?:${KEY_PATTERN})$`, 'v').test(k))).toBe(false);
  });
});
