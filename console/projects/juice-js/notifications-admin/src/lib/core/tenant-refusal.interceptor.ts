import { HttpErrorResponse, HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Injectable, Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, throwError } from 'rxjs';
import type { NoAccessReason } from './guards';
import { NOTIFICATIONS_ADMIN_OPTIONS, normalizeBasePath, under } from './options';

/** The tenant pattern's refusals (Juice.MultiTenant.AspNetCore), and what the no-access page says for each. */
const REFUSALS: Record<string, NoAccessReason> = {
  tenant_not_found: 'unknown',
  tenant_inactive: 'inactive',
  tenant_starting: 'starting',
  tenant_forbidden: 'tenant',
};

/**
 * A tenant's read or action the API refused for the tenant itself (unknown, not active, still starting, not the caller's): the page can't
 * work in it, so the no-access page says why, with the link opened to try again. The guards can't tell beforehand: a platform admin
 * administers every tenant, and whether one exists and is active is the deployment's tenant directory's to say. The error still reaches
 * the page that asked.
 */
@Injectable()
export class TenantRefusalInterceptor implements HttpInterceptor {
  private readonly injector = inject(Injector);
  private readonly options = inject(NOTIFICATIONS_ADMIN_OPTIONS);

  intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    const tenants = `${this.options.apiBase}/api/v1/tenants/`;
    if (!request.url.startsWith(tenants)) return next.handle(request);
    return next.handle(request).pipe(catchError((e: unknown) => {
      const code = e instanceof HttpErrorResponse && e.error && typeof e.error === 'object' ? (e.error as { code?: string }).code : undefined;
      const reason = code ? REFUSALS[code] : undefined;
      if (reason) this.show(reason);
      return throwError(() => e);
    }));
  }

  private show(reason: NoAccessReason): void {
    // The router reads through HttpClient's injector chain: asked for when needed, not at construction.
    const router = this.injector.get(Router);
    const here = router.url;
    const page = under(normalizeBasePath(this.options.basePath), 'no-access');
    if (here.split('?')[0] === page) return;
    void router.navigate([page], { queryParams: { reason, returnUrl: here }, replaceUrl: true });
  }
}
