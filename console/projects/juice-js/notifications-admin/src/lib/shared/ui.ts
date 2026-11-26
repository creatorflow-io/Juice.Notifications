import { ChangeDetectionStrategy, Component, Pipe, PipeTransform, computed, inject, input } from '@angular/core';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';
import { Problem } from '../core/models';

// `wouldSend` (dry run) has no tone on purpose: recorded, not sent; neither done nor under way.
const OK = new Set(['delivered', 'accepted', 'planned', 'resolved', 'closed', 'enabled']);
const WARN = new Set(['held', 'pending', 'sending', 'retrying', 'planning', 'running', 'open', 'acknowledged', 'deferred', 'muted', 'optedOut', 'truncated', 'active', 'upcoming']);
const BAD = new Set(['failed', 'bounced', 'expired', 'resolverUnavailable']);

/** A state as a badge: delivered and done green, under way or held amber, failed red, the rest grey. */
@Component({
  selector: 'na-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="na-badge" [class.na-ok]="tone() === 'ok'" [class.na-warn]="tone() === 'warn'" [class.na-bad]="tone() === 'bad'"
    [attr.data-state]="state()">{{ label() }}</span>`,
})
export class Badge {
  readonly state = input.required<string>();
  protected readonly tone = computed(() => OK.has(this.state()) ? 'ok' : WARN.has(this.state()) ? 'warn' : BAD.has(this.state()) ? 'bad' : 'none');
  protected readonly label = computed(() => words(this.state()));
}

/** `optedOut` → `opted out`, `resolverUnavailable` → `resolver unavailable`. */
export function words(value: string | null | undefined): string {
  return (value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
}

/** A tenant page's link: `['deliveries', id] | naTenant: tenantId` → `['/tenants', tenantId, 'deliveries', id]` under the mount. */
@Pipe({ name: 'naTenant' })
export class TenantLinkPipe implements PipeTransform {
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  transform(segments: unknown[], tenantId: string): unknown[] {
    return [under(this.base, `tenants/${tenantId}`), ...segments];
  }
}

/** What went wrong, for a page: the API's own sentence when it gave one. */
export function problemText(error: unknown): string {
  if (!error) return '';
  const e = error as { status?: number; error?: Problem | string | null; message?: string };
  const problem = typeof e.error === 'object' ? e.error : null;
  if (e.status === 0) return "The API couldn't be reached.";
  if (e.status === 401) return 'Your sign-in has ended: sign in again.';
  if (e.status === 403) return problem?.detail ?? "You don't administer this tenant.";
  return problem?.detail ?? problem?.title ?? (typeof e.error === 'string' && e.error ? e.error : e.message ?? 'Something went wrong.');
}

/** A date-time input's value (local, minutes) as an ISO instant, or undefined. */
export function instant(local: string | null | undefined): string | undefined {
  if (!local) return undefined;
  const at = new Date(local);
  return isNaN(at.getTime()) ? undefined : at.toISOString();
}

/** An ISO instant as a date-time input's value (local, minutes). */
export function localInput(iso: string | null | undefined): string {
  if (!iso) return '';
  const at = new Date(iso);
  if (isNaN(at.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}T${pad(at.getHours())}:${pad(at.getMinutes())}`;
}
