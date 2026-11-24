import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, shareReplay, throwError } from 'rxjs';
import {
  AllowlistEntry, Correlation, CorrelationListItem, Delivery, DeliveryDetail, EscalationPolicy, EscalationStep, Integration, Match, Mute,
  NotificationDetail, NotificationListItem, Overview, Page, Provider, Recipient, ReplayResult, Resolver, RetryResult, Route, Storm, Target,
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

  // Delivery configuration (M38). Each write goes through the API's own commands, so it is checked and audited as any API change; a PUT
  // sends every field the form holds (the API keeps what is left out, and takes a secret reference sent back unchanged as no new secret).

  /**
   * What the deployment can send through: fixed while it runs, so read once per tenant (the read is a tenant admin's) and shared. A read
   * that fails is forgotten, so the next asks again.
   */
  providers(tenantId: string): Observable<Provider[]> {
    let read = this.providerReads.get(tenantId);
    if (!read) {
      read = this.http.get<Provider[]>(this.url(tenantId, 'providers')).pipe(
        catchError(e => { this.providerReads.delete(tenantId); return throwError(() => e); }),
        shareReplay(1));
      this.providerReads.set(tenantId, read);
    }
    return read;
  }

  private readonly providerReads = new Map<string, Observable<Provider[]>>();

  integrations(tenantId: string): Observable<Integration[]> {
    return this.http.get<Integration[]>(this.url(tenantId, 'integrations'));
  }

  putIntegration(tenantId: string, key: string, body: { channel: string; kind: string; settings: Record<string, unknown>; secretRef: string | null;
    priority: number; enabled: boolean }): Observable<Integration> {
    return this.http.put<Integration>(this.url(tenantId, `integrations/${encodeURIComponent(key)}`), body);
  }

  deleteIntegration(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `integrations/${encodeURIComponent(key)}`));
  }

  /** Tells the provider where its callbacks go (Telegram's webhook): for kinds that `registersCallbacks`. */
  registerCallbacks(tenantId: string, key: string): Observable<void> {
    return this.http.post<void>(this.url(tenantId, `integrations/${encodeURIComponent(key)}/callbacks`), null);
  }

  putTarget(tenantId: string, integration: string, key: string, body: { address: string | null; addressRef: string | null; enabled: boolean })
    : Observable<Target> {
    return this.http.put<Target>(this.url(tenantId, `integrations/${encodeURIComponent(integration)}/targets/${encodeURIComponent(key)}`), body);
  }

  deleteTarget(tenantId: string, integration: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `integrations/${encodeURIComponent(integration)}/targets/${encodeURIComponent(key)}`));
  }

  routes(tenantId: string): Observable<Route[]> {
    return this.http.get<Route[]>(this.url(tenantId, 'routes'));
  }

  putRoute(tenantId: string, key: string, body: { match: Match; targets: string[]; enabled: boolean }): Observable<Route> {
    return this.http.put<Route>(this.url(tenantId, `routes/${encodeURIComponent(key)}`), body);
  }

  deleteRoute(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `routes/${encodeURIComponent(key)}`));
  }

  /** The tenant's mutes, newest first: current and upcoming ones, or with `all` ended and cancelled ones too; `before` a mute's id. */
  mutes(tenantId: string, all: boolean, before?: string | null, limit = 50): Observable<Mute[]> {
    return this.http.get<Mute[]>(this.url(tenantId, 'mutes'), { params: this.params({ all: all || null, before, limit }) });
  }

  addMute(tenantId: string, body: { match: Match; startsAt: string | null; endsAt: string | null; reason: string | null }): Observable<Mute> {
    return this.http.post<Mute>(this.url(tenantId, 'mutes'), body);
  }

  cancelMute(tenantId: string, id: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `mutes/${encodeURIComponent(id)}`));
  }

  escalationPolicies(tenantId: string): Observable<EscalationPolicy[]> {
    return this.http.get<EscalationPolicy[]>(this.url(tenantId, 'escalation-policies'));
  }

  putEscalationPolicy(tenantId: string, key: string, body: { steps: EscalationStep[]; stopOnAcknowledge: boolean }): Observable<EscalationPolicy> {
    return this.http.put<EscalationPolicy>(this.url(tenantId, `escalation-policies/${encodeURIComponent(key)}`), body);
  }

  deleteEscalationPolicy(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `escalation-policies/${encodeURIComponent(key)}`));
  }

  resolvers(tenantId: string): Observable<Resolver[]> {
    return this.http.get<Resolver[]>(this.url(tenantId, 'resolvers'));
  }

  allowlist(tenantId: string): Observable<AllowlistEntry[]> {
    return this.http.get<AllowlistEntry[]>(this.url(tenantId, 'allowlist'));
  }

  putAllowlistEntry(tenantId: string, body: { destination: string; ports: number[] }): Observable<AllowlistEntry> {
    return this.http.put<AllowlistEntry>(this.url(tenantId, 'allowlist'), body);
  }

  /** A destination can be a network (`10.20.0.0/16`), so it goes in the query. */
  deleteAllowlistEntry(tenantId: string, destination: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, 'allowlist'), { params: this.params({ destination }) });
  }

  /** Acknowledge, resolve (with an outcome) or cancel, as the signed-in admin. */
  changeCorrelation(tenantId: string, application: string, key: string, action: 'acknowledge' | 'resolve' | 'cancel', outcome?: string)
    : Observable<Correlation> {
    return this.http.post<Correlation>(this.url(tenantId, `correlations/${encodeURIComponent(key)}/${action}`),
      action === 'resolve' ? { outcome: outcome || null } : {}, { params: this.params({ application }) });
  }
}
