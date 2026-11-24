import { HTTP_INTERCEPTORS } from '@angular/common/http';
import { EnvironmentProviders, InjectionToken, makeEnvironmentProviders } from '@angular/core';
import { Observable } from 'rxjs';
import { TenantRefusalInterceptor } from './tenant-refusal.interceptor';

/** How the host app mounts the Notifications Console pages. */
export interface NotificationsAdminOptions {
  /** The Juice.Notifications API origin; empty when the API serves the app (same origin). */
  apiBase: string;
  /** Where the host mounts {@link NOTIFICATIONS_ADMIN_ROUTES}: `/` for the whole app, or a path such as `/notifications`. */
  basePath: string;
}

/** What {@link provideNotificationsAdmin} takes: the options, and how the pages know the user is signed in. */
export interface NotificationsAdminSetup extends Partial<NotificationsAdminOptions> {
  /**
   * Called once, in an injection context (it may `inject()` the host's sign-in service). Returns a cold observable that emits, on each
   * subscription, the signed-in user's id (the token's subject) once there is a token the API accepts: at once when there is one, else
   * when sign-in or a refresh brings it. The pages read nothing before, and read again when the user changes. Required: without it a page
   * would ask the API before sign-in completes, be refused, and say the access couldn't be read.
   */
  signedIn: () => Observable<string>;
}

export const NOTIFICATIONS_ADMIN_OPTIONS = new InjectionToken<NotificationsAdminOptions>('NOTIFICATIONS_ADMIN_OPTIONS', {
  providedIn: 'root',
  factory: () => ({ apiBase: '', basePath: '/' }),
});

/** The signed-in user's id, as {@link NotificationsAdminSetup.signedIn} gives it. No default: a host must say how sign-in is known. */
export const NOTIFICATIONS_ADMIN_SIGNED_IN = new InjectionToken<Observable<string>>('NOTIFICATIONS_ADMIN_SIGNED_IN');

/**
 * Configures the pages: where the API is, where the host mounted the routes, and how they know who is signed in. Also registers the
 * interceptor that sends a refused tenant (unknown, not active) to the no-access page: the host's HttpClient takes DI interceptors
 * (`provideHttpClient(withInterceptorsFromDi())`).
 */
export function provideNotificationsAdmin(setup: NotificationsAdminSetup): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: HTTP_INTERCEPTORS, useClass: TenantRefusalInterceptor, multi: true },
    {
      provide: NOTIFICATIONS_ADMIN_OPTIONS,
      useValue: { apiBase: setup.apiBase ?? '', basePath: setup.basePath ?? '/' } satisfies NotificationsAdminOptions,
    },
    { provide: NOTIFICATIONS_ADMIN_SIGNED_IN, useFactory: setup.signedIn },
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
