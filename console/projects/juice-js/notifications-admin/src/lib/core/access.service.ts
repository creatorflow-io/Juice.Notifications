import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { Observable, catchError, filter, map, of, shareReplay, switchMap, take, tap } from 'rxjs';
import { NOTIFICATIONS_ADMIN_OPTIONS } from './options';

/**
 * `GET /api/v1/access`: what the signed-in user may administer (phase-4c-plan I50). The tenants come from the API's `ITenantAccess`, never
 * from the token: `allTenants` (every tenant; `tenants` lists the ones the service knows, to pick from) or exactly `tenants`.
 * `platformAdmin` is the role the applications API requires.
 */
export interface Access {
  platformAdmin: boolean;
  allTenants: boolean;
  tenants: AccessTenant[];
}

export interface AccessTenant {
  id: string;
  /** The tenant's name when the deployment knows it; null otherwise (the id is shown). */
  name: string | null;
}

const NONE: Access = { platformAdmin: false, allTenants: false, tenants: [] };
const CURRENT_KEY = 'notifications-admin.tenant';
/** How long a granting answer is kept: a tenant granted or linked meanwhile shows within this. */
const KEEP_MS = 5 * 60 * 1000;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * What the user may administer, and the tenant they're working in. The menu links to tenant-less paths; this is the tenant they lead to.
 * The read waits for a valid access token, so a guard that runs beside sign-in never asks without one.
 */
@Injectable({ providedIn: 'root' })
export class AccessService {
  private readonly http = inject(HttpClient);
  private readonly options = inject(NOTIFICATIONS_ADMIN_OPTIONS);
  private readonly oauth = inject(OAuthService, { optional: true });
  private readonly current = signal<string | null>(readStored());
  private cached: { access: Observable<Access>; until: number } | null = null;

  /** The tenant being worked in: the last one opened. */
  readonly tenantId = this.current.asReadonly();

  /**
   * The access. An answer that grants something is kept a few minutes; one that grants nothing, or a failed read (refused, the API down),
   * isn't, so the next navigation asks again (a tenant may have been granted or linked).
   */
  load(): Observable<Access> {
    if (this.cached && this.cached.until > Date.now()) {
      return this.cached.access;
    }
    const until = Date.now() + KEEP_MS;
    const access = this.signedIn().pipe(
      switchMap(() => this.http.get<Partial<Access>>(`${this.options.apiBase}/api/v1/access`)),
      map(a => ({ platformAdmin: !!a?.platformAdmin, allTenants: !!a?.allTenants, tenants: Array.isArray(a?.tenants) ? a.tenants : [] }) satisfies Access),
      tap(a => { if (!grants(a)) this.forget(access); }),
      catchError(() => {
        this.forget(access);
        return of(NONE);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.cached = { access, until };
    return access;
  }

  /** Remembers the tenant opened (for the menu's links and the next visit). */
  use(tenantId: string): void {
    this.current.set(tenantId);
    try {
      sessionStorage.setItem(CURRENT_KEY, tenantId);
    } catch {
      // Storage refused (a private window): the tenant is kept for this page's life only.
    }
  }

  /** The tenant to open: the current one if allowed, else the first listed; null when there is none. */
  pick(access: Access): string | null {
    const current = this.current();
    const listed = current ? access.tenants.find(t => sameId(t.id, current)) : undefined;
    if (listed) return listed.id;
    if (current && access.allTenants && GUID.test(current)) return current;
    return access.tenants[0]?.id ?? null;
  }

  /** Whether the user administers `tenantId`: listed, or every tenant (any GUID: the API answers for it). */
  allows(access: Access, tenantId: string | null | undefined): boolean {
    return !!tenantId && (access.tenants.some(t => sameId(t.id, tenantId)) || (access.allTenants && GUID.test(tenantId)));
  }

  private forget(access: Observable<Access>): void {
    if (this.cached?.access === access) this.cached = null;
  }

  /** Done once there is a valid access token (now, or when sign-in or a refresh brings one). Without OAuth configured, at once. */
  private signedIn(): Observable<unknown> {
    const oauth = this.oauth;
    if (!oauth || oauth.hasValidAccessToken()) return of(true);
    return oauth.events.pipe(filter(() => oauth.hasValidAccessToken()), take(1));
  }
}

/** Whether an answer grants anything to administer. */
function grants(a: Access): boolean {
  return a.allTenants || a.tenants.length > 0;
}

/** Tenant ids compare as GUIDs: case doesn't matter. */
export function sameId(a: string | null | undefined, b: string | null | undefined): boolean {
  return !!a && !!b && a.toLowerCase() === b.toLowerCase();
}

function readStored(): string | null {
  try {
    return sessionStorage.getItem(CURRENT_KEY);
  } catch {
    return null;
  }
}
