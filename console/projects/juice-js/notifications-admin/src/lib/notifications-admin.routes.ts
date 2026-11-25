import { Routes } from '@angular/router';
import { Menus } from '@juice-js/core';
import { goGuard, homeGuard, platformGuard, tenantGuard } from './core/guards';
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
  // The platform's, outside any tenant: platform admins only (reached from the tenant bar, or the home when no tenant is linked yet).
  { ...page('applications', 'Applications', () => import('./pages/applications.page').then(m => m.ApplicationsPage)), canActivate: [platformGuard] },
  { ...page('applications/:key', 'Application', () => import('./pages/application.page').then(m => m.ApplicationPage)), canActivate: [platformGuard] },
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
      page('integrations', 'Integrations', () => import('./pages/integrations.page').then(m => m.IntegrationsPage)),
      page('integrations/:key', 'Integration', () => import('./pages/integration.page').then(m => m.IntegrationPage)),
      page('routes', 'Routes', () => import('./pages/routes.page').then(m => m.RoutesPage)),
      page('mutes', 'Mutes', () => import('./pages/mutes.page').then(m => m.MutesPage)),
      page('escalation-policies', 'Escalation policies', () => import('./pages/escalation-policies.page').then(m => m.EscalationPoliciesPage)),
      page('allowlist', 'Destination allowlist', () => import('./pages/allowlist.page').then(m => m.AllowlistPage)),
      page('types', 'Types', () => import('./pages/types.page').then(m => m.TypesPage)),
      page('types/:key', 'Type', () => import('./pages/type.page').then(m => m.TypePage)),
      page('display-groups', 'Display groups', () => import('./pages/display-groups.page').then(m => m.DisplayGroupsPage)),
      page('subscribers', 'Subscribers', () => import('./pages/subscribers.page').then(m => m.SubscribersPage)),
      page('subscribers/:id', 'Subscriber', () => import('./pages/subscriber.page').then(m => m.SubscriberPage)),
      page('groups', 'Groups', () => import('./pages/groups.page').then(m => m.GroupsPage)),
      page('topics', 'Topics', () => import('./pages/topics.page').then(m => m.TopicsPage)),
      page('resolvers', 'Resolvers', () => import('./pages/resolvers.page').then(m => m.ResolversPage)),
      page('settings', 'Settings', () => import('./pages/settings.page').then(m => m.SettingsPage)),
    ],
  },
];

/**
 * The side menu (juice-layout), linking under `basePath`. Tenant pages link through `go/…`, so one menu serves every tenant. Investigation
 * first; configuration grouped after (phase-4c-plan §4). The platform's applications are not in it: the menu is the same for everyone and can't
 * follow the access read, so platform admins reach them from the tenant bar.
 */
export function notificationsAdminMenus(basePath = '/', order = 1): Menus {
  const menus: Menus = [
    { id: 'overview', label: 'Overview', icon: 'dashboard', routerLink: under(basePath, ''), shortcut: 'g o' },
    { id: 'deliveries', label: 'Deliveries', icon: 'outbox', routerLink: goLink(basePath, 'deliveries'), shortcut: 'g d' },
    { id: 'notifications', label: 'Notifications', icon: 'notifications', routerLink: goLink(basePath, 'notifications'), shortcut: 'g n' },
    { id: 'correlations', label: 'Correlations', icon: 'link', routerLink: goLink(basePath, 'correlations'), shortcut: 'g c' },
    { id: 'storms', label: 'Storms', icon: 'thunderstorm', routerLink: goLink(basePath, 'storms') },
    group('notifications-admin-delivery', 'Delivery', 'send', [
      { id: 'integrations', label: 'Integrations', icon: 'cable', routerLink: goLink(basePath, 'integrations'), shortcut: 'g i' },
      { id: 'routes', label: 'Routes', icon: 'alt_route', routerLink: goLink(basePath, 'routes'), shortcut: 'g r' },
      { id: 'mutes', label: 'Mutes', icon: 'notifications_paused', routerLink: goLink(basePath, 'mutes') },
      { id: 'escalation-policies', label: 'Escalation policies', icon: 'escalator_warning', routerLink: goLink(basePath, 'escalation-policies') },
      { id: 'allowlist', label: 'Destination allowlist', icon: 'shield', routerLink: goLink(basePath, 'allowlist') },
    ]),
    group('notifications-admin-content', 'Content', 'article', [
      { id: 'types', label: 'Types', icon: 'category', routerLink: goLink(basePath, 'types'), shortcut: 'g t' },
      { id: 'display-groups', label: 'Display groups', icon: 'tab', routerLink: goLink(basePath, 'display-groups') },
    ]),
    group('notifications-admin-people', 'People', 'groups', [
      { id: 'subscribers', label: 'Subscribers', icon: 'person', routerLink: goLink(basePath, 'subscribers'), shortcut: 'g s' },
      { id: 'groups', label: 'Groups', icon: 'group', routerLink: goLink(basePath, 'groups') },
      { id: 'topics', label: 'Topics', icon: 'campaign', routerLink: goLink(basePath, 'topics') },
      { id: 'resolvers', label: 'Resolvers', icon: 'person_search', routerLink: goLink(basePath, 'resolvers') },
    ]),
    { id: 'settings', label: 'Settings', icon: 'settings', routerLink: goLink(basePath, 'settings') },
  ];
  return menus.map((menu, index) => ({ ...menu, order: order + index }));
}

/**
 * A menu group, as Observability's: it links to its first page and keeps its pages' own links (`replaceChildRouterLink: false`); juice-layout
 * only opens and closes it on a click. Group ids carry a `notifications-admin-` prefix, as a host's own menu may sit beside them.
 */
function group(id: string, label: string, icon: string, children: Menus): Menus[number] {
  return { id, label, icon, routerLink: children[0].routerLink, replaceChildRouterLink: false, children };
}

/** The menu's link to a tenant page: `go(base, 'deliveries')` → `/go/deliveries`. */
export function goLink(basePath: string, page: string): string {
  return under(basePath, `go/${page}`);
}
