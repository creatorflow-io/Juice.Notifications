import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { TenantSettings } from '../core/models';
import { FieldIssue, FormErrors, FormErrorsComponent, NaFieldError, Pending, submit, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { TenantLinkPipe, problemText } from '../shared/ui';
import { tenantRights } from '../shared/rights';

/** The API's bounds (TenantSettingsHandler). */
const MAX_STORM_THRESHOLD = 100_000;
const MAX_RETENTION_DAYS = 3650;

type Field = 'stormThreshold' | 'requestRetentionDays' | 'contentRetentionDays';
const FIELDS: readonly Field[] = ['stormThreshold', 'requestRetentionDays', 'contentRetentionDays'];
type Draft = Record<Field, string>;

function blank(): Draft {
  return { stormThreshold: '', requestRetentionDays: '', contentRetentionDays: '' };
}

/** A stored setting as the form holds it: empty for "the deployment's default". */
function drafted(s: TenantSettings): Draft {
  const text = (n: number | null) => (n === null ? '' : String(n));
  return { stormThreshold: text(s.stormThreshold), requestRetentionDays: text(s.requestRetentionDays), contentRetentionDays: text(s.contentRetentionDays) };
}

/**
 * The tenant's own settings (phase-4-plan §4): when a route storms (I43), and how long requests, deliveries and their rendered content are
 * kept. Each left empty takes the deployment's default, shown beside it; a save sends all three.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, TenantLinkPipe,
    NaFieldError, FormErrorsComponent],
  template: `
    <div class="na-head"><h1>Settings</h1><span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    @if (settings.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (settings.error()) { <p class="na-error" data-testid="settings-error">{{ error() }}</p> }
    @if (settings.value(); as s) {
      <form class="na-form na-card" (ngSubmit)="save(s)" (input)="saved.set(false)" data-testid="settings-form">
        <h2>Storms</h2>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Storm threshold</mat-label>
            <input matInput name="stormThreshold" [(ngModel)]="draft.stormThreshold" inputmode="numeric" [placeholder]="'' + s.defaultStormThreshold"
              data-testid="settings-storm-threshold" [naError]="errors" naPointer="/stormThreshold">
            <mat-hint>Notifications a route takes in a minute before it storms; 0 never. Empty: the default, {{ s.defaultStormThreshold }}.</mat-hint>
            @for (e of errors.of('/stormThreshold'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
        </div>
        <p class="na-hint">A storming route sends a digest instead of each notification. <a [routerLink]="['storms'] | naTenant: tenantId()">Storms</a>
          lists the current and past ones.</p>

        <h2>Retention</h2>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Requests and deliveries (days)</mat-label>
            <input matInput name="requestRetentionDays" [(ngModel)]="draft.requestRetentionDays" inputmode="numeric"
              [placeholder]="'' + s.defaultRequestRetentionDays" data-testid="settings-request-days" [naError]="errors" naPointer="/requestRetentionDays">
            <mat-hint>Empty: the default, {{ s.defaultRequestRetentionDays }}.</mat-hint>
            @for (e of errors.of('/requestRetentionDays'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Rendered content (days)</mat-label>
            <input matInput name="contentRetentionDays" [(ngModel)]="draft.contentRetentionDays" inputmode="numeric"
              [placeholder]="'' + s.defaultContentRetentionDays" data-testid="settings-content-days" [naError]="errors" naPointer="/contentRetentionDays">
            <mat-hint data-testid="settings-content-hint">{{ contentHint(s) }}</mat-hint>
            @for (e of errors.of('/contentRetentionDays'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
          </mat-form-field>
        </div>
        <p class="na-hint">Older requests and deliveries are removed with their history; content (the rendered subject and body) goes first, so
          a delivery can be investigated after what it said is gone.</p>
        <na-form-errors [errors]="errors" [shown]="pointers" [reload]="reloadFn" />
        <div class="na-actions">
          @if (can.configure()) { <button mat-flat-button type="submit" [disabled]="busy()" data-testid="settings-save">Save</button> }
          @if (saved()) { <span role="status" data-testid="settings-saved">Saved.</span> }
          <span class="na-spacer"></span>
          <span class="na-muted na-small">{{ s.updatedAt ? 'Changed ' + (s.updatedAt | date: 'MMM d, y, HH:mm') : 'Never changed' }}</span>
        </div>
      </form>
    }`,
})
export class SettingsPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  /** The user's rights here: controls they may not use are hidden (the API refuses them anyway). */
  protected readonly can = tenantRights(() => this.tenantId());

  protected readonly settings = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.settings(params) });
  protected readonly error = computed(() => problemText(this.settings.error()));
  protected readonly pointers = FIELDS.map(f => `/${f}`);
  protected readonly errors = new FormErrors();
  protected readonly busy = signal(false);
  protected readonly saved = signal(false);
  protected readonly reloadFn = () => this.reload();
  protected draft = blank();
  /** The fields as last read: one still showing them takes what is read next; one typed over keeps the typing. */
  private loaded = blank();
  private readonly pending = new Pending();

  constructor() {
    // Another tenant (the page reused): nothing typed or sent for the last one carries over.
    whenChanged(() => this.tenantId(), () => {
      this.pending.cancel();
      this.busy.set(false);
      this.errors.clear();
      this.saved.set(false);
      this.draft = blank();
      this.loaded = blank();
    });
    whenChanged(() => this.settings.value(), () => {
      const s = this.settings.value();
      if (!s) return;
      const read = drafted(s);
      const next = { ...this.draft };
      for (const f of FIELDS) if (this.draft[f] === this.loaded[f]) next[f] = read[f];
      this.draft = next;
      this.loaded = read;
    });
  }

  /**
   * Content's default, and what it comes to: requests kept fewer days than content's default take their deliveries' content with them
   * (the sweep removes the deliveries at the requests' days), so an empty field then keeps content only that long.
   */
  protected contentHint(s: TenantSettings): string {
    const typed = this.draft.requestRetentionDays.trim();
    const n = Number(typed);
    const requestDays = /^\d+$/.test(typed) && n >= 1 && n <= MAX_RETENTION_DAYS ? n : s.defaultRequestRetentionDays;
    const fallback = s.defaultContentRetentionDays;
    return fallback > requestDays
      ? `At most the requests' days. Empty: the default, ${fallback}, so ${requestDays} here: content goes with its requests.`
      : `At most the requests' days. Empty: the default, ${fallback}.`;
  }

  protected reload(): void {
    this.errors.clear();
    this.saved.set(false);
    this.settings.reload();
  }

  protected save(s: TenantSettings): void {
    this.saved.set(false);
    const issues: FieldIssue[] = [];
    // Empty is the default (null); a number in range is itself; anything else is undefined, with its issue named for its field.
    const read = (f: Field, label: string, min: number, max: number, rule: string): number | null | undefined => {
      const text = this.draft[f].trim();
      if (!text) return null;
      const n = Number(text);
      if (!/^\d+$/.test(text) || n < min || n > max) {
        issues.push({ pointer: `/${f}`, message: `${label}: ${rule}; empty for the default.` });
        return undefined;
      }
      return n;
    };
    const stormThreshold = read('stormThreshold', 'Storm threshold', 0, MAX_STORM_THRESHOLD,
      `a whole number of notifications a minute, 0 (never storms) to ${MAX_STORM_THRESHOLD}`);
    const requestRetentionDays = read('requestRetentionDays', 'Requests and deliveries', 1, MAX_RETENTION_DAYS, `whole days, 1 to ${MAX_RETENTION_DAYS}`);
    // Content goes no later than its request: at most the requests' days, as they will be (typed, or the default).
    const requestDays = requestRetentionDays ?? s.defaultRequestRetentionDays;
    const contentRetentionDays = read('contentRetentionDays', 'Rendered content', 1, Math.min(requestDays, MAX_RETENTION_DAYS),
      `whole days, 1 to the requests' ${requestDays}`);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    this.pending.track(submit(this.api.putSettings(this.tenantId(), { stormThreshold: stormThreshold ?? null,
      requestRetentionDays: requestRetentionDays ?? null, contentRetentionDays: contentRetentionDays ?? null }), this.busy,
      this.errors, () => {
        this.saved.set(true);
        this.settings.reload();
      }));
  }
}
