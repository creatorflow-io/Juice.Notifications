import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, shareReplay } from 'rxjs';
import { NOTIFICATIONS_ADMIN_OPTIONS } from './options';

/** `GET /api/v1/access`: what the signed-in user may administer (phase-4c-plan I50). */
export interface Access {
  platformAdmin: boolean;
  tenants: AccessTenant[];
}

export interface AccessTenant {
  id: string;
  /** The tenant's name when the deployment knows it; null otherwise (the id is shown). */
  name: string | null;
}

const NONE: Access = { platformAdmin: false, tenants: [] };
const CURRENT_KEY = 'notifications-admin.tenant';

/**
 * What the user may administer, read once per session from the API (never from the token: the API decides through `ITenantAccess`), and
 * the tenant they're working in. The menu links to tenant-less paths; this is the tenant they lead to.
 */
@Injectable({ providedIn: 'root' })
export class AccessService {
  private readonly http = inject(HttpClient);
  private readonly options = inject(NOTIFICATIONS_ADMIN_OPTIONS);
  private readonly current = signal<string | null>(readStored());

  private cached: Observable<Access> | null = null;

  /**
   * The access, read once and kept; none when it can't be read (signed out, refused, the API down), and then not kept, so the next
   * navigation asks again (a guard may run before sign-in completes).
   */
  load(): Observable<Access> {
    if (!this.cached) {
      const request = this.http.get<Access>(`${this.options.apiBase}/api/v1/access`).pipe(
        map(a => ({ platformAdmin: !!a?.platformAdmin, tenants: Array.isArray(a?.tenants) ? a.tenants : [] }) satisfies Access),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
      this.cached = request.pipe(catchError(() => {
        this.cached = null;
        return of(NONE);
      }));
    }
    return this.cached;
  }

  /** The tenant being worked in: the last one opened, if the user still administers it. */
  readonly tenantId = computed(() => this.current());

  /** Remembers the tenant opened (for the menu's links and the next visit). */
  use(tenantId: string): void {
    this.current.set(tenantId);
    try {
      sessionStorage.setItem(CURRENT_KEY, tenantId);
    } catch {
      // Storage refused (a private window): the tenant is kept for this page's life only.
    }
  }

  /** The tenant to open: the current one if allowed, else the first allowed; null when there is none. */
  pick(access: Access): string | null {
    const current = this.current();
    if (current && access.tenants.some(t => sameId(t.id, current))) {
      return access.tenants.find(t => sameId(t.id, current))!.id;
    }
    return access.tenants[0]?.id ?? null;
  }
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
