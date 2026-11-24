import { DatePipe, JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { Subscription, catchError, debounceTime, map, of, switchMap } from 'rxjs';
import { NotificationsAdminApi } from '../core/api.service';
import { TemplatePreview, TenantTypeDetail } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, submit, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { TenantLinkPipe, problemText } from '../shared/ui';

/** The locale every type has; others only as the type declares them (the API refuses a tenant's own in any other). */
const DEFAULT_LOCALE = 'en';

/** A preview's part, as the page shows it: the HTML of an email in a sandboxed frame, text as text, anything else as JSON. */
interface PreviewPart { key: string; kind: 'html' | 'text' | 'json'; value: unknown; html?: SafeHtml; }

/**
 * One type the tenant receives (phase-4c-plan §4, M39): its latest version as declared, its versions and data schema, where its inbox items
 * go in this tenant, and its templates, channel by channel and locale by locale. The tenant's own template is edited beside a preview
 * rendered by the API with the type's sample data (or data typed in); saved, it is used instead of the application's until removed. Moved
 * here from `@juice-js/notifications` (p4c-d5).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, JsonPipe, FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule,
    TenantLinkPipe, FieldErrorsComponent, FormErrorsComponent],
  template: `
    <div class="na-head">
      <a [routerLink]="['types'] | naTenant: tenantId()">Types</a><span class="na-muted">/</span>
      <h1 class="na-mono" data-testid="type-title">{{ key() }}</h1>
      <span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="refresh()" data-testid="refresh">Refresh</button>
    </div>
    @if (detail.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (detail.error()) { <p class="na-error" data-testid="type-error">{{ detailError() }}</p> }
    @if (detail.value(); as t) {
      <div class="na-grid-2">
        <section class="na-card">
          <h2>{{ t.name }}</h2>
          <table class="na-kv" data-testid="type-facts"><tbody>
            <tr><th>Application</th><td>{{ t.application }}</td></tr>
            <tr><th>Version</th><td>{{ t.version }}</td></tr>
            <tr><th>Category</th><td>{{ t.category }}</td></tr>
            <tr><th>Priority</th><td>{{ t.priority }}</td></tr>
            <tr><th>Mandatory</th><td>{{ t.mandatory ? 'yes: preferences and mutes by pattern never hold it' : 'no' }}</td></tr>
            <tr><th>External recipients</th><td>{{ t.externalRecipients ? 'allowed' : 'no' }}</td></tr>
            <tr><th>Correlation key</th><td>{{ t.correlation }}</td></tr>
          </tbody></table>
        </section>
        <section class="na-card" data-testid="type-group">
          <h2>Display group</h2>
          <p class="na-hint">Where its items go in people's inboxes here. The type declares <span class="na-mono">{{ t.displayGroup }}</span>;
            its items go to <strong data-testid="type-group-now">{{ groupName(t.group) }}</strong>{{ t.groupChosen ? ', as the tenant chose' : '' }}.</p>
          <!-- Made afresh after a refused move: ngModel doesn't rewrite a select whose bound value didn't change. -->
          @for (g of [groupGeneration()]; track g) {
          <div class="na-filters">
            <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Group</mat-label>
              <mat-select [ngModel]="t.groupChosen ? t.group : ''" (ngModelChange)="move($event)" [disabled]="busy() || !groups.hasValue()"
                data-testid="type-group-select">
                <mat-option value="">The type's own</mat-option>
                @for (g of groups.value() ?? []; track g.key) { <mat-option [value]="g.key">{{ g.name }} ({{ g.key }})</mat-option> }
              </mat-select></mat-form-field>
          </div>
          }
          @if (groups.error()) { <p class="na-error">The display groups couldn't be read: {{ groupsError() }}</p> }
          <na-form-errors [errors]="groupErrors" />
          @if (moved()) { <p class="na-small" data-testid="type-group-moved">{{ moved() }}</p> }
        </section>
      </div>

      <section class="na-card" data-testid="templates">
        <h2>Templates</h2>
        @if (t.serviceType) {
          <p class="na-notice" data-testid="templates-service">The service's own type: its templates stay as they are.</p>
        }
        <div class="na-filters">
          <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Channel</mat-label>
            <mat-select [ngModel]="channel()" (ngModelChange)="pick($event, defaultLocale)" [disabled]="dirty()" data-testid="template-channel">
              @for (c of t.channels; track c.channel) {
                <mat-option [value]="c.channel">{{ c.channel }}{{ c.overridden ? ' (own)' : c.declared ? '' : ' (none)' }}</mat-option>
              }
            </mat-select></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Locale</mat-label>
            <mat-select [ngModel]="locale()" (ngModelChange)="pick(channel(), $event)" [disabled]="dirty()" data-testid="template-locale">
              @for (l of locales(); track l.locale) { <mat-option [value]="l.locale">{{ l.locale }}{{ l.overridden ? ' (own)' : '' }}</mat-option> }
            </mat-select></mat-form-field>
          @if (dirty()) { <span class="na-muted na-small">Save or revert to pick another.</span> }
        </div>
        @if (locale() !== defaultLocale) { <p class="na-hint">Deliveries render {{ defaultLocale }} today; other locales are kept for when they don't.</p> }
        @if (template.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
        @if (template.error()) { <p class="na-error" data-testid="template-error">{{ templateError() }}</p> }
        @if (template.value(); as tpl) {
          <div class="na-grid-2">
            <div>
              <p class="na-small" data-testid="template-source">
                @if (tpl.tenant !== null) { The tenant's own (version {{ tpl.tenantVersion }}, {{ tpl.updatedAt | date: 'MMM d, y, HH:mm' }}), used instead of the application's. }
                @else if (tpl.application !== null) { The application's. Change it and save to make it the tenant's own. }
                @else { No template on this channel{{ locale() === defaultLocale ? '' : ' in this locale' }}: one written here is the tenant's own. }
              </p>
              <mat-form-field subscriptSizing="dynamic" class="na-field-full"><mat-label>Template (Scriban)</mat-label>
                <textarea matInput class="na-code" rows="14" spellcheck="false" [ngModel]="body()" (ngModelChange)="edited($event)" [disabled]="t.serviceType"
                  data-testid="template-body"></textarea>
              </mat-form-field>
              <p class="na-hint">Data as <code ngNonBindable>{{ data.field }}</code>. A first line <code>Subject:</code> (email) or <code>Title:</code>
                (inbox, chat, push) sets the heading. <code>passthrough</code> sends the data as is where the channel allows it.</p>
              <na-field-errors [messages]="errors.at('/body')" pointer="/body" />
              <na-field-errors [messages]="errors.at('/locale')" pointer="/locale" />
              <na-form-errors [errors]="errors" [shown]="['/body', '/locale']" />
              @if (changedUnder()) {
                <p class="na-notice" data-testid="template-changed">The stored template changed while you were editing; your text is kept. Revert shows the new one.</p>
              }
              @if (!t.serviceType) {
                <div class="na-actions">
                  <button mat-flat-button type="button" (click)="save()" [disabled]="busy() || !dirty() || !body().trim()" data-testid="template-save">Save</button>
                  <button mat-button type="button" (click)="revert()" [disabled]="busy() || !dirty()" data-testid="template-revert">Revert</button>
                  @if (tpl.tenant !== null) {
                    @if (confirmingRemove()) {
                      <span class="na-confirm">Use the application's again{{ tpl.application === null ? ' (it has none here: nothing would be sent on this channel by a template)' : '' }}?
                        <button mat-flat-button type="button" (click)="remove()" [disabled]="busy()" data-testid="template-remove-confirm">Use it</button>
                        <button mat-button type="button" (click)="confirmingRemove.set(false)">Keep ours</button></span>
                    } @else {
                      <button mat-button type="button" (click)="confirmingRemove.set(true)" [disabled]="busy() || dirty()" data-testid="template-remove">
                        Use the application's…</button>
                    }
                  }
                  @if (saved()) { <span role="status" data-testid="template-saved">{{ saved() }}</span> }
                </div>
              }
              <mat-form-field subscriptSizing="dynamic" class="na-field-full"><mat-label>Sample data (JSON)</mat-label>
                <textarea matInput class="na-code" rows="8" spellcheck="false" [ngModel]="dataShown()" (ngModelChange)="data.set($event)" data-testid="template-data"></textarea>
                <mat-hint>Empty: a sample from the type's schema.</mat-hint>
              </mat-form-field>
            </div>
            <div class="na-preview" aria-live="polite" data-testid="preview">
              <h3>Preview</h3>
              @if (preview(); as p) {
                @if (p.error) { <p class="na-error" data-testid="preview-error">{{ p.error }}</p> }
                @for (part of parts(); track part.key) {
                  <div class="na-preview-part" data-testid="preview-part" [attr.data-part]="part.key">
                    <div class="na-muted na-small">{{ part.key }}</div>
                    @switch (part.kind) {
                      @case ('html') { <iframe class="na-preview-html" sandbox="" [srcdoc]="part.html" title="The email's HTML"></iframe> }
                      @case ('text') { <pre class="na-pre">{{ part.value }}</pre> }
                      @default { <pre class="na-pre">{{ part.value | json }}</pre> }
                    }
                  </div>
                }
              } @else { <p class="na-muted">Rendering…</p> }
            </div>
          </div>
        }
      </section>

      <div class="na-grid-2">
        <section class="na-card">
          <h2>Versions</h2>
          <table class="na-table" data-testid="type-versions">
            <thead><tr><th>Version</th><th>Name</th><th>Schema</th><th>Declared</th></tr></thead>
            <tbody>
              @for (v of newestFirst(t); track v.version) {
                <tr><td>{{ v.version }}</td><td>{{ v.name }}</td><td class="na-mono na-small" [title]="v.schemaHash">{{ v.schemaHash.slice(0, 12) }}</td>
                  <td class="na-nowrap">{{ v.createdAt | date: 'MMM d, y, HH:mm' }}</td></tr>
              }
            </tbody>
          </table>
        </section>
        <section class="na-card">
          <h2>Data schema</h2>
          <pre class="na-pre" data-testid="type-schema">{{ t.schema | json }}</pre>
        </section>
      </div>
    }`,
})
export class TypePage {
  private readonly api = inject(NotificationsAdminApi);
  private readonly sanitizer = inject(DomSanitizer);
  readonly tenantId = input.required<string>();
  readonly key = input.required<string>();
  protected readonly defaultLocale = DEFAULT_LOCALE;

  protected readonly detail = apiResource({ params: () => ({ tenantId: this.tenantId(), key: this.key() }),
    stream: ({ params }) => this.api.type(params.tenantId, params.key) });
  protected readonly groups = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.displayGroups(params) });
  protected readonly detailError = computed(() => problemText(this.detail.error()));
  protected readonly groupsError = computed(() => problemText(this.groups.error()));

  protected readonly channel = signal<string | null>(null);
  protected readonly locale = signal(DEFAULT_LOCALE);
  /** The channel's locales: the default one always, and those the type declares or the tenant has. */
  protected readonly locales = computed(() => {
    const found = this.detail.value()?.channels.find(c => c.channel === this.channel())?.locales ?? [];
    return found.some(l => l.locale === DEFAULT_LOCALE) ? found : [{ locale: DEFAULT_LOCALE, declared: false, overridden: false }, ...found];
  });
  protected readonly template = apiResource({
    params: () => {
      const channel = this.channel();
      return channel ? { tenantId: this.tenantId(), key: this.key(), channel, locale: this.locale() } : undefined;
    },
    stream: ({ params }) => this.api.template(params.tenantId, params.key, params.channel, params.locale),
  });
  protected readonly templateError = computed(() => problemText(this.template.error()));

  protected readonly body = signal('');
  /** The text as read: what Revert puts back, and what "changed" is measured against. */
  private readonly loaded = signal('');
  protected readonly dirty = computed(() => this.template.hasValue() && this.body() !== this.loaded());
  /** The sample data typed in; empty, the preview uses the type's sample. */
  protected readonly data = signal('');
  /** The sample the API made up from the schema, for the type it was made for (never another's, answered late). */
  private readonly sample = signal({ for: '', text: '' });
  /** What the data field shows: what was typed, or the type's sample, so it can be changed. Not the preview's input: showing it sends nothing. */
  protected readonly dataShown = computed(() => {
    const sample = this.sample();
    return this.data() || (sample.for === this.at() ? sample.text : '');
  });
  /** The stored template changed (a Refresh, someone else's save) while the editor held unsaved text, which was kept. */
  protected readonly changedUnder = signal(false);
  protected readonly errors = new FormErrors();
  protected readonly groupErrors = new FormErrors();
  protected readonly busy = signal(false);
  protected readonly saved = signal('');
  protected readonly moved = signal('');
  protected readonly confirmingRemove = signal(false);
  protected readonly groupGeneration = signal(0);

  /** The type being worked on: answers for another (the page reused) are dropped. */
  private readonly at = computed(() => `${this.tenantId()}|${this.key()}`);
  /** Saves and moves still running, for the type they were sent for. */
  private pending: Subscription[] = [];
  /** Which template the editor holds (another channel or locale fills it afresh), and the text it was filled with. */
  private held = '';

  /** Rendered by the API as the text and data change (half a second after the last keystroke); never stored. */
  protected readonly preview = toSignal(toObservable(computed(() => {
    const t = this.template.value();
    return t ? { tenantId: this.tenantId(), at: this.at(), type: t.type, channel: t.channel, locale: t.locale, body: this.body(), data: this.data() } : null;
  })).pipe(debounceTime(500), switchMap(p => {
    if (!p) return of(null);
    let data: unknown = null;
    if (p.data.trim()) {
      try {
        data = JSON.parse(p.data);
      } catch (e) {
        return of<TemplatePreview>({ channel: p.channel, content: null, error: `The sample data isn't JSON: ${(e as Error).message}`, data: null });
      }
    }
    // An empty editor previews the template in effect in the locale picked (the API's own choice), not an empty one.
    return this.api.preview(p.tenantId, p.type, p.channel, p.locale, p.body.trim() ? p.body : null, data).pipe(
      map(result => {
        // The sample the API made up is shown, so it can be changed; kept apart from what was typed, so a late answer overwrites nothing.
        if (!p.data.trim()) this.sample.set({ for: p.at, text: JSON.stringify(result.data, null, 2) });
        return result;
      }),
      catchError(e => of<TemplatePreview>({ channel: p.channel, content: null, error: problemText(e), data: null })));
  })), { initialValue: null });

  protected readonly parts = computed<PreviewPart[]>(() => Object.entries(this.preview()?.content ?? {}).map(([key, value]) => {
    if (typeof value === 'string' && key.toLowerCase() === 'html') {
      // Sandboxed with nothing allowed (no scripts, no forms, no navigation): the email as it would look, and nothing more.
      return { key, kind: 'html', value, html: this.sanitizer.bypassSecurityTrustHtml(value) };
    }
    return { key, kind: typeof value === 'string' ? 'text' : 'json', value };
  }));

  constructor() {
    // Another type (the page reused): start from its first channel with a template.
    whenChanged(() => this.at(), () => {
      // Whatever the last type's saves and moves answer is no longer this page's.
      this.pending.forEach(s => s.unsubscribe());
      this.pending = [];
      this.busy.set(false);
      this.channel.set(null);
      this.locale.set(DEFAULT_LOCALE);
      this.data.set('');
      this.saved.set('');
      this.moved.set('');
      this.errors.clear();
      this.groupErrors.clear();
    });
    whenChanged(() => this.detail.value(), () => {
      const t = this.detail.value();
      if (t && !t.channels.some(c => c.channel === this.channel())) {
        this.channel.set((t.channels.find(c => c.overridden || c.declared) ?? t.channels[0])?.channel ?? null);
      }
    });
    whenChanged(() => this.template.value(), () => this.read());
  }

  /**
   * The template read (or read again): another one fills the editor; the same one again (Refresh, after a save, someone else's change) fills
   * it only when nothing is unsaved. Unsaved text is kept, and Revert then puts back what is stored now.
   */
  private read(): void {
    const t = this.template.value();
    // Loading or failed: the editor isn't shown, and what it holds waits for the next read.
    if (!t) return;
    const which = `${this.tenantId()}|${t.type}|${t.channel}|${t.locale}`;
    const text = t.tenant ?? t.application ?? '';
    if (which !== this.held || this.body() === this.loaded()) {
      this.held = which;
      this.body.set(text);
      this.loaded.set(text);
      this.changedUnder.set(false);
      this.errors.clear();
      this.confirmingRemove.set(false);
    } else if (text !== this.loaded()) {
      this.loaded.set(text);
      // Saved as typed: nothing is unsaved any more. Otherwise the stored one moved under the edit.
      this.changedUnder.set(this.body() !== text);
    }
  }

  /** A save or move for this type; dropped if the page moves on to another. */
  private track(s: Subscription): void {
    this.pending = [...this.pending.filter(p => !p.closed), s];
  }

  protected groupName(key: string): string {
    const group = this.groups.value()?.find(g => g.key === key);
    return group ? `${group.name} (${key})` : key;
  }

  protected newestFirst(t: TenantTypeDetail) {
    return [...t.versions].reverse();
  }

  protected refresh(): void {
    this.detail.reload();
    this.groups.reload();
    this.template.reload();
  }

  /** Another channel or locale (only with nothing unsaved: the selects are off while there is). */
  protected pick(channel: string | null, locale: string): void {
    this.channel.set(channel);
    // A locale the new channel doesn't have falls back to the default one.
    const has = this.detail.value()?.channels.find(c => c.channel === channel)?.locales.some(l => l.locale === locale);
    this.locale.set(has || locale === DEFAULT_LOCALE ? locale : DEFAULT_LOCALE);
    this.saved.set('');
  }

  protected edited(text: string): void {
    this.body.set(text);
    this.saved.set('');
  }

  protected revert(): void {
    this.body.set(this.loaded());
    this.changedUnder.set(false);
    this.errors.clear();
  }

  protected save(): void {
    const t = this.template.value();
    if (!t) return;
    this.track(submit(this.api.putTemplate(this.tenantId(), this.key(), t.channel, t.locale, this.body()), this.busy, this.errors, () => {
      this.saved.set('Saved: the tenant\'s own from now on.');
      this.template.reload();
      this.detail.reload();
    }));
  }

  protected remove(): void {
    const t = this.template.value();
    if (!t) return;
    this.track(submit(this.api.deleteTemplate(this.tenantId(), this.key(), t.channel, t.locale), this.busy, this.errors, () => {
      this.confirmingRemove.set(false);
      this.saved.set('The application\'s template is in effect again.');
      this.template.reload();
      this.detail.reload();
    }));
  }

  /** The group the type's items go to here: '' puts it back in the one it declares. Open items move at once. */
  protected move(group: string): void {
    this.moved.set('');
    this.track(submit(this.api.moveType(this.tenantId(), this.key(), group || null), this.busy, this.groupErrors, answer => {
      this.moved.set(`Its items go to ${this.groupName(answer.group)} now; ${answer.itemsMoved} open item(s) moved.`);
      this.detail.reload();
      this.groups.reload();
    // Refused: the type read again, and the select made afresh, so it shows where the type really is.
    }, () => { this.detail.reload(); this.groupGeneration.update(g => g + 1); }));
  }
}
