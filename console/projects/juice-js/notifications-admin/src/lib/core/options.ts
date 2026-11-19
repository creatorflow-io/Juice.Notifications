import { EnvironmentProviders, InjectionToken, makeEnvironmentProviders } from '@angular/core';

/** How the host app mounts the Notifications Console pages. */
export interface NotificationsAdminOptions {
  /** The Juice.Notifications API origin; empty when the API serves the app (same origin). */
  apiBase: string;
  /** Where the host mounts {@link NOTIFICATIONS_ADMIN_ROUTES}: `/` for the whole app, or a path such as `/notifications`. */
  basePath: string;
}

export const NOTIFICATIONS_ADMIN_OPTIONS = new InjectionToken<NotificationsAdminOptions>('NOTIFICATIONS_ADMIN_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ apiBase: '', basePath: '/' }),
});

/** Configures the pages: where the API is and where the host mounted the routes. */
export function provideNotificationsAdmin(options: Partial<NotificationsAdminOptions> = {}): EnvironmentProviders {
  return makeEnvironmentProviders([
    {
      provide: NOTIFICATIONS_ADMIN_OPTIONS,
      useValue: { apiBase: options.apiBase ?? '', basePath: options.basePath ?? '/' } satisfies NotificationsAdminOptions,
    },
  ]);
}

/** `/notifications/` → `/notifications`; `` and `/` → `/`. */
export function normalizeBasePath(basePath: string): string {
  const trimmed = basePath.trim().replace(/^\/+|\/+$/g, '');
  return trimmed ? `/${trimmed}` : '/';
}

/** A path under the mount: `under('/', 'tenants')` → `/tenants`; `under('/n', '')` → `/n`. */
export function under(basePath: string, path: string): string {
  const base = normalizeBasePath(basePath);
  return base === '/' ? `/${path}` : path ? `${base}/${path}` : base;
}
