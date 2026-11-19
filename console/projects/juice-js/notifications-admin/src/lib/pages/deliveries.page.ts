import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DeliveryFilter, NotificationsAdminApi } from '../core/api.service';
import { DELIVERY_STATUSES } from '../core/models';
import { cursorList } from '../shared/resource';
import { Badge, TenantLinkPipe, instant, localInput, problemText, words } from '../shared/ui';

/**
 * The tenant's deliveries, newest first (phase-4c-plan §4): filtered by status, channel, integration, type, request and time, all in the
 * URL so a link (the overview's counts) opens the same list. With the filter on failed deliveries, "Replay" puts back exactly the failed
 * ones it matches (proof point 4): the same filter, up to the moment the list was read.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, Badge,
    TenantLinkPipe],
  template: `
    <div class="na-head"><h1>Deliveries</h1><span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="refresh()" data-testid="refresh">Refresh</button></div>
    <form class="na-filters" (ngSubmit)="apply()" data-testid="delivery-filters">
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Status</mat-label>
        <mat-select name="status" [(ngModel)]="draft.status" data-testid="filter-status">
          <mat-option value="">Any</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ label(s) }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Channel</mat-label>
        <input matInput name="channel" [(ngModel)]="draft.channel" data-testid="filter-channel"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Integration</mat-label>
        <input matInput name="integration" [(ngModel)]="draft.integration" data-testid="filter-integration"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Type</mat-label>
        <input matInput name="type" [(ngModel)]="draft.type" data-testid="filter-type"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Notification id</mat-label>
        <input matInput name="request" [(ngModel)]="draft.request" data-testid="filter-request"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>From</mat-label>
        <input matInput type="datetime-local" name="from" [(ngModel)]="draft.from"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>To</mat-label>
        <input matInput type="datetime-local" name="to" [(ngModel)]="draft.to"></mat-form-field>
      <button mat-flat-button type="submit" data-testid="filter-apply">Apply</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>

    @if (status() === 'failed') {
      <div class="na-actions" data-testid="replay-bar">
        @if (request()) {
          <span class="na-muted na-small">Replay takes no notification id: clear it to replay what the other filters match.</span>
        } @else if (!confirming()) {
          <button mat-stroked-button type="button" (click)="confirming.set(true)" [disabled]="!readAt() || replaying()" data-testid="replay">
            Replay these failed deliveries</button>
        } @else {
          <span>Put back every failed delivery this filter matches{{ list.hasMore() ? '' : ' (' + list.items().length + ')' }}, up to the newest
            shown ({{ readAt() | date: 'HH:mm:ss' }})?</span>
          <button mat-flat-button type="button" (click)="replay()" [disabled]="replaying()" data-testid="replay-confirm">Replay</button>
          <button mat-button type="button" (click)="confirming.set(false)">Cancel</button>
        }
        @if (replayed(); as r) {
          <span data-testid="replay-result">Replayed {{ r.replayed }}{{ r.skipped ? ', passed over ' + r.skipped + ' (nothing to send, or expired)' : '' }}.
            {{ r.more ? 'More matched: replay again for the rest.' : '' }}</span>
        }
        @if (replayError()) { <span class="na-error" data-testid="replay-error">{{ replayError() }}</span> }
      </div>
    }

    @if (list.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (list.first.error()) { <p class="na-error" data-testid="deliveries-error">{{ error() }}</p> }
    <table class="na-table" data-testid="deliveries">
      <thead><tr><th>Delivery</th><th>Status</th><th>Channel</th><th>Integration</th><th>Type</th><th>Attempts</th><th>Created</th><th>Last error</th></tr></thead>
      <tbody>
        @for (d of list.items(); track d.id) {
          <tr data-testid="delivery-row" [attr.data-id]="d.id">
            <td><a class="na-mono" [routerLink]="['deliveries', d.id] | naTenant: tenantId()">{{ d.id.slice(0, 8) }}…</a></td>
            <td><na-badge [state]="d.status" />@if (d.failureCategory) { <div class="na-muted na-small">{{ label(d.failureCategory) }}</div> }</td>
            <td>{{ d.channel }}</td>
            <td>{{ d.integration }}</td>
            <td>{{ d.type ?? '—' }}</td>
            <td>{{ d.attempts }}</td>
            <td class="na-nowrap">{{ d.createdAt | date: 'MMM d, HH:mm:ss' }}</td>
            <td class="na-small">{{ d.lastError }}</td>
          </tr>
        } @empty { @if (!list.first.isLoading()) { <tr><td colspan="8" class="na-muted">No deliveries match.</td></tr> } }
      </tbody>
    </table>
    <div class="na-more">
      <span class="na-muted">{{ list.items().length }} shown</span>
      @if (list.hasMore()) { <button mat-stroked-button type="button" (click)="list.more()" [disabled]="list.loadingMore()" data-testid="more">More</button> }
    </div>`,
})
export class DeliveriesPage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly tenantId = input.required<string>();
  readonly status = input<string>();
  readonly channel = input<string>();
  readonly integration = input<string>();
  readonly type = input<string>();
  readonly request = input<string>();
  readonly from = input<string>();
  readonly to = input<string>();

  protected readonly statuses = DELIVERY_STATUSES;
  protected readonly label = words;
  protected draft = { status: '', channel: '', integration: '', type: '', request: '', from: '', to: '' };

  private readonly filter = computed<DeliveryFilter>(() => ({ status: this.status(), channel: this.channel(), integration: this.integration(),
    type: this.type(), request: this.request(), from: this.from(), to: this.to() }));
  protected readonly list = cursorList(() => ({ tenantId: this.tenantId(), filter: this.filter() }),
    (p, cursor) => this.api.deliveries(p.tenantId, p.filter, cursor));
  /** When the list was read: a replay takes nothing created after it, so it puts back what the list shows. */
  // The latest creation time among the deliveries shown, by the server's clock: a replay takes nothing created after it, so it puts back what
  // the list shows whenever the list was read and whatever the browser's clock says. The latest of all, not the first row's: the list is in
  // id order, and a delivery's time is its planning step's start, before its id is minted (two planned at once can be out of order).
  protected readonly readAt = computed(() => this.list.items().reduce<string | null>(
    (latest, d) => !latest || new Date(d.createdAt).getTime() > new Date(latest).getTime() ? d.createdAt : latest, null));
  protected readonly error = computed(() => problemText(this.list.first.error()));
  protected readonly confirming = signal(false);
  protected readonly replaying = signal(false);
  protected readonly replayed = signal<{ replayed: number; skipped: number; more: boolean } | null>(null);
  protected readonly replayError = signal('');

  constructor() {
    // The fields show the filter in the URL (a link, back and forward); a new filter asks to confirm a replay afresh.
    effect(() => {
      const f = this.filter();
      untracked(() => {
        this.draft = { status: f.status ?? '', channel: f.channel ?? '', integration: f.integration ?? '', type: f.type ?? '',
          request: f.request ?? '', from: localInput(f.from), to: localInput(f.to) };
        this.confirming.set(false);
        this.replayed.set(null);
      });
    });
  }

  protected refresh(): void {
    this.list.first.reload();
  }

  protected apply(): void {
    const d = this.draft;
    void this.router.navigate([], { relativeTo: this.route, queryParams: { status: d.status || null, channel: d.channel.trim() || null,
      integration: d.integration.trim() || null, type: d.type.trim() || null, request: d.request.trim() || null, from: instant(d.from) ?? null,
      to: instant(d.to) ?? null } });
  }

  protected clear(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: {} });
  }

  protected replay(): void {
    const f = this.filter();
    const readAt = this.readAt();
    // Up to when the list was read (or the filter's own end, if earlier).
    if (!readAt) return;
    // Past the newest shown (the bound is exclusive; the API's times are finer than a millisecond), or the filter's own end if earlier.
    const newest = new Date(new Date(readAt).getTime() + 1).toISOString();
    const to = f.to && f.to < newest ? f.to : newest;
    this.replaying.set(true);
    this.replayError.set('');
    this.api.replay(this.tenantId(), { channel: f.channel, integration: f.integration, type: f.type, from: f.from, to: to ?? undefined }, 10_000).subscribe({
      next: result => {
        this.replaying.set(false);
        this.confirming.set(false);
        this.replayed.set(result);
        this.refresh();
      },
      error: e => { this.replaying.set(false); this.replayError.set(problemText(e)); },
    });
  }
}
