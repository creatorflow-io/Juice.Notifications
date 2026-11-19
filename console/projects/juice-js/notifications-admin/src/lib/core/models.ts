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

/** A problem the API answered with (RFC 9457), with the fields it names. */
export interface Problem { status?: number; title?: string; detail?: string; code?: string; errors?: { pointer: string; message: string }[]; }
