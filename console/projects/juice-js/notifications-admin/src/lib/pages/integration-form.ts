import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { NotificationsAdminApi } from '../core/api.service';
import { Integration, Provider } from '../core/models';
import { FormErrors, FormErrorsComponent, KIND_HINTS, NaFieldError, SECRET_REF_HINT, SETTINGS_EXAMPLES, isBlankNumber, keyIssue, submit, validateAll } from '../shared/forms';

/**
 * An integration's fields (design §9, docs/providers.md): a new one's key, channel and provider kind, then its settings (JSON), the
 * reference to its secret (I53: never the secret), its priority among the channel's integrations (failover order) and whether it is on.
 * An existing one keeps its key and channel; the API refuses a change of either.
 */
@Component({
  selector: 'na-integration-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, NaFieldError,
    FormErrorsComponent],
  template: `
    <form class="na-form" (ngSubmit)="save()" data-testid="integration-form">
      <!-- Made afresh on each fill: ngModel writes nothing to an input whose model value is unchanged, so a field typed over would keep its text. -->
      @for (g of [generation()]; track g) {
      <div class="na-form-grid">
        <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
          <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!!existing()" required data-testid="integration-key" [naError]="errors" naPointer="/key">
          @for (e of errors.of('/key'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Channel</mat-label>
          <mat-select name="channel" [(ngModel)]="draft.channel" (ngModelChange)="channelChanged()" [disabled]="!!existing()" required
            data-testid="integration-channel" [naError]="errors" naPointer="/channel">
            @for (c of channels(); track c) { <mat-option [value]="c">{{ c }}</mat-option> }
          </mat-select>
          @for (e of errors.of('/channel'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Provider</mat-label>
          <mat-select name="kind" [(ngModel)]="draft.kind" (ngModelChange)="kindChanged()" required data-testid="integration-kind"
            [naError]="errors" naPointer="/kind">
            @for (p of kinds(); track p.kind) {
              <mat-option [value]="p.kind">{{ p.kind }}@if (p.unlisted) { <span class="na-muted"> (not in this deployment)</span> }</mat-option>
            }
          </mat-select>
          @for (e of errors.of('/kind'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Priority</mat-label>
          <input matInput type="number" name="priority" [(ngModel)]="draft.priority" min="0" data-testid="integration-priority"
            [naError]="errors" naPointer="/priority">
          <mat-hint>Lower first; the next one takes over on failure.</mat-hint>
          @for (e of errors.of('/priority'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
        </mat-form-field>
        <mat-checkbox name="enabled" [(ngModel)]="draft.enabled" data-testid="integration-enabled">Enabled</mat-checkbox>
      </div>
      <div class="na-form-grid">
        <mat-form-field subscriptSizing="dynamic" class="na-field-full"><mat-label>Settings (JSON)</mat-label>
          <textarea matInput name="settings" [(ngModel)]="draft.settings" rows="6" spellcheck="false" data-testid="integration-settings"
            [naError]="errors" naPointer="/settings"></textarea>
          <mat-hint>The provider's settings, as docs/providers.md lists them. No secrets here: the secret reference names where they are.</mat-hint>
          @for (e of errors.of('/settings'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-full"><mat-label>Secret reference</mat-label>
          <input matInput name="secretRef" [(ngModel)]="draft.secretRef" autocomplete="off" spellcheck="false" placeholder="env:NOTIFICATIONS_SECRET_…"
            data-testid="integration-secret-ref" [naError]="errors" naPointer="/secretRef">
          <mat-hint>{{ secretHint() }}</mat-hint>
          @for (e of errors.of('/secretRef'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }
        </mat-form-field>
      </div>
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
    const secretRef = this.draft.secretRef.trim();
    const key = this.draft.key.trim();
    const issues = validateAll([
      () => {
        try {
          settings = this.draft.settings.trim() ? JSON.parse(this.draft.settings) : {};
        } catch (e) {
          return { pointer: '/settings', message: `Settings: not JSON (${(e as Error).message}).` };
        }
        // The API takes an object only; a null would read as "left out" and keep the stored settings without a word.
        return typeof settings !== 'object' || settings === null || Array.isArray(settings)
          ? { pointer: '/settings', message: 'Settings: a JSON object, { "name": value, … }.' } : null;
      },
      // A cleared priority reads null, which would be sent as 0: first in the channel's failover order, not what was meant.
      () => isBlankNumber(this.draft.priority) || Number(this.draft.priority) < 0 || !Number.isInteger(Number(this.draft.priority))
        ? { pointer: '/priority', message: 'Priority: a whole number, 0 or more; lower is tried first.' } : null,
      // A secret reference left out keeps the stored one (the API's PUT keeps what isn't sent): emptied, it would stay without a word.
      () => !secretRef && this.existing()?.secretRef
        ? { pointer: '/secretRef', message: 'Secret reference: can be changed but not removed, the stored one stays. Point it at another, or create an integration without one.' } : null,
      () => this.existing() ? null : keyIssue(key, 'integration'),
      () => !this.existing() && key && this.takenKeys().includes(key)
        ? { pointer: '/key', message: 'Key: there is an integration with this key already; open it to change it.' } : null,
      () => this.draft.channel ? null : { pointer: '/channel', message: 'Channel: choose the channel it sends on.' },
      () => this.draft.kind ? null : { pointer: '/kind', message: 'Provider: choose the provider kind.' },
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    submit(this.api.putIntegration(this.tenantId(), key, { channel: this.draft.channel, kind: this.draft.kind, settings: settings as Record<string, unknown>,
      secretRef: secretRef || null, priority: Number(this.draft.priority), enabled: this.draft.enabled }), this.busy, this.errors,
      saved => this.saved.emit(saved));
  }
}
