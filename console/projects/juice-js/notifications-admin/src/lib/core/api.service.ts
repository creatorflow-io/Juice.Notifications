import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, shareReplay, throwError } from 'rxjs';
import {
  AllowlistEntry, Correlation, CorrelationListItem, Delivery, DeliveryDetail, EscalationPolicy, EscalationStep, Integration, Match, Mute,
  DisplayGroup, Group, MovedType, ResolverKind, Subscriber, Topic, NotificationDetail, NotificationListItem, Overview, Page, Provider, Recipient, ReplayResult, Resolver, RetryResult, Route, Storm, Target,
  TemplatePreview, TenantTemplate, TenantType, TenantTypeDetail,
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

  // People (M40): resolvers, subscribers and their aliases, local users, groups, topics.
  resolverKinds(tenantId: string): Observable<ResolverKind[]> {
    return this.http.get<ResolverKind[]>(this.url(tenantId, 'resolver-kinds'));
  }

  /** Creates or replaces; the kind of an existing resolver is fixed. */
  putResolver(tenantId: string, key: string, body: { kind: string; handles: string[]; settings: unknown; secretRef: string | null; enabled: boolean }): Observable<Resolver> {
    return this.http.put<Resolver>(this.url(tenantId, `resolvers/${encodeURIComponent(key)}`), body);
  }

  deleteResolver(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `resolvers/${encodeURIComponent(key)}`));
  }

  /** A page in creation order after `after`; of one source, or of one identity (an alias's finds the subscriber it is linked to). */
  subscribers(tenantId: string, query: { source?: string; externalId?: string; after?: string | null; limit?: number }): Observable<Subscriber[]> {
    return this.http.get<Subscriber[]>(this.url(tenantId, 'subscribers'), { params: this.params(query) });
  }

  subscriber(tenantId: string, id: string): Observable<Subscriber> {
    return this.http.get<Subscriber>(this.url(tenantId, `subscribers/${encodeURIComponent(id)}`));
  }

  linkAlias(tenantId: string, id: string, body: { source: string; externalId: string }): Observable<Subscriber> {
    return this.http.post<Subscriber>(this.url(tenantId, `subscribers/${encodeURIComponent(id)}/aliases`), body);
  }

  /** An external id can hold anything a source uses (slashes included), so the identity goes in the query. */
  unlinkAlias(tenantId: string, id: string, source: string, externalId: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `subscribers/${encodeURIComponent(id)}/aliases`), { params: this.params({ source, externalId }) });
  }

  /** Adds or changes a local user (a subscriber of source `local`). */
  putLocalUser(tenantId: string, id: string, body: { name: string | null; locale: string | null; timeZone: string | null }): Observable<Subscriber> {
    return this.http.put<Subscriber>(this.url(tenantId, `local-users/${encodeURIComponent(id)}`), body);
  }

  groups(tenantId: string): Observable<Group[]> {
    return this.http.get<Group[]>(this.url(tenantId, 'groups'));
  }

  putGroup(tenantId: string, key: string, body: { name: string }): Observable<Group> {
    return this.http.put<Group>(this.url(tenantId, `groups/${encodeURIComponent(key)}`), body);
  }

  deleteGroup(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `groups/${encodeURIComponent(key)}`));
  }

  groupMembers(tenantId: string, key: string): Observable<Subscriber[]> {
    return this.http.get<Subscriber[]>(this.url(tenantId, `groups/${encodeURIComponent(key)}/members`));
  }

  addGroupMember(tenantId: string, key: string, subscriberId: string): Observable<void> {
    return this.http.put<void>(this.url(tenantId, `groups/${encodeURIComponent(key)}/members/${encodeURIComponent(subscriberId)}`), null);
  }

  removeGroupMember(tenantId: string, key: string, subscriberId: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `groups/${encodeURIComponent(key)}/members/${encodeURIComponent(subscriberId)}`));
  }

  topics(tenantId: string): Observable<Topic[]> {
    return this.http.get<Topic[]>(this.url(tenantId, 'topics'));
  }

  putTopic(tenantId: string, key: string, body: { name: string; selfService: boolean }): Observable<Topic> {
    return this.http.put<Topic>(this.url(tenantId, `topics/${encodeURIComponent(key)}`), body);
  }

  deleteTopic(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `topics/${encodeURIComponent(key)}`));
  }

  topicSubscribers(tenantId: string, key: string): Observable<Subscriber[]> {
    return this.http.get<Subscriber[]>(this.url(tenantId, `topics/${encodeURIComponent(key)}/subscribers`));
  }

  subscribe(tenantId: string, key: string, subscriberId: string): Observable<void> {
    return this.http.put<void>(this.url(tenantId, `topics/${encodeURIComponent(key)}/subscribers/${encodeURIComponent(subscriberId)}`), null);
  }

  unsubscribe(tenantId: string, key: string, subscriberId: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `topics/${encodeURIComponent(key)}/subscribers/${encodeURIComponent(subscriberId)}`));
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

  // Content (M39): types and their templates (the tenant's own over the application's), the preview, display groups.

  types(tenantId: string): Observable<TenantType[]> {
    return this.http.get<TenantType[]>(this.url(tenantId, 'types'));
  }

  type(tenantId: string, key: string): Observable<TenantTypeDetail> {
    return this.http.get<TenantTypeDetail>(this.url(tenantId, `types/${encodeURIComponent(key)}`));
  }

  template(tenantId: string, key: string, channel: string, locale: string): Observable<TenantTemplate> {
    return this.http.get<TenantTemplate>(this.url(tenantId, `types/${encodeURIComponent(key)}/templates/${encodeURIComponent(channel)}`),
      { params: this.params({ locale }) });
  }

  putTemplate(tenantId: string, key: string, channel: string, locale: string, body: string): Observable<TenantTemplate> {
    return this.http.put<TenantTemplate>(this.url(tenantId, `types/${encodeURIComponent(key)}/templates/${encodeURIComponent(channel)}`), { body, locale });
  }

  /** The tenant's own template goes: the application's is in effect again. */
  deleteTemplate(tenantId: string, key: string, channel: string, locale: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `types/${encodeURIComponent(key)}/templates/${encodeURIComponent(channel)}`),
      { params: this.params({ locale }) });
  }

  /**
   * Renders `body` (or the template in effect in `locale`, when null) with `data` (or a sample from the type's schema, when null); nothing
   * is stored.
   */
  preview(tenantId: string, type: string, channel: string, locale: string, body: string | null, data: unknown): Observable<TemplatePreview> {
    return this.http.post<TemplatePreview>(this.url(tenantId, 'templates/preview'), { type, channel, locale, body, data });
  }

  displayGroups(tenantId: string): Observable<DisplayGroup[]> {
    return this.http.get<DisplayGroup[]>(this.url(tenantId, 'display-groups'));
  }

  putDisplayGroup(tenantId: string, key: string, body: { name: string; position: number | null }): Observable<DisplayGroup> {
    return this.http.put<DisplayGroup>(this.url(tenantId, `display-groups/${encodeURIComponent(key)}`), body);
  }

  /** Types moved to it go back to their own groups; their open items follow. */
  deleteDisplayGroup(tenantId: string, key: string): Observable<void> {
    return this.http.delete<void>(this.url(tenantId, `display-groups/${encodeURIComponent(key)}`));
  }

  /** Where a type's items go in this tenant: a group, or null for the one it declares. Open items move at once. */
  moveType(tenantId: string, key: string, group: string | null): Observable<MovedType> {
    const url = this.url(tenantId, `types/${encodeURIComponent(key)}/display-group`);
    return group === null ? this.http.delete<MovedType>(url) : this.http.put<MovedType>(url, { group });
  }

  /** Acknowledge, resolve (with an outcome) or cancel, as the signed-in admin. */
  changeCorrelation(tenantId: string, application: string, key: string, action: 'acknowledge' | 'resolve' | 'cancel', outcome?: string)
    : Observable<Correlation> {
    return this.http.post<Correlation>(this.url(tenantId, `correlations/${encodeURIComponent(key)}/${action}`),
      action === 'resolve' ? { outcome: outcome || null } : {}, { params: this.params({ application }) });
  }
}
