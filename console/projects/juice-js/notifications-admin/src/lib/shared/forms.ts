import { ChangeDetectionStrategy, Component, computed, effect, input, signal, untracked, WritableSignal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ManagedBy, Match, PRIORITIES, Priority, Problem } from '../core/models';
import { Observable, Subscription } from 'rxjs';
import { problemText } from './ui';

/**
 * A form's answer from the API (phase-4c-plan I54): the field errors it named, by JSON pointer, shown on their fields; anything else (a
 * conflict, a refusal) as one sentence. A change raced by another (`concurrent_change`) says to reload.
 */
export class FormErrors {
  private readonly problem = signal<{ body: Problem | null; text: string; local?: boolean } | null>(null);

  /** The API's errors, by pointer; empty when it gave none. */
  readonly fields = computed(() => this.problem()?.body?.errors ?? []);
  /** Whether the save lost a race: the row changed (or appeared) since the form read it. */
  readonly conflict = computed(() => this.problem()?.body?.code === 'concurrent_change');
  /** The sentence for the whole form: the API's own, or the reload advice for a conflict; none for the form's own refusal (nothing was sent). */
  readonly summary = computed(() => {
    const p = this.problem();
    if (!p || p.local) return '';
    if (this.conflict()) return 'Someone changed this meanwhile. Reload to see what is there now, then make your change again.';
    return this.fields().length ? 'The API refused some fields: see below.' : p.text;
  });

  set(error: unknown): void {
    const e = error as { error?: Problem | string | null };
    this.problem.set({ body: typeof e?.error === 'object' ? e.error : null, text: problemText(error) });
  }

  /** A pointer's own error, as from the form (a JSON text that doesn't parse, before anything is sent). */
  local(pointer: string, message: string): void {
    this.problem.set({ body: { errors: [{ pointer, message }] }, text: message, local: true });
  }

  clear(): void {
    this.problem.set(null);
  }

  /** The messages for `pointer` and the fields under it (`/match` also takes `/match/labels/env`). */
  at(pointer: string): string[] {
    return this.fields().filter(e => e.pointer === pointer || e.pointer.startsWith(pointer + '/')).map(e => e.message);
  }

  /** The messages for exactly `pointer`. */
  exactly(pointer: string): string[] {
    return this.fields().filter(e => e.pointer === pointer).map(e => e.message);
  }

  /** Field errors no field of the form shows (`shown` are the pointers it has), so none is lost. */
  others(shown: string[]): { pointer: string; message: string }[] {
    return this.fields().filter(e => !shown.some(p => e.pointer === p || e.pointer.startsWith(p + '/')));
  }
}

/**
 * Sends a form's save: busy while it runs, the API's refusal on the form's errors (then `failed`), `done` with what it answered. The
 * subscription lets a page that moves on (another type) drop the answer.
 */
export function submit<T>(call: Observable<T>, busy: WritableSignal<boolean>, errors: FormErrors, done: (value: T) => void, failed?: () => void): Subscription {
  busy.set(true);
  errors.clear();
  return call.subscribe({
    next: value => { busy.set(false); done(value); },
    error: e => { busy.set(false); errors.set(e); failed?.(); },
  });
}

/**
 * A row's action (a delete, a cancel): busy while it runs, its refusal as the row's sentence, the confirmation disarmed either way, then
 * `done` (on success) or `failed`.
 */
export function rowAction(call: Observable<unknown>, o: { busy: WritableSignal<boolean>; error: WritableSignal<string>; disarm: () => void;
  done: () => void; failed?: () => void }): Subscription {
  o.busy.set(true);
  o.error.set('');
  return call.subscribe({
    next: () => { o.busy.set(false); o.disarm(); o.done(); },
    error: e => { o.busy.set(false); o.disarm(); o.error.set(problemText(e)); o.failed?.(); },
  });
}

/**
 * Saves still running for what a page shows now: `cancel` drops their answers when it moves on (another tenant, type, subscriber or
 * group), so one sent for the last never lands on the next.
 */
export class Pending {
  private running: Subscription[] = [];

  track(s: Subscription): void {
    this.running = [...this.running.filter(p => !p.closed), s];
  }

  cancel(): void {
    this.running.forEach(s => s.unsubscribe());
    this.running = [];
  }
}

/**
 * Runs `act` whenever `source` changes (and once at the start), outside of tracking. For a page Angular reuses across tenants (or keys):
 * a form opened for one must not save into the next. Call it in a constructor or a field initializer.
 */
export function whenChanged(source: () => unknown, act: () => void): void {
  effect(() => {
    source();
    untracked(act);
  });
}

/** Whether a number field holds a number: a cleared one reads null (or ''), which Number() would send as 0. */
export function isBlankNumber(value: unknown): boolean {
  return value === null || value === undefined || String(value).trim() === '' || !Number.isFinite(Number(value));
}

/** A JSON pointer's segment for a name (`a/b` → `a~1b`), as the API escapes it. */
export function pointerSegment(name: string): string {
  return name.replace(/~/g, '~0').replace(/\//g, '~1');
}

/** The errors under a field, each on its own line. */
@Component({
  selector: 'na-field-errors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@for (m of messages(); track $index) { <div class="na-error na-small" data-testid="field-error" [attr.data-pointer]="pointer()">{{ m }}</div> }`,
})
export class FieldErrorsComponent {
  readonly messages = input.required<string[]>();
  readonly pointer = input<string>('');
}

/** A form's sentence, and the field errors it has no field for (with their pointers). */
@Component({
  selector: 'na-form-errors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    @if (errors().summary()) {
      <div class="na-error" data-testid="form-error">{{ errors().summary() }}
        @if (errors().conflict() && reload()) { <button mat-button type="button" (click)="reload()!()" data-testid="form-reload">Reload</button> }
      </div>
    }
    @for (e of errors().others(shown()); track $index) {
      <div class="na-error na-small" data-testid="field-error" [attr.data-pointer]="e.pointer"><span class="na-mono">{{ e.pointer }}</span>: {{ e.message }}</div>
    }`,
})
export class FormErrorsComponent {
  readonly errors = input.required<FormErrors>();
  readonly shown = input<string[]>([]);
  readonly reload = input<(() => void) | null>(null);
}

/** Who owns a row, when it isn't the API: such a row is shown, not edited. */
export function managedNote(managedBy: ManagedBy | undefined): string {
  return managedBy === 'config' ? "From the deployment's configuration: change it there."
    : managedBy === 'service' ? "The service's own: it can't be changed or deleted." : '';
}

/**
 * A key as the API takes it: 1–63 lowercase letters, digits or hyphens, starting with a letter. The hyphen is escaped: browsers compile an
 * input's `pattern` with the `v` flag, where a bare `-` in a class is a syntax error and the pattern is dropped.
 */
export const KEY_PATTERN = '[a-z][a-z0-9\\-]{0,62}';

/** A match as a form edits it: types one per line or comma, a row per label matcher, a minimum priority or none. */
export interface MatchDraft { types: string; labels: { name: string; matcher: string }[]; minPriority: Priority | ''; }

export function matchDraft(match: Match | null | undefined): MatchDraft {
  const types = match?.type === undefined ? [] : Array.isArray(match.type) ? match.type : [match.type];
  return {
    types: types.join(', '),
    labels: Object.entries(match?.labels ?? {}).map(([name, matcher]) => ({ name, matcher })),
    minPriority: match?.minPriority ?? '',
  };
}

/** The draft as the API takes it: one type as a string, several as a list, empty parts left out (`{}` matches everything). */
export function matchOf(draft: MatchDraft): Match {
  const match: Match = {};
  const types = draft.types.split(/[\s,]+/).map(t => t.trim()).filter(Boolean);
  if (types.length === 1) match.type = types[0];
  else if (types.length > 1) match.type = types;
  const labels = draft.labels.filter(l => l.name.trim() || l.matcher.trim());
  if (labels.length) match.labels = Object.fromEntries(labels.map(l => [l.name.trim(), l.matcher.trim()]));
  if (draft.minPriority) match.minPriority = draft.minPriority;
  return match;
}

/**
 * What {@link matchOf} can't send as typed: a label matcher without a name, or one name twice (an object keeps one of them, so the other
 * condition would vanish without a word). Null when the draft is fine.
 */
export function matchProblem(draft: MatchDraft): { pointer: string; message: string } | null {
  const seen = new Set<string>();
  for (const label of draft.labels) {
    const name = label.name.trim();
    if (!name && !label.matcher.trim()) continue;
    if (!name) return { pointer: '/match/labels', message: 'Each label matcher names its label.' };
    if (seen.has(name)) {
      return { pointer: '/match/labels/' + pointerSegment(name), message: `The label ${name} is matched twice: one matcher per label (in(a,b) takes several values).` };
    }
    seen.add(name);
  }
  return null;
}

/** A match in one line, for a list: `sample.alert.* · env=prod · severity in(high,critical) · high and above`. */
export function matchText(match: Match | null | undefined): string {
  const parts: string[] = [];
  if (match?.type !== undefined) parts.push(Array.isArray(match.type) ? match.type.join(', ') : match.type);
  for (const [name, matcher] of Object.entries(match?.labels ?? {})) {
    parts.push(/^(=|!=|=~|in\()/.test(matcher) ? `${name} ${matcher}` : `${name}=${matcher}`);
  }
  if (match?.minPriority) parts.push(`${match.minPriority} and above`);
  return parts.length ? parts.join(' · ') : 'every notification';
}

/**
 * A route's or a mute's match (design §6.3): type patterns (`*` for any run of characters), label matchers (`value`, `=value`, `!=value`,
 * `=~regex`, `in(a,b)`) and a minimum priority. The API's errors under `/match` show on their parts.
 */
@Component({
  selector: 'na-match-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, FieldErrorsComponent],
  template: `
    <div class="na-filters">
      <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Types</mat-label>
        <input matInput [name]="name() + '-types'" [(ngModel)]="draft().types" [disabled]="disabled()" placeholder="sample.alert.*, sample.job.failed"
          data-testid="match-types">
        <mat-hint>Patterns, comma-separated; * for any run of characters. Empty: any type.</mat-hint></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Minimum priority</mat-label>
        <mat-select [name]="name() + '-priority'" [(ngModel)]="draft().minPriority" [disabled]="disabled()" data-testid="match-priority">
          <mat-option value="">Any</mat-option>
          @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }
        </mat-select></mat-form-field>
    </div>
    <na-field-errors [messages]="errors().at('/match/type')" pointer="/match/type" />
    <na-field-errors [messages]="errors().at('/match/minPriority')" pointer="/match/minPriority" />
    <na-field-errors [messages]="errors().exactly('/match')" pointer="/match" />
    <na-field-errors [messages]="errors().exactly('/match/labels')" pointer="/match/labels" />
    @for (label of draft().labels; track $index; let i = $index) {
      <div class="na-filters" data-testid="match-label">
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Label</mat-label>
          <input matInput [name]="name() + '-label-' + i" [(ngModel)]="label.name" [disabled]="disabled()" data-testid="match-label-name"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Matches</mat-label>
          <input matInput [name]="name() + '-matcher-' + i" [(ngModel)]="label.matcher" [disabled]="disabled()" placeholder="prod, !=dev, in(high,critical)"
            data-testid="match-label-matcher"></mat-form-field>
        @if (!disabled()) {
          <button mat-icon-button type="button" (click)="draft().labels.splice(i, 1)" aria-label="Remove the label matcher"><mat-icon>close</mat-icon></button>
        }
      </div>
      <na-field-errors [messages]="errors().at('/match/labels/' + segment(label.name.trim()))" />
    }
    @if (!disabled()) {
      <button mat-button type="button" (click)="draft().labels.push({ name: '', matcher: '' })" data-testid="match-add-label">Add a label matcher</button>
    }`,
})
export class MatchEditorComponent {
  readonly draft = input.required<MatchDraft>();
  readonly errors = input.required<FormErrors>();
  readonly disabled = input(false);
  /** Names the inputs apart when a page has two editors. */
  readonly name = input('match');
  protected readonly priorities = PRIORITIES;
  protected readonly segment = pointerSegment;
}

/**
 * What each provider's `settings` hold (docs/providers.md), as a starting point for a new integration. Secrets never go here: the
 * integration's secret reference names where they are.
 */
export const SETTINGS_EXAMPLES: Record<string, Record<string, unknown>> = {
  smtp: { server: 'local', from: 'notifications@example.com', fromName: 'Notifications' },
  ses: { region: 'eu-west-1', from: 'notifications@example.com', fromName: 'Notifications', configurationSet: '', topicArn: '' },
  sendgrid: { from: 'notifications@example.com', fromName: 'Notifications', region: 'global', eventWebhookKey: '' },
  twilio: { from: '+15005550006' },
  fcm: { projectId: '' },
  apns: { teamId: '', keyId: '', bundleId: '', environment: 'production' },
  webpush: { subject: 'mailto:ops@example.com', publicKey: '' },
};

/** What a kind's secret reference names, and what its targets' addresses are (docs/providers.md). */
export const KIND_HINTS: Record<string, { secret?: string; target?: string }> = {
  smtp: { secret: "None needed: settings.server names one of the deployment's SMTP servers, which carries its own sign-in.", target: 'An email address.' },
  ses: { secret: 'An AWS access key: {accessKeyId}:{secretAccessKey}.', target: 'An email address.' },
  sendgrid: { secret: 'The SendGrid API key.', target: 'An email address.' },
  webhook: { secret: 'The signing secret: every request is signed with it.', target: 'An https URL (http only to an allowlisted private host).' },
  slack: { secret: 'A value {botToken}:{signingSecret}.', target: "A channel's id (C… or G…); invite the app to it." },
  'slack-webhook': { target: "The incoming webhook's URL, as an address reference (it is a secret)." },
  'teams-workflow': { target: "The Teams workflow's URL, as an address reference (it is a secret)." },
  telegram: { secret: "The bot's token, from BotFather.", target: "A chat's id (-100… for a group or channel) or a public channel's @name." },
  twilio: { secret: 'A value {accountSid}:{authToken}.', target: 'A phone number (E.164).' },
  fcm: { secret: "The service account's JSON key." },
  apns: { secret: 'The .p8 signing key (PEM).' },
  webpush: { secret: 'The VAPID private key (base64url).' },
};

/** Where a secret reference may point (I53): a name, never the secret itself. */
export const SECRET_REF_HINT = 'A reference, never the secret: env:NAME, file:/path, vault:path#field or keyvault:name, within what the deployment allows.';
