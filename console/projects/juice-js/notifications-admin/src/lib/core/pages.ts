/**
 * The admin page ids per hosting mode (phase-5 M44; m44-architecture §3.4, §7), in menu order. They equal the API's `AdminPages`, and are
 * reviewed against the fixture its tests compare with (test/Juice.Notifications.IntegrationTests/Fixtures/admin-pages.json). A detail page
 * belongs to its list page's id.
 */
export const NOTIFICATIONS_ADMIN_PAGES = {
  service: [
    'overview', 'deliveries', 'notifications', 'correlations', 'storms', 'integrations', 'routes', 'mutes', 'escalation-policies', 'allowlist',
    'types', 'display-groups', 'subscribers', 'groups', 'topics', 'resolvers', 'settings', 'applications',
  ],
  embedded: [
    'overview', 'deliveries', 'notifications', 'correlations', 'storms', 'integrations', 'routes', 'mutes', 'escalation-policies', 'allowlist',
    'types', 'settings',
  ],
} as const satisfies { service: readonly string[]; embedded: readonly string[] };

/** How the notifications are hosted: the centralized service, or embedded in a product's own host (one tenant, no people). */
export type NotificationsMode = 'service' | 'embedded';

/** What a caller may do in a tenant (p5-d6): read pages and history, operate (retry, replay), configure (every change). */
export type AdminRight = 'read' | 'operate' | 'configure';
