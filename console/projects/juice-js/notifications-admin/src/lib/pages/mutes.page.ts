import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { interval, map } from 'rxjs';
import { NotificationsAdminApi } from '../core/api.service';
import { Mute, Page } from '../core/models';
import { FormErrors, FormErrorsComponent, MatchDraft, MatchEditorComponent, NaFieldError, Pending, everyNotification, matchDraft, matchIssues,
  matchOf, matchText, rowAction, submit, validateAll, whenChanged } from '../shared/forms';
import { cursorList } from '../shared/resource';
import { Badge, instant, problemText } from '../shared/ui';
import { tenantRights } from '../shared/rights';

const PAGE = 50;
/** The API's bound on a mute's reason. */
const REASON_MAX = 500;

/**
 * The tenant's mutes (design §6.4): for a while, or until cancelled, notifications a match names reach no one in the tenant. A mandatory
 * type is held only by a mute that names it exactly, never by a pattern; such a mute is flagged. A cancelled or ended mute is history.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, Badge,
    NaFieldError, FormErrorsComponent, MatchEditorComponent],
  template: `
    <div class="na-head"><h1>Mutes</h1><span class="na-spacer"></span>
      <mat-checkbox [ngModel]="all()" (ngModelChange)="all.set($event)" data-testid="mutes-all">Show ended and cancelled</mat-checkbox>
      @if (!adding()) { @if (can.configure()) { <button mat-flat-button type="button" (click)="add()" data-testid="mute-new">New mute</button> } }
      <button mat-stroked-button type="button" (click)="list.first.reload()" data-testid="refresh">Refresh</button></div>
    @if (adding()) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="mute-form">
        @if (confirmingAll()) {
          <h2 data-testid="mute-confirm-title">Mute every notification for this tenant?</h2>
          <p class="na-hint">Nothing the tenant is sent will reach anyone while this lasts. A mandatory type is held only by a mute that names it
            exactly.</p>
          <na-form-errors [errors]="errors" [shown]="[]" />
          <div class="na-actions">
            <button mat-flat-button type="button" (click)="send()" [disabled]="busy()" data-testid="mute-confirm">Confirm</button>
            <button mat-button type="button" (click)="back()" data-testid="mute-back">Back</button>
          </div>
        } @else {
          <h2>New mute</h2>
          <p class="na-hint">Notifications this matches reach no one in the tenant while it lasts. A mandatory type is held only when the mute names
            it exactly (no *).</p>
          <na-match-editor [draft]="draft.match" [errors]="errors" />
          <mat-checkbox name="everything" [(ngModel)]="draft.everything" data-testid="mute-everything">Mute everything: name no type or label</mat-checkbox>
          <div class="na-form-grid">
            <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Starts</mat-label>
              <input matInput type="datetime-local" name="startsAt" [(ngModel)]="draft.startsAt" data-testid="mute-starts" [naError]="errors" naPointer="/startsAt">
              <mat-hint>Empty: now.</mat-hint>
              @for (e of errors.of('/startsAt'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
            </mat-form-field>
            <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Ends</mat-label>
              <input matInput type="datetime-local" name="endsAt" [(ngModel)]="draft.endsAt" data-testid="mute-ends" [naError]="errors" naPointer="/endsAt">
              <mat-hint>Empty: until cancelled.</mat-hint>
              @for (e of errors.of('/endsAt'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
            </mat-form-field>
            <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Reason</mat-label>
              <input matInput name="reason" [(ngModel)]="draft.reason" data-testid="mute-reason" [naError]="errors" naPointer="/reason">
              <mat-hint>Optional; up to 500 characters.</mat-hint>
              @for (e of errors.of('/reason'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
            </mat-form-field>
          </div>
          <na-form-errors [errors]="errors" [shown]="['/match', '/startsAt', '/endsAt', '/reason']" />
          <div class="na-actions">
            <button mat-flat-button type="submit" [disabled]="busy()" data-testid="mute-save">Mute</button>
            <button mat-button type="button" (click)="cancelForm()">Cancel</button>
          </div>
        }
      </form>
    }
    @if (list.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (list.first.error()) { <p class="na-error" data-testid="mutes-error">{{ error() }}</p> }
    <table class="na-table" data-testid="mutes">
      <thead><tr><th>Matches</th><th>State</th><th>From</th><th>Until</th><th>Reason</th><th>By</th><th></th></tr></thead>
      <tbody>
        @for (m of list.items(); track m.id) {
          @let s = state(m);
          <tr data-testid="mute-row" [attr.data-id]="m.id" [class.na-off]="s !== 'active' && s !== 'upcoming'">
            <td class="na-small">{{ text(m) }}
              @if (m.appliesToMandatory) { <div class="na-small na-warn-text" data-testid="mute-mandatory">holds a mandatory type too</div> }</td>
            <td><na-badge [state]="s" /></td>
            <td class="na-nowrap">{{ m.startsAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">@if (m.cancelledAt) { cancelled {{ m.cancelledAt | date: 'MMM d, HH:mm' }} } @else if (m.endsAt) { {{ m.endsAt | date: 'MMM d, HH:mm' }} } @else { until cancelled }</td>
            <td class="na-small">{{ m.reason ?? '' }}</td>
            <td class="na-small">{{ m.createdBy ?? '—' }}</td>
            <td class="na-nowrap">
              @if (s === 'active' || s === 'upcoming') {
                @if (confirming() === m.id) {
                  <button mat-flat-button type="button" (click)="cancel(m.id)" [disabled]="busy()" data-testid="mute-cancel-confirm">Cancel the mute</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button>
                } @else {
                  @if (can.configure()) { <button mat-button type="button" (click)="confirming.set(m.id)" data-testid="mute-cancel">Cancel…</button> }
                }
              }
            </td>
          </tr>
        } @empty { @if (!list.first.isLoading()) { <tr><td colspan="7" class="na-muted">{{ all() ? 'No mutes.' : 'Nothing is muted.' }}</td></tr> } }
      </tbody>
    </table>
    <div class="na-more">
      <span class="na-muted">{{ list.items().length }} shown</span>
      @if (list.hasMore()) { <button mat-stroked-button type="button" (click)="list.more()" [disabled]="list.loadingMore()" data-testid="more">More</button> }
    </div>
    @if (list.moreError()) { <p class="na-error" data-testid="mutes-more-error">{{ moreError() }}</p> }
    @if (rowError()) { <p class="na-error" data-testid="mute-row-error">{{ rowError() }}</p> }`,
})
export class MutesPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  /** The user's rights here: controls they may not use are hidden (the API refuses them anyway). */
  protected readonly can = tenantRights(() => this.tenantId());

  protected readonly all = signal(false);
  // The API pages mutes by the last one's id (newest first) and answers a plain list: a full page may have more after it.
  protected readonly list = cursorList(() => ({ tenantId: this.tenantId(), all: this.all() }),
    (p, before) => this.api.mutes(p.tenantId, p.all, before, PAGE).pipe(
      map((items): Page<Mute> => ({ items, next: items.length === PAGE ? items[items.length - 1].id : null }))));
  /** Renders the states again every 30 seconds; each is judged at the time of rendering. */
  private readonly tick = toSignal(interval(30_000), { initialValue: 0 });
  protected readonly error = computed(() => problemText(this.list.first.error()));
  protected readonly moreError = computed(() => problemText(this.list.moreError()));
  protected readonly errors = new FormErrors();
  protected readonly adding = signal(false);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  /** Whether the form asks "every notification?" before it sends: the draft names nothing to match, and "Mute everything" is ticked. */
  protected readonly confirmingAll = signal(false);
  private readonly pending = new Pending();
  protected draft: { match: MatchDraft; everything: boolean; startsAt: string; endsAt: string; reason: string } = this.blank();
  protected readonly text = (m: Mute) => matchText(m.match);

  constructor() {
    whenChanged(() => this.tenantId(), () => this.close());
  }

  private blank() {
    return { match: matchDraft({}), everything: false, startsAt: '', endsAt: '', reason: '' };
  }

  protected state(m: Mute): string {
    this.tick(); // read again every 30 s, so a mute that ends or starts while the page is open shows it
    const now = Date.now();
    if (m.cancelledAt) return 'cancelled';
    if (m.endsAt && new Date(m.endsAt).getTime() <= now) return 'ended';
    return new Date(m.startsAt).getTime() > now ? 'upcoming' : 'active';
  }

  protected add(): void {
    this.resetForm();
    this.adding.set(true);
  }

  /** A fresh draft; a save still running for the last one is dropped, and no confirm step or error carries over. */
  private resetForm(): void {
    this.pending.cancel();
    this.busy.set(false);
    this.draft = this.blank();
    this.errors.clear();
    this.confirmingAll.set(false);
  }

  protected cancelForm(): void {
    this.resetForm();
    this.adding.set(false);
  }

  /** Back from "every notification?" to the form, as it was typed. */
  protected back(): void {
    this.confirmingAll.set(false);
    this.errors.clear();
  }

  protected save(): void {
    if (this.busy() || this.confirmingAll()) return;
    // A type or label typed after ticking "everything": what was typed wins, and the box is cleared (the scope is never silently widened).
    if (this.draft.everything && !everyNotification(this.draft.match)) this.draft.everything = false;
    const startsAt = instant(this.draft.startsAt);
    const endsAt = instant(this.draft.endsAt);
    const reason = this.draft.reason;
    const issues = validateAll([
      () => matchIssues(this.draft.match),
      () => !this.draft.everything && everyNotification(this.draft.match)
        ? { pointer: '/match', message: 'Name a type or a label, or choose Mute everything.' } : null,
      () => startsAt && endsAt && new Date(endsAt).getTime() <= new Date(startsAt).getTime()
        ? { pointer: '/endsAt', message: 'Ends: must be after Starts.' } : null,
      () => reason.length > REASON_MAX ? { pointer: '/reason', message: `Reason: at most ${REASON_MAX} characters (${reason.length} typed).` } : null,
      () => /[\u0000-\u001f\u007f]/.test(reason) ? { pointer: '/reason', message: 'Reason: plain text on one line, no control characters.' } : null,
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    if (this.draft.everything) {
      this.errors.clear();
      this.confirmingAll.set(true);
      return;
    }
    this.send();
  }

  /** Sends the mute (a mute of everything only from the confirm step, which is the one place that calls this for it). */
  protected send(): void {
    if (this.draft.everything && !this.confirmingAll()) return;
    // The match is re-read here, not at the check: nothing sent is wider than what the guard let through.
    if (!this.draft.everything && everyNotification(this.draft.match)) return;
    this.pending.track(submit(this.api.addMute(this.tenantId(), { match: matchOf(this.draft.match), startsAt: instant(this.draft.startsAt) ?? null,
      endsAt: instant(this.draft.endsAt) ?? null, reason: this.draft.reason.trim() || null }), this.busy, this.errors,
      () => { this.cancelForm(); this.list.first.reload(); }));
  }

  protected cancel(id: string): void {
    // A mute already ended or cancelled elsewhere: the list read again shows how it stands.
    const again = () => this.list.first.reload();
    rowAction(this.api.cancelMute(this.tenantId(), id),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: again, failed: again });
  }

  /** What was open for another tenant (the page is reused across them) is closed. */
  private close(): void {
    this.adding.set(false);
    this.confirming.set(null);
    this.rowError.set('');
    this.resetForm();
  }
}
