import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Correlation, CorrelationListItem, Delivery, DeliveryDetail, NotificationDetail, NotificationListItem, Overview, Page, Recipient, ReplayResult,
  RetryResult, Storm,
} from './models';
import { NOTIFICATIONS_ADMIN_OPTIONS } from './options';

/** Query parameters; empty values are left out. */
export type Query = Record<string, string | number | boolean | null | undefined>;

/** A delivery list's filter, as the list and a bulk replay share it. */
export interface DeliveryFilter {
  status?: string; channel?: string; integration?: string; type?: string; request?: string; from?: string; to?: string;
}

/** The tenant's reads and actions the pages use, each under `/api/v1/tenants/{tenantId}`. */
@Injectable({ providedIn: 'root' })
export class NotificationsAdminApi {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(NOTIFICATIONS_ADMIN_OPTIONS).apiBase;

  private url(tenantId: string, path: string): string {
    return `${this.apiBase}/api/v1/tenants/${encodeURIComponent(tenantId)}/${path}`;
  }

  private params(query: Query = {}): HttpParams {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== null && value !== undefined && value !== '') params = params.set(key, String(value));
    }
    return params;
  }

  overview(tenantId: string): Observable<Overview> {
    return this.http.get<Overview>(this.url(tenantId, 'overview'));
  }

  storms(tenantId: string, all = false): Observable<Storm[]> {
    return this.http.get<Storm[]>(this.url(tenantId, 'storms'), { params: this.params({ all: all || null }) });
  }

  deliveries(tenantId: string, filter: DeliveryFilter, before?: string | null, limit = 50): Observable<Page<Delivery>> {
    return this.http.get<Page<Delivery>>(this.url(tenantId, 'deliveries'), { params: this.params({ ...filter, before, limit }) });
  }

  delivery(tenantId: string, id: string): Observable<DeliveryDetail> {
    return this.http.get<DeliveryDetail>(this.url(tenantId, `deliveries/${encodeURIComponent(id)}`));
  }

  retry(tenantId: string, id: string): Observable<RetryResult> {
    return this.http.post<RetryResult>(this.url(tenantId, `deliveries/${encodeURIComponent(id)}/retry`), null);
  }

  /** The failed deliveries a filter names, put back to send (at most `limit`; `more` when others matched). */
  replay(tenantId: string, filter: Omit<DeliveryFilter, 'status' | 'request'>, limit?: number): Observable<ReplayResult> {
    const body = { channel: filter.channel || null, integration: filter.integration || null, type: filter.type || null, category: null,
      from: filter.from || null, to: filter.to || null, limit: limit ?? null };
    return this.http.post<ReplayResult>(this.url(tenantId, 'deliveries/replay'), body);
  }

  notifications(tenantId: string, filter: Query, before?: string | null, limit = 50): Observable<Page<NotificationListItem>> {
    return this.http.get<Page<NotificationListItem>>(this.url(tenantId, 'notifications'), { params: this.params({ ...filter, before, limit }) });
  }

  notification(tenantId: string, id: string): Observable<NotificationDetail> {
    return this.http.get<NotificationDetail>(this.url(tenantId, `notifications/${encodeURIComponent(id)}`));
  }

  recipients(tenantId: string, id: string, after?: string | null, limit = 100): Observable<Page<Recipient>> {
    return this.http.get<Page<Recipient>>(this.url(tenantId, `notifications/${encodeURIComponent(id)}/recipients`),
      { params: this.params({ after, limit }) });
  }

  correlations(tenantId: string, filter: Query, before?: string | null, limit = 50): Observable<Page<CorrelationListItem>> {
    return this.http.get<Page<CorrelationListItem>>(this.url(tenantId, 'correlations'), { params: this.params({ ...filter, before, limit }) });
  }

  correlation(tenantId: string, application: string, key: string): Observable<Correlation> {
    return this.http.get<Correlation>(this.url(tenantId, `correlations/${encodeURIComponent(key)}`), { params: this.params({ application }) });
  }

  /** Acknowledge, resolve (with an outcome) or cancel, as the signed-in admin. */
  changeCorrelation(tenantId: string, application: string, key: string, action: 'acknowledge' | 'resolve' | 'cancel', outcome?: string)
    : Observable<Correlation> {
    return this.http.post<Correlation>(this.url(tenantId, `correlations/${encodeURIComponent(key)}/${action}`),
      action === 'resolve' ? { outcome: outcome || null } : {}, { params: this.params({ application }) });
  }
}
