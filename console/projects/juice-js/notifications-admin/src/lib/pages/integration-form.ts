import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { NotificationsAdminApi } from '../core/api.service';
import { Integration, Provider } from '../core/models';
import { FieldErrorsComponent, FormErrors, FormErrorsComponent, KEY_PATTERN, KIND_HINTS, SECRET_REF_HINT, SETTINGS_EXAMPLES, isBlankNumber, submit } from '../shared/forms';

/**
 * An integration's fields (design §9, docs/providers.md): a new one's key, channel and provider kind, then its settings (JSON), the
 * reference to its secret (I53: never the secret), its priority among the channel's integrations (failover order) and whether it is on.
 * An existing one keeps its key and channel; the API refuses a change of either.
 */
@Component({
  selector: 'na-integration-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, FieldErrorsComponent,
    FormErrorsComponent],
  template: `
    <form class="na-form" (ngSubmit)="save()" data-testid="integration-form">
      <!-- Made afresh on each fill: ngModel writes nothing to an input whose model value is unchanged, so a field typed over would keep its text. -->
      @for (g of [generation()]; track g) {
      <div class="na-filters">
        <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
          <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!!existing()" required [pattern]="keyPattern" data-testid="integration-key"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Channel</mat-label>
          <mat-select name="channel" [(ngModel)]="draft.channel" (ngModelChange)="channelChanged()" [disabled]="!!existing()" required
            data-testid="integration-channel">
            @for (c of channels(); track c) { <mat-option [value]="c">{{ c }}</mat-option> }
          </mat-select></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Provider</mat-label>
          <mat-select name="kind" [(ngModel)]="draft.kind" (ngModelChange)="kindChanged()" required data-testid="integration-kind">
            @for (p of kinds(); track p.kind) {
              <mat-option [value]="p.kind">{{ p.kind }}@if (p.unlisted) { <span class="na-muted"> (not in this deployment)</span> }</mat-option>
            }
          </mat-select></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Priority</mat-label>
          <input matInput type="number" name="priority" [(ngModel)]="draft.priority" min="0" data-testid="integration-priority">
          <mat-hint>Lower first; the next one takes over on failure.</mat-hint></mat-form-field>
        <mat-checkbox name="enabled" [(ngModel)]="draft.enabled" data-testid="integration-enabled">Enabled</mat-checkbox>
      </div>
      <na-field-errors [messages]="errors.at('/key')" pointer="/key" />
      <na-field-errors [messages]="errors.at('/channel')" pointer="/channel" />
      <na-field-errors [messages]="errors.at('/kind')" pointer="/kind" />
      <na-field-errors [messages]="errors.at('/priority')" pointer="/priority" />
      <mat-form-field subscriptSizing="dynamic"><mat-label>Settings (JSON)</mat-label>
        <textarea matInput name="settings" [(ngModel)]="draft.settings" rows="6" spellcheck="false" data-testid="integration-settings"></textarea>
      </mat-form-field>
      <p class="na-hint">The provider's settings, as docs/providers.md lists them. No secrets here: the secret reference names where they are.</p>
      <na-field-errors [messages]="errors.at('/settings')" pointer="/settings" />
      <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Secret reference</mat-label>
        <input matInput name="secretRef" [(ngModel)]="draft.secretRef" autocomplete="off" spellcheck="false" placeholder="env:NOTIFICATIONS_SECRET_…"
          data-testid="integration-secret-ref"></mat-form-field>
      <p class="na-hint">{{ secretHint() }}</p>
      <na-field-errors [messages]="errors.at('/secretRef')" pointer="/secretRef" />
      <na-form-errors [errors]="errors" [shown]="shown" [reload]="reloadFn()" />
      <div class="na-actions">
        <button mat-flat-button type="submit" [disabled]="busy()" data-testid="integration-save">{{ existing() ? 'Save' : 'Create' }}</button>
        <button mat-button type="button" (click)="cancel()">Cancel</button>
      </div>
      }
    </form>`,
})
export class IntegrationForm {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  readonly providers = input.required<Provider[]>();
  /** The integration edited; null for a new one. */
  readonly existing = input<Integration | null>(null);
  /** The tenant's keys: a new integration can't take one (a PUT to it would change that integration instead). */
  readonly takenKeys = input<string[]>([]);
  /** Reads the integration again (after a conflict). */
  readonly reloadFn = input<(() => void) | null>(null);
  readonly saved = output<Integration>();
  readonly cancelled = output<void>();

  protected readonly keyPattern = KEY_PATTERN;
  protected readonly shown = ['/key', '/channel', '/kind', '/priority', '/settings', '/secretRef'];
  protected readonly errors = new FormErrors();
  protected readonly busy = signal(false);
  protected draft = { key: '', channel: '', kind: '', settings: '{}', secretRef: '', priority: 0, enabled: true };
  private readonly channel = signal('');
  private readonly kind = signal('');
  protected readonly generation = signal(0);

  // An existing integration's channel and kind stay options though the deployment no longer lists them: an empty select would hide what is
  // stored, and invite picking another provider.
  protected readonly channels = computed(() => [...new Set([...this.providers().map(p => p.channel), ...(this.existing() ? [this.existing()!.channel] : [])])]);
  protected readonly kinds = computed(() => {
    const listed: (Provider & { unlisted?: boolean })[] = this.providers().filter(p => p.channel === this.channel());
    const i = this.existing();
    return i && i.channel === this.channel() && !listed.some(p => p.kind === i.kind)
      ? [...listed, { channel: i.channel, kind: i.kind, signs: false, registersCallbacks: false, unlisted: true }] : listed;
  });
  protected readonly secretHint = computed(() => {
    const provider = this.providers().find(p => p.channel === this.channel() && p.kind === this.kind());
    const own = KIND_HINTS[this.kind()]?.secret;
    return [provider?.signs ? 'Required: this provider signs what it sends.' : '', own ?? '', SECRET_REF_HINT].filter(Boolean).join(' ');
  });

  /**
   * Which version of the integration the fields hold: its key and when it last changed. A page reload that reads the same version (after a
   * target is added, or Refresh) leaves what is being typed alone; a new version (this form's save, or someone else's) fills them afresh.
   */
  private readonly version = computed(() => {
    const i = this.existing();
    return i ? `${i.key}|${i.updatedAt}` : '';
  });

  constructor() {
    effect(() => {
      this.version();
      untracked(() => this.fill());
    });
  }

  /** The fields as the integration was read. */
  private fill(): void {
    const i = this.existing();
    if (i) {
      this.draft = { key: i.key, channel: i.channel, kind: i.kind, settings: JSON.stringify(i.settings ?? {}, null, 2),
        secretRef: i.secretRef ?? '', priority: i.priority, enabled: i.enabled };
    }
    this.channel.set(this.draft.channel);
    this.kind.set(this.draft.kind);
    this.errors.clear();
    this.generation.update(g => g + 1);
  }

  /** Puts back what was read, then lets the page know. */
  protected cancel(): void {
    this.fill();
    this.cancelled.emit();
  }

  protected channelChanged(): void {
    this.channel.set(this.draft.channel);
    const kinds = this.kinds();
    this.draft.kind = kinds.length === 1 ? kinds[0].kind : '';
    this.kindChanged();
  }

  /** A new integration starts from the kind's example settings, unless some were typed already. */
  protected kindChanged(): void {
    this.kind.set(this.draft.kind);
    const typed = this.draft.settings.trim();
    const isExample = typed === '' || typed === '{}' || Object.values(SETTINGS_EXAMPLES).some(e => JSON.stringify(e, null, 2) === typed);
    if (!this.existing() && isExample) this.draft.settings = JSON.stringify(SETTINGS_EXAMPLES[this.draft.kind] ?? {}, null, 2);
  }

  protected save(): void {
    let settings: unknown;
    try {
      settings = this.draft.settings.trim() ? JSON.parse(this.draft.settings) : {};
    } catch (e) {
      this.errors.local('/settings', `The settings aren't JSON: ${(e as Error).message}`);
      return;
    }
    // The API takes an object only; a null would read as "left out" and keep the stored settings without a word.
    if (typeof settings !== 'object' || settings === null || Array.isArray(settings)) {
      this.errors.local('/settings', 'Settings are a JSON object: { "name": value, … }.');
      return;
    }
    // A cleared priority reads null, which would be sent as 0: first in the channel's failover order, not what was meant.
    if (isBlankNumber(this.draft.priority) || Number(this.draft.priority) < 0) {
      this.errors.local('/priority', 'Give the priority: 0 or more, lower tried first.');
      return;
    }
    // A secret reference left out keeps the stored one (the API's PUT keeps what isn't sent): emptied, it would stay without a word.
    const secretRef = this.draft.secretRef.trim();
    if (!secretRef && this.existing()?.secretRef) {
      this.errors.local('/secretRef', "A secret reference can be changed but not removed: the stored one stays. Point it at another, or create an integration without one.");
      return;
    }
    const key = this.draft.key.trim();
    if (!key) {
      this.errors.local('/key', 'Give the integration a key.');
      return;
    }
    if (!this.existing() && this.takenKeys().includes(key)) {
      this.errors.local('/key', `There is an integration ${key} already: open it to change it.`);
      return;
    }
    submit(this.api.putIntegration(this.tenantId(), key, { channel: this.draft.channel, kind: this.draft.kind, settings: settings as Record<string, unknown>,
      secretRef: secretRef || null, priority: Number(this.draft.priority), enabled: this.draft.enabled }), this.busy, this.errors,
      saved => this.saved.emit(saved));
  }
}
