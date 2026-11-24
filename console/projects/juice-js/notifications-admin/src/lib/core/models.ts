/*
 * What the Notifications API answers, as the Console reads it (Juice.Notifications.Api.Contracts). Enums arrive camelCase.
 */

export type DeliveryStatus = 'pending' | 'sending' | 'retrying' | 'accepted' | 'delivered' | 'bounced' | 'failed' | 'expired' | 'cancelled';
export const DELIVERY_STATUSES: DeliveryStatus[] = ['pending', 'sending', 'retrying', 'accepted', 'delivered', 'bounced', 'failed', 'expired', 'cancelled'];
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
}

export interface Correlation {
  key: string; state: CorrelationState; acknowledgedBy: string | null; acknowledgedAt: string | null; resolvedAt: string | null;
  resolvedBy: string | null; outcome: string | null; reopenedAt: string | null; createdAt: string; lastRequestAt: string;
  notifications: { id: string; requestId: string; type: string; state: NotificationState; createdAt: string }[];
  recipients: Record<string, number>; deliveries: Record<string, number>;
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

export interface Route { key: string; match: Match; targets: string[]; enabled: boolean; managedBy: ManagedBy; updatedAt: string; }

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

export interface AllowlistEntry { destination: string; ports: number[]; managedBy: ManagedBy; updatedAt: string; }

/** A problem the API answered with (RFC 9457), with the fields it names. */
export interface Problem { status?: number; title?: string; detail?: string; code?: string; errors?: { pointer: string; message: string }[]; }
