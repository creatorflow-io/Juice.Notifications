import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, map, of, shareReplay, switchMap, take, tap } from 'rxjs';
import { NOTIFICATIONS_ADMIN_OPTIONS, NOTIFICATIONS_ADMIN_SIGNED_IN } from './options';

/**
 * `GET /api/v1/access`: what the signed-in user may administer (phase-4c-plan I50). The tenants come from the API's `ITenantAccess`, never
 * from the token: `allTenants` (every tenant; `tenants` lists the ones the service knows, to pick from) or exactly `tenants`.
 * `platformAdmin` is the role the applications API requires. `unavailable` (the Console's own): the read failed, so nothing is known.
 */
export interface Access {
  platformAdmin: boolean;
  allTenants: boolean;
  tenants: AccessTenant[];
  unavailable?: boolean;
}

export interface AccessTenant {
  id: string;
  /** The tenant's name when the deployment knows it; null otherwise (the id is shown). */
  name: string | null;
}

/** Nothing to administer: the pages' value until the access is read. */
export const EMPTY_ACCESS: Access = { platformAdmin: false, allTenants: false, tenants: [] };

const CURRENT_KEY = 'notifications-admin.tenant';
/** How long an answer is kept: one that grants (a tenant granted or linked meanwhile shows within this), one that grants nothing. */
const KEEP_GRANTING_MS = 5 * 60 * 1000;
const KEEP_NOTHING_MS = 30 * 1000;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * What the user may administer, and the tenant they're working in. The menu links to tenant-less paths; this is the tenant they lead to.
 * The read waits for sign-in ({@link NOTIFICATIONS_ADMIN_SIGNED_IN}), so a guard that runs beside it never asks without a token.
 */
@Injectable({ providedIn: 'root' })
export class AccessService {
  private readonly http = inject(HttpClient);
  private readonly options = inject(NOTIFICATIONS_ADMIN_OPTIONS);
  private readonly signedIn = inject(NOTIFICATIONS_ADMIN_SIGNED_IN);
  private readonly current = signal<string | null>(readStored());
  private kept: { access: Access; until: number } | null = null;
  private reading: Observable<Access> | null = null;

  /** The tenant being worked in: the last one opened. */
  readonly tenantId = this.current.asReadonly();

  /**
   * The access. An answer is kept (5 minutes if it grants something, 30 seconds if not); a failed read (refused, the API down) isn't, and
   * neither is a read still waiting for sign-in: it is shared only while someone waits for it, and dropped when they all stop.
   */
  load(): Observable<Access> {
    if (this.kept && this.kept.until > Date.now()) {
      return of(this.kept.access);
    }
    this.reading ??= this.signedIn.pipe(
      take(1),
      switchMap(() => this.http.get<Partial<Access>>(`${this.options.apiBase}/api/v1/access`)),
      map(a => ({ platformAdmin: !!a?.platformAdmin, allTenants: !!a?.allTenants, tenants: Array.isArray(a?.tenants) ? a.tenants : [] }) satisfies Access),
      tap(access => this.kept = { access, until: Date.now() + (grants(access) ? KEEP_GRANTING_MS : KEEP_NOTHING_MS) }),
      catchError(() => of({ ...EMPTY_ACCESS, unavailable: true } satisfies Access)),
      finalize(() => this.reading = null),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
    return this.reading;
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

  /**
   * The user's tenant `tenantId` names, as the API spells it: listed, or any GUID when every tenant is theirs (the API answers for it);
   * null when it isn't theirs or isn't a tenant id. The one rule the guards and the home go by.
   */
  resolve(access: Access, tenantId: string | null | undefined): string | null {
    if (!tenantId) return null;
    return access.tenants.find(t => sameId(t.id, tenantId))?.id ?? (access.allTenants && GUID.test(tenantId) ? tenantId.toLowerCase() : null);
  }

  /** The tenant to open: the current one if it is still the user's, else the first listed; null when there is none. */
  pick(access: Access): string | null {
    return this.resolve(access, this.current()) ?? access.tenants[0]?.id ?? null;
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
