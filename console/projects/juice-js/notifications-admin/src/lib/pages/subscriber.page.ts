import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { Alias, Subscriber } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, KEY_PATTERN, Pending, rowAction, submit, whenChanged } from '../shared/forms';
import { LOCAL_SOURCE, personName } from '../shared/people';
import { apiResource } from '../shared/resource';
import { TenantLinkPipe, problemText } from '../shared/ui';

/** A local user's fields, as the form holds them (empty for none). */
function blankLocal() {
  return { name: '', locale: '', timeZone: '' };
}

/**
 * One subscriber (design D23): who they are, and the other identities linked to them, so a request naming any of them reaches this one
 * person. Linking the identity of another subscriber links that subscriber into this one (one level: theirs then reach here). A local user's
 * name, locale and time zone are changed here; others come from their identity directory.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, TenantLinkPipe,
    FieldErrorsComponent, FormErrorsComponent],
  template: `
    <div class="na-head">
      <a [routerLink]="['subscribers'] | naTenant: tenantId()">Subscribers</a><span class="na-muted">/</span>
      <h1 data-testid="subscriber-title">{{ subscriber.value() ? name(subscriber.value()!) : id() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button>
    </div>
    @if (subscriber.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (subscriber.error()) { <p class="na-error" data-testid="subscriber-error">{{ error() }}</p> }
    @if (subscriber.value(); as s) {
      @if (s.mergedInto) {
        <p class="na-notice" data-testid="subscriber-merged">Linked into
          <a [routerLink]="['subscribers', s.mergedInto] | naTenant: tenantId()">another subscriber</a>, who receives for this identity.</p>
      }
      <div class="na-grid-2">
        <section class="na-card">
          <h2>Who</h2>
          <table class="na-kv" data-testid="subscriber-facts"><tbody>
            <tr><th>Identity</th><td class="na-mono">{{ s.source }}:{{ s.externalId }}</td></tr>
            <tr><th>Name</th><td>{{ s.name ?? '—' }}</td></tr>
            <tr><th>Locale</th><td>{{ s.locale ?? '—' }}</td></tr>
            <tr><th>Time zone</th><td>{{ s.timeZone ?? '—' }}</td></tr>
            <tr><th>First seen</th><td>{{ s.createdAt | date: 'MMM d, y, HH:mm' }}</td></tr>
            <tr><th>Id</th><td class="na-mono na-small">{{ s.id }}</td></tr>
          </tbody></table>
        </section>
        @if (s.mergedInto) {
          <section class="na-card"><h2>Details</h2>
            <p class="na-hint" data-testid="subscriber-merged-details">Requests naming this identity reach the subscriber it is linked into, with their
              name, locale and time zone: change those there.</p></section>
        } @else if (s.source === localSource) {
          <form class="na-card na-form" (ngSubmit)="saveLocal(s)" (input)="localSaved.set(false)" data-testid="local-user-form">
            <h2>Local user</h2>
            <div class="na-filters">
              <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Name</mat-label>
                <input matInput name="name" [(ngModel)]="local.name" maxlength="200" data-testid="local-user-name"></mat-form-field>
              <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Locale</mat-label>
                <input matInput name="locale" [(ngModel)]="local.locale" maxlength="35" placeholder="en" data-testid="local-user-locale"></mat-form-field>
              <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Time zone</mat-label>
                <input matInput name="timeZone" [(ngModel)]="local.timeZone" maxlength="64" placeholder="Asia/Ho_Chi_Minh" data-testid="local-user-time-zone"></mat-form-field>
            </div>
            @for (p of localFields; track p) { <na-field-errors [messages]="localErrors.at(p)" [pointer]="p" /> }
            <na-form-errors [errors]="localErrors" [shown]="localFields" />
            <div class="na-actions">
              <button mat-flat-button type="submit" [disabled]="busy()" data-testid="local-user-save">Save</button>
              @if (localSaved()) { <span role="status" data-testid="local-user-saved">Saved.</span> }
            </div>
          </form>
        } @else {
          <section class="na-card"><h2>Details</h2>
            <p class="na-hint">Name, locale and time zone come from the {{ s.source }} identity directory each time a request reaches them.</p></section>
        }
      </div>

      <section class="na-card" data-testid="aliases">
        <h2>Linked identities</h2>
        <p class="na-hint">A request naming any of these reaches this person. Linking another subscriber's identity links that subscriber in here.</p>
        @if (!s.mergedInto) {
          <form class="na-filters" (ngSubmit)="link()" data-testid="alias-form">
            <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Source</mat-label>
              <input matInput name="source" [(ngModel)]="alias.source" [pattern]="keyPattern" spellcheck="false" data-testid="alias-source"></mat-form-field>
            <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>External id</mat-label>
              <input matInput name="externalId" [(ngModel)]="alias.externalId" spellcheck="false" data-testid="alias-id"></mat-form-field>
            <button mat-flat-button type="submit" [disabled]="busy() || !alias.source.trim() || !alias.externalId.trim()" data-testid="alias-link">Link</button>
          </form>
          <na-field-errors [messages]="aliasErrors.at('/source')" pointer="/source" />
          <na-field-errors [messages]="aliasErrors.at('/externalId')" pointer="/externalId" />
          <na-form-errors [errors]="aliasErrors" [shown]="['/source', '/externalId']" [reload]="reloadFn" />
        }
        <table class="na-table">
          <thead><tr><th>Identity</th><th>Linked by</th><th>Linked</th><th></th></tr></thead>
          <tbody>
            @for (a of s.aliases; track a.source + ':' + a.externalId) {
              <tr data-testid="alias-row" [attr.data-identity]="a.source + ':' + a.externalId">
                <td class="na-mono">{{ a.source }}:{{ a.externalId }}</td>
                <td>{{ a.linkedBy ?? '—' }}</td>
                <td class="na-nowrap">{{ a.linkedAt | date: 'MMM d, y, HH:mm' }}</td>
                <td class="na-nowrap">
                  @if (confirming() === aliasKey(a)) {
                    <span class="na-confirm">Requests naming it reach {{ a.source }}:{{ a.externalId }} alone again.
                      <button mat-flat-button type="button" (click)="unlink(a)" [disabled]="busy()" data-testid="alias-unlink-confirm">Unlink</button>
                      <button mat-button type="button" (click)="confirming.set(null)">Keep</button></span>
                  } @else {
                    <button mat-button type="button" (click)="confirming.set(aliasKey(a))" data-testid="alias-unlink">Unlink…</button>
                  }
                </td>
              </tr>
            } @empty { <tr><td colspan="4" class="na-muted">None.</td></tr> }
          </tbody>
        </table>
        @if (rowError()) { <p class="na-error" data-testid="alias-row-error">{{ rowError() }}</p> }
      </section>
    }`,
})
export class SubscriberPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  readonly id = input.required<string>();

  protected readonly subscriber = apiResource({ params: () => ({ tenantId: this.tenantId(), id: this.id() }),
    stream: ({ params }) => this.api.subscriber(params.tenantId, params.id) });
  protected readonly error = computed(() => problemText(this.subscriber.error()));
  protected readonly name = personName;
  protected readonly localSource = LOCAL_SOURCE;
  protected readonly keyPattern = KEY_PATTERN;
  protected readonly localFields = ['/name', '/locale', '/timeZone'];
  protected readonly localErrors = new FormErrors();
  protected readonly aliasErrors = new FormErrors();
  protected readonly busy = signal(false);
  protected readonly localSaved = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected local = blankLocal();
  /** The local user's fields as last read: a field still showing them takes what is read next; one typed over keeps the typing. */
  private loaded = blankLocal();
  protected alias = { source: '', externalId: '' };
  protected readonly reloadFn = () => this.reload();
  /** Saves still running, for the subscriber they were sent for. */
  private readonly pending = new Pending();

  constructor() {
    // Another subscriber (the page reused): nothing typed, armed or answered for the last one carries over.
    whenChanged(() => [this.tenantId(), this.id()], () => {
      this.pending.cancel();
      this.local = blankLocal();
      this.loaded = blankLocal();
      this.busy.set(false);
      this.alias = { source: '', externalId: '' };
      this.localErrors.clear();
      this.aliasErrors.clear();
      this.localSaved.set(false);
      this.confirming.set(null);
      this.rowError.set('');
    });
    // The local user's fields from what was read (again, after a link or a refresh), keeping what was typed and not saved.
    whenChanged(() => this.subscriber.value(), () => {
      const s = this.subscriber.value();
      if (!s) return;
      const read = { name: s.name ?? '', locale: s.locale ?? '', timeZone: s.timeZone ?? '' };
      const keys = ['name', 'locale', 'timeZone'] as const;
      const next = { ...this.local };
      for (const k of keys) if (this.local[k] === this.loaded[k]) next[k] = read[k];
      this.local = next;
      this.loaded = read;
    });
  }

  protected aliasKey(a: Alias): string {
    return `${a.source}:${a.externalId}`;
  }

  protected reload(): void {
    this.confirming.set(null);
    this.rowError.set('');
    this.aliasErrors.clear();
    this.localErrors.clear();
    this.localSaved.set(false);
    this.subscriber.reload();
  }

  protected saveLocal(s: Subscriber): void {
    this.localSaved.set(false);
    const l = this.local;
    // Left out, a field keeps what is stored: an emptied one would look cleared and come back.
    const emptied = ([['/name', l.name, s.name], ['/locale', l.locale, s.locale], ['/timeZone', l.timeZone, s.timeZone]] as const)
      .find(([, typed, stored]) => !typed.trim() && stored);
    if (emptied) {
      this.localErrors.local(emptied[0], 'This can be changed but not cleared: the stored value stays. Type another.');
      return;
    }
    this.pending.track(submit(this.api.putLocalUser(this.tenantId(), s.externalId, { name: l.name.trim() || null, locale: l.locale.trim() || null,
      timeZone: l.timeZone.trim() || null }), this.busy, this.localErrors, () => {
      this.localSaved.set(true);
      this.subscriber.reload();
    }));
  }

  protected link(): void {
    const body = { source: this.alias.source.trim(), externalId: this.alias.externalId.trim() };
    this.pending.track(submit(this.api.linkAlias(this.tenantId(), this.id(), body), this.busy, this.aliasErrors, () => {
      this.alias = { source: body.source, externalId: '' };
      this.subscriber.reload();
    }));
  }

  protected unlink(a: Alias): void {
    this.pending.track(rowAction(this.api.unlinkAlias(this.tenantId(), this.id(), a.source, a.externalId),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.subscriber.reload() }));
  }
}
