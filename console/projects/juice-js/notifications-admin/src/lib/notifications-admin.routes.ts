import { Routes } from '@angular/router';
import { Menus } from '@juice-js/core';
import { goGuard, homeGuard, tenantGuard } from './core/guards';
import { under } from './core/options';

function page(path: string, title: string, load: Routes[number]['loadComponent']): Routes[number] {
  return { path, title: `${title} · Juice Notifications`, loadComponent: load };
}

/**
 * The Console's pages (phase-4c-plan §4). A tenant's pages sit under `tenants/:tenantId`; the home and the menu's `go/…` links lead to the
 * tenant being worked in. The host mounts them at its `basePath` (see {@link provideNotificationsAdmin}), adds sign-in in front of each,
 * and routes with `paramsInheritanceStrategy: 'always'` and `bindToComponentInputs` (pages read the tenant id as an input).
 */
export const NOTIFICATIONS_ADMIN_ROUTES: Routes = [
  { path: '', pathMatch: 'full', canActivate: [homeGuard], children: [] },
  page('no-access', 'No access', () => import('./pages/no-access.page').then(m => m.NoAccessPage)),
  { path: 'go', children: [{ path: '**', canActivate: [goGuard], children: [] }] },
  {
    path: 'tenants/:tenantId',
    canActivate: [tenantGuard],
    loadComponent: () => import('./pages/tenant-shell.page').then(m => m.TenantShellPage),
    children: [
      page('', 'Overview', () => import('./pages/overview.page').then(m => m.OverviewPage)),
      page('deliveries', 'Deliveries', () => import('./pages/deliveries.page').then(m => m.DeliveriesPage)),
      page('deliveries/:id', 'Delivery', () => import('./pages/delivery.page').then(m => m.DeliveryPage)),
      page('notifications', 'Notifications', () => import('./pages/notifications.page').then(m => m.NotificationsPage)),
      page('notifications/:id', 'Notification', () => import('./pages/notification.page').then(m => m.NotificationPage)),
      page('correlations', 'Correlations', () => import('./pages/correlations.page').then(m => m.CorrelationsPage)),
      // Keys are per application: the application, then the key (a slash in it encoded in its segment).
      page('correlations/:application/:key', 'Correlation', () => import('./pages/correlation.page').then(m => m.CorrelationPage)),
      page('storms', 'Storms', () => import('./pages/storms.page').then(m => m.StormsPage)),
    ],
  },
];

/**
 * The side menu (juice-layout), linking under `basePath`. Tenant pages link through `go/…`, so one menu serves every tenant. Investigation
 * first; configuration grouped after (phase-4c-plan §4). Each milestone adds its pages here.
 */
export function notificationsAdminMenus(basePath = '/', order = 1): Menus {
  const menus: Menus = [
    { id: 'overview', label: 'Overview', icon: 'dashboard', routerLink: under(basePath, ''), shortcut: 'g o' },
    { id: 'deliveries', label: 'Deliveries', icon: 'outbox', routerLink: goLink(basePath, 'deliveries'), shortcut: 'g d' },
    { id: 'notifications', label: 'Notifications', icon: 'notifications', routerLink: goLink(basePath, 'notifications'), shortcut: 'g n' },
    { id: 'correlations', label: 'Correlations', icon: 'link', routerLink: goLink(basePath, 'correlations'), shortcut: 'g c' },
    { id: 'storms', label: 'Storms', icon: 'thunderstorm', routerLink: goLink(basePath, 'storms') },
  ];
  return menus.map((menu, index) => ({ ...menu, order: order + index }));
}

/** The menu's link to a tenant page: `go(base, 'deliveries')` → `/go/deliveries`. */
export function goLink(basePath: string, page: string): string {
  return under(basePath, `go/${page}`);
}
