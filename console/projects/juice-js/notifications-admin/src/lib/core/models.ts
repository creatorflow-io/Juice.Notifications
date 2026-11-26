/*
 * What the Notifications API answers, as the Console reads it (Juice.Notifications.Api.Contracts). Enums arrive camelCase.
 */

/** `wouldSend`: recorded in dry run (phase-5-plan I67): every check passed, nothing was sent. */
export type DeliveryStatus = 'pending' | 'sending' | 'retrying' | 'accepted' | 'delivered' | 'bounced' | 'failed' | 'expired' | 'cancelled' | 'wouldSend';
export const DELIVERY_STATUSES: DeliveryStatus[] = ['pending', 'sending', 'retrying', 'accepted', 'delivered', 'bounced', 'failed', 'expired', 'cancelled', 'wouldSend'];
export type FailureCategory = 'provider' | 'destination' | 'content' | 'policy' | 'rateLimited';
export type AttemptOutcome = 'delivered' | 'accepted' | 'retryableFailure' | 'permanentFailure' | 'unknown';
export type NotificationState = 'planning' | 'planned' | 'expired' | 'failed' | 'running' | 'conditionNotMet' | 'cancelled';
export const NOTIFICATION_STATES: NotificationState[] = ['planning', 'running', 'planned', 'failed', 'expired', 'conditionNotMet', 'cancelled'];
export type CorrelationState = 'open' | 'acknowledged' | 'resolved' | 'cancelled';
export const CORRELATION_STATES: CorrelationState[] = ['open', 'acknowledged', 'resolved', 'cancelled'];
export type RecipientKind = 'subscriber' | 'route' | 'external' | 'name' | 'contactPoint';
export type RecipientOutcome = 'pending' | 'planned' | 'muted' | 'optedOut' | 'deferred' | 'cancelled' | 'truncated' | 'resolverUnavailable' | 'failed';
export type DigestState = 'open' | 'closed' | 'cancelled';

/** A page of a list paged by cursor: `next` is the cursor for the page after, null on the last. */
export interface Page<T> { items: T[]; next: string | null; }

export interface Delivery {
  id: string; requestId: string | null; type: string | null; channel: string; integration: string; destinationKey: string;
  status: DeliveryStatus; attempts: number; failureCategory: FailureCategory | null; lastError: string | null;
  createdAt: string; firstAttemptAt: string | null; completedAt: string | null; failovers: number;
}

export interface DeliveryItem { recipientId: string; kind: RecipientKind; namedAs: string; route: string | null; resolver: string; }

export interface DeliveryAttempt {
  number: number; at: string; outcome: AttemptOutcome; category: FailureCategory | null; integration: string; statusCode: number | null;
  durationMs: number; error: string | null; providerMessageId: string | null;
}

export interface DeliveryDetail {
  delivery: Delivery; destinationSnapshot: string; templateVersion: number; locale: string; rendererVersion: string;
  providerMessageId: string | null; items: DeliveryItem[]; attempts: DeliveryAttempt[];
}

export interface RetryResult { id: string; status: DeliveryStatus; attempts: number; replays: number; }
export interface ReplayResult { replayed: number; skipped: number; more: boolean; }

export interface NotificationListItem {
  id: string; type: string; typeVersion: number; application: string; correlationKey: string | null; priority: string;
  state: NotificationState; error: string | null; actor: string | null; acceptedAt: string;
}

export interface NotificationDelivery {
  id: string; channel: string; destinationKey: string; status: DeliveryStatus; attempts: number; failureCategory: FailureCategory | null;
  createdAt: string; completedAt: string | null;
}

export interface NotificationDetail {
  id: string; type: string; typeVersion: number; idempotencyKey: string; correlationKey: string | null; priority: string;
  labels: Record<string, string>; links: Record<string, string>; actor: string | null; acceptedAt: string; expiresAt: string | null;
  state: NotificationState; error: string | null; deliveries: NotificationDelivery[]; application: string | null;
}

export interface Recipient {
  id: string; kind: RecipientKind; namedAs: string; via: string[]; subscriberId: string | null; route: string | null; channel: string | null;
  address: string | null; resolver: string; outcome: RecipientOutcome; bypassed: string[]; escalationStep: number;
  digest: { id: string; state: DigestState; closesAt: string; closedAt: string | null } | null; createdAt: string; name: string | null;
  deliveries: { id: string; channel: string; destinationKey: string; status: DeliveryStatus; failureCategory: FailureCategory | null }[];
}

export interface CorrelationListItem {
  id: string; application: string; key: string; state: CorrelationState; acknowledgedBy: string | null; acknowledgedAt: string | null;
  resolvedAt: string | null; outcome: string | null; createdAt: string; lastRequestAt: string;
  /** When its producer holds its escalation (p5-d3); null when it isn't held. Absent from an older API. */
  heldAt?: string | null;
}

export interface Correlation {
  key: string; state: CorrelationState; acknowledgedBy: string | null; acknowledgedAt: string | null; resolvedAt: string | null;
  resolvedBy: string | null; outcome: string | null; reopenedAt: string | null; createdAt: string; lastRequestAt: string;
  notifications: { id: string; requestId: string; type: string; state: NotificationState; createdAt: string }[];
  recipients: Record<string, number>; deliveries: Record<string, number>;
  /** The producer's hold on its escalation (p5-d3): since when, why, by whom; null when it isn't held. Absent from an older API. */
  heldAt?: string | null; holdReason?: string | null; heldBy?: string | null;
}

export interface Storm { id: string; route: string; startedAt: string; endedAt: string | null; peak: number; digested: number; }

export interface Overview {
  at: string; since: string; deliveries: { channel: string; status: DeliveryStatus; count: number }[];
  failingIntegrations: { integration: string; failed: number; lastFailedAt: string; lastError: string | null }[];
  openStorms: number; unfinished: number;
}

/** Who owns a configuration row: the API (editable here), the deployment's provisioning file, or the service itself (the inbox). */
export type ManagedBy = 'api' | 'config' | 'service';

/** A channel and provider kind the deployment can send through (`GET …/providers`). */
export interface Provider { channel: string; kind: string; signs: boolean; registersCallbacks: boolean; }

export interface Target { key: string; address: string | null; addressRef: string | null; enabled: boolean; managedBy: ManagedBy; updatedAt: string; }

export interface Integration {
  key: string; channel: string; kind: string; settings: Record<string, unknown>; secretRef: string | null; priority: number; enabled: boolean;
  managedBy: ManagedBy; targets: Target[]; updatedAt: string;
}

/** What a route or a mute matches (design §6.3): type patterns, label matchers, a minimum priority. `{}` matches everything. */
export interface Match { type?: string | string[]; labels?: Record<string, string>; minPriority?: Priority; }
export type Priority = 'low' | 'normal' | 'high' | 'critical';
export const PRIORITIES: Priority[] = ['low', 'normal', 'high', 'critical'];

/** A route; `digestSeconds` (0–3600, phase 5 M46-1): what reaches it goes as one digest per window and type; 0 (or an older API's none) at once. */
export interface Route { key: string; match: Match; targets: string[]; enabled: boolean; managedBy: ManagedBy; updatedAt: string; digestSeconds?: number; }

export interface Mute {
  id: string; match: Match; startsAt: string; endsAt: string | null; reason: string | null; createdBy: string | null; createdAt: string;
  cancelledAt: string | null; appliesToMandatory: boolean;
}

/** An escalation step's recipient, as a request names one: the route's targets, or people through one of the tenant's resolvers. */
export type EscalationRecipient =
  | { kind: 'route' }
  | { kind: 'user' | 'group' | 'role'; id: string; resolver: string }
  | { kind: 'topic'; key: string; resolver: string };
export const ESCALATION_RECIPIENT_KINDS: EscalationRecipient['kind'][] = ['route', 'user', 'group', 'role', 'topic'];
export interface EscalationStep { afterMinutes: number; recipients: EscalationRecipient[]; }
export interface EscalationPolicy { key: string; steps: EscalationStep[]; stopOnAcknowledge: boolean; managedBy: ManagedBy; updatedAt: string; }

export interface Resolver {
  key: string; kind: string; handles: string[]; settings: Record<string, unknown>; secretRef: string | null; enabled: boolean; managedBy: ManagedBy;
  updatedAt: string;
}

/** What a tenant's resolver can be in this deployment, and the recipient kinds it can handle. */
export interface ResolverKind { kind: string; handles: string[]; }

/** Another identity linked to a subscriber: requests naming it reach the subscriber. */
export interface Alias { source: string; externalId: string; linkedBy: string | null; linkedAt: string; }

/**
 * A person the tenant notifies, by the identity they were first seen as (`source`, `externalId`: `local` for a local user). `mergedInto`: their
 * identity is linked into another subscriber, who receives for them.
 */
export interface Subscriber {
  id: string; source: string; externalId: string; name: string | null; locale: string | null; timeZone: string | null; mergedInto: string | null;
  aliases: Alias[]; createdAt: string;
}

/** A local group (the `local` resolver's `group`) and how many are in it. */
export interface Group { key: string; name: string; members: number; updatedAt: string; }

/** A topic: requests name it to reach its subscribers; self-service ones people join and leave themselves. */
export interface Topic { key: string; name: string; selfService: boolean; subscribers: number; updatedAt: string; }

/** A tenant's settings: a null field takes the deployment's default, given beside it. */
export interface TenantSettings {
  stormThreshold: number | null; defaultStormThreshold: number; requestRetentionDays: number | null; defaultRequestRetentionDays: number;
  contentRetentionDays: number | null; defaultContentRetentionDays: number; updatedAt: string | null;
}

/** An application (platform admins): what sends, as which client and broker user, and for which tenants. */
export interface Application {
  id: string; key: string; name: string; clientId: string; brokerUser: string | null; enabled: boolean; tenants: string[]; createdAt: string;
  updatedAt: string;
}

export interface AllowlistEntry { destination: string; ports: number[]; managedBy: ManagedBy; updatedAt: string; }

/** A type the tenant receives, at its latest version, and which channels have a template: the application's (`declared`) or its own. */
export interface TenantType {
  key: string; name: string; version: number; application: string; channels: { channel: string; declared: boolean; overridden: boolean }[];
}

/** One type in full (`GET …/types/{key}`): its latest version as declared, its versions, its group here and its templates by locale. */
export interface TenantTypeDetail {
  key: string; application: string; version: number; name: string; category: string;
  /** The group the type declares; `group` is where its items go here (`groupChosen` when the tenant chose it). */
  displayGroup: string; group: string; groupChosen: boolean;
  priority: Priority; mandatory: boolean; externalRecipients: boolean; correlation: string; schema: unknown;
  /** The service's own type: its templates stay as they are. */
  serviceType: boolean;
  versions: { key: string; version: number; name: string; schemaHash: string; createdAt: string }[];
  channels: { channel: string; declared: boolean; overridden: boolean; locales: { locale: string; declared: boolean; overridden: boolean }[] }[];
}

/** A channel's template for a locale: the application's text and the tenant's own (null when none; `passthrough` sends the data as is). */
export interface TenantTemplate {
  type: string; typeVersion: number; channel: string; locale: string; application: string | null; tenant: string | null; tenantVersion: number | null;
  updatedAt: string | null;
}

/** A rendering, as the channel stores it (email: subject, text, html), or why it didn't render; `data` is what it rendered with. */
export interface TemplatePreview { channel: string; content: Record<string, unknown> | null; error: string | null; data: unknown; }

/** An inbox display group (design D12): the three built-ins can be renamed, not deleted; `types` are those the tenant moved here. */
export interface DisplayGroup { key: string; name: string; position: number; builtIn: boolean; types: string[]; updatedAt: string; }

/** A type moved to a group (or back to its own): where its items go now, and how many open ones moved. */
export interface MovedType { type: string; group: string; chosen: boolean; itemsMoved: number; }

/** A problem the API answered with (RFC 9457), with the fields it names. */
export interface Problem { status?: number; title?: string; detail?: string; code?: string; errors?: { pointer: string; message: string }[]; }
