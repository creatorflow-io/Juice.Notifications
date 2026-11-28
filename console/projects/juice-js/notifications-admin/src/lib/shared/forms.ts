import { ChangeDetectionStrategy, Component, computed, Directive, effect, ElementRef, forwardRef, inject, input, Signal, signal, untracked, WritableSignal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ManagedBy, Match, PRIORITIES, Priority, Problem } from '../core/models';
import { Observable, Subscription } from 'rxjs';
import { problemText } from './ui';

/** One refused field: its JSON pointer, and a message that names the field and the rule. */
export interface FieldIssue { pointer: string; message: string }
/** One rule of a form: nothing when it holds, else what is wrong (one issue, or one per list entry). */
export type Check = () => FieldIssue | FieldIssue[] | null;

/**
 * Runs every check, never stopping at the first, so a form shows all its problems in one submit. A list's entries are named in the
 * message ("Port 99999 (entry 3): ...").
 */
export function validateAll(checks: Check[]): FieldIssue[] {
  const issues: FieldIssue[] = [];
  for (const check of checks) {
    const found = check();
    if (found) issues.push(...(Array.isArray(found) ? found : [found]));
  }
  return issues;
}

/** Whether a pointer is under `spec`: `/a` takes `/a` and `/a/b`; `/a$` takes `/a` only. */
function under(pointer: string, spec: string): boolean {
  return spec.endsWith('$') ? pointer === spec.slice(0, -1) : pointer === spec || pointer.startsWith(spec + '/');
}

/**
 * A form's answer from the API (phase-4c-plan I54) or from the form itself: the field errors, by JSON pointer, shown inside their fields;
 * anything else (a conflict, a refusal) as one sentence. A change raced by another (`concurrent_change`) says to reload.
 */
export class FormErrors {
  private readonly problem = signal<{ body: Problem | null; text: string; local?: boolean } | null>(null);

  /** The errors, by pointer; empty when there are none. */
  readonly fields = computed(() => this.problem()?.body?.errors ?? []);
  /** How many field errors the form shows (its marked fields, and the ones listed for lack of a field). */
  readonly count: Signal<number> = computed(() => this.fields().length);
  /** Whether the save lost a race: the row changed (or appeared) since the form read it. */
  readonly conflict = computed(() => this.problem()?.body?.code === 'concurrent_change');
  /**
   * The sentence for the whole form: how many fields to fix, the API's own sentence, or the reload advice for a conflict. It never points
   * below: the errors are in the fields (and the list above it), not under it.
   */
  readonly summary = computed(() => {
    const p = this.problem();
    if (!p) return '';
    if (this.conflict()) return 'Someone changed this meanwhile. Reload to see what is there now, then make your change again.';
    const n = this.count();
    return n ? `Fix the ${n} marked field${n === 1 ? '' : 's'}.` : p.local ? '' : p.text;
  });
  /** Bumped when errors arrive: the fields that show one offer themselves for the focus. */
  readonly focusTick = signal(0);
  private candidate: HTMLElement | null = null;
  private focusTimer: ReturnType<typeof setTimeout> | undefined;

  set(error: unknown): void {
    const e = error as { error?: Problem | string | null };
    this.problem.set({ body: typeof e?.error === 'object' ? e.error : null, text: problemText(error) });
    this.focusTick.update(n => n + 1);
  }

  /** Marks every issue at once (the form's own refusal: nothing was sent). No issues clears the errors. */
  localAll(issues: FieldIssue[]): void {
    if (!issues.length) {
      this.clear();
      return;
    }
    this.problem.set({ body: { errors: issues.map(i => ({ pointer: i.pointer, message: i.message })) }, text: issues[0].message, local: true });
    this.focusTick.update(n => n + 1);
  }

  /** A pointer's own error, as from the form (a JSON text that doesn't parse, before anything is sent). */
  local(pointer: string, message: string): void {
    this.localAll([{ pointer, message }]);
  }

  clear(): void {
    this.problem.set(null);
  }

  /** What a field's edit does to its errors: those under the pointers go (`/a$` is `/a` alone); the rest stay until the next submit. */
  drop(pointers: string[]): void {
    const p = this.problem();
    const errors = p?.body?.errors;
    if (!p || !errors?.length) return;
    const rest = errors.filter(e => !pointers.some(s => under(e.pointer, s)));
    if (rest.length === errors.length) return;
    this.problem.set(rest.length ? { ...p, body: { ...p.body, errors: rest } } : null);
  }

  /** The errors for any of `pointers` (`a|b`; `/a$` is `/a` alone), with their pointers. */
  of(pointers: string): { pointer: string; message: string }[] {
    const specs = pointers.split('|');
    return this.fields().filter(e => specs.some(s => under(e.pointer, s)));
  }

  /** A field with an error offers itself: once all have, the first in the document takes the focus. */
  offerFocus(el: HTMLElement): void {
    if (!this.candidate || this.candidate.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_PRECEDING) this.candidate = el;
    this.focusTimer ??= setTimeout(() => {
      const c = this.candidate;
      this.candidate = null;
      this.focusTimer = undefined;
      if (c?.isConnected) c.focus();
    });
  }

  /** The messages for `pointer` and the fields under it (`/match` also takes `/match/labels/env`). */
  at(pointer: string): string[] {
    return this.fields().filter(e => e.pointer === pointer || e.pointer.startsWith(pointer + '/')).map(e => e.message);
  }

  /** The messages for exactly `pointer`. */
  exactly(pointer: string): string[] {
    return this.fields().filter(e => e.pointer === pointer).map(e => e.message);
  }

  /**
   * Field errors no field of the form shows, so none is lost: `shown` are the pointers it has (an entry takes what is under it, `/a$` only
   * `/a`), or a test of a pointer for a form whose fields come and go.
   */
  others(shown: string[] | ((pointer: string) => boolean)): { pointer: string; message: string }[] {
    return this.fields().filter(e => !(typeof shown === 'function' ? shown(e.pointer) : shown.some(p => under(e.pointer, p))));
  }
}

/**
 * Sends a form's save: busy while it runs, the API's refusal on the form's errors (then `failed`), `done` with what it answered. The
 * subscription lets a page that moves on (another type) drop the answer.
 */
export function submit<T>(call: Observable<T>, busy: WritableSignal<boolean>, errors: FormErrors, done: (value: T) => void, failed?: () => void): Subscription {
  // A second Save (a double click, Enter) while one runs sends nothing: the first answers for both.
  if (busy()) return Subscription.EMPTY;
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

/**
 * Marks a form control (`matInput`, `mat-select`, a chip grid) as refused while its form has errors for its pointer (`/a|/b` for several,
 * `/a$` for `/a` alone). The errors themselves are `<mat-error>` children of the control's `mat-form-field`, one per message:
 *
 *   `@for (e of errors.of('/key'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error> }`
 *
 * An edit of the control drops its errors, and the first marked control in the document takes the focus when errors arrive.
 */
@Directive({
  selector: '[naError]',
  providers: [{ provide: ErrorStateMatcher, useExisting: forwardRef(() => NaFieldError) }],
  // Material leaves `aria-invalid` off an empty required input (a pristine field isn't invalid); a refused one is, empty or not.
  // Not `change`: it fires on blur, when the focus leaves for the first marked field after a submit, and would take the errors it just showed.
  host: { '(input)': 'edited()', '(selectionChange)': 'edited()', '[attr.aria-invalid]': 'invalid() ? "true" : null' },
})
export class NaFieldError extends ErrorStateMatcher {
  readonly naError = input.required<FormErrors>();
  readonly naPointer = input.required<string>();
  protected readonly invalid = computed(() => this.naError().of(this.naPointer()).length > 0);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  constructor() {
    super();
    let seen: number | null = null;
    effect(() => {
      const errors = this.naError();
      const tick = errors.focusTick();
      untracked(() => {
        // The tick this control was made under is not news.
        if (seen === null) seen = tick;
        else if (tick !== seen) {
          seen = tick;
          if (errors.of(this.naPointer()).length) errors.offerFocus(this.element);
        }
      });
    });
  }

  override isErrorState(): boolean {
    return this.naError().of(this.naPointer()).length > 0;
  }

  protected edited(): void {
    this.naError().drop(this.naPointer().split('|'));
  }
}

/** A form's sentence (how many fields to fix, or what the API said), and the field errors it has no field for (with their pointers). */
@Component({
  selector: 'na-form-errors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    @if (errors().summary()) {
      <div class="na-error na-small" role="alert" data-testid="form-error">{{ errors().summary() }}
        @if (errors().conflict() && reload()) { <button mat-button type="button" (click)="reload()!()" data-testid="form-reload">Reload</button> }
      </div>
    }
    @for (e of errors().others(shown()); track $index) {
      <div class="na-error na-small" data-testid="field-error" [attr.data-pointer]="e.pointer"><span class="na-mono">{{ e.pointer }}</span>: {{ e.message }}</div>
    }`,
})
export class FormErrorsComponent {
  readonly errors = input.required<FormErrors>();
  readonly shown = input<string[] | ((pointer: string) => boolean)>([]);
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
  const types = typesOf(draft);
  if (types.length === 1) match.type = types[0];
  else if (types.length > 1) match.type = types;
  const labels = draft.labels.filter(l => l.name.trim() || l.matcher.trim());
  if (labels.length) match.labels = Object.fromEntries(labels.map(l => [l.name.trim(), l.matcher.trim()]));
  if (draft.minPriority) match.minPriority = draft.minPriority;
  return match;
}

/**
 * What {@link matchOf} can't send as typed: a label matcher without a name, or one name twice (an object keeps one of them, so the other
 * condition would vanish without a word). Every such row, in one list; empty when the draft is fine.
 */
export function matchIssues(draft: MatchDraft): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const seen = new Set<string>();
  draft.labels.forEach((label, i) => {
    const name = label.name.trim();
    if (!name && !label.matcher.trim()) return;
    if (!name) {
      issues.push({ pointer: '/match/labels', message: `Label matcher ${i + 1} needs a label name: each matcher names its label.` });
    } else if (seen.has(name)) {
      issues.push({ pointer: '/match/labels/' + pointerSegment(name),
        message: `The label ${name} is matched twice (matcher ${i + 1}): one matcher per label (in(a,b) takes several values).` });
    }
    seen.add(name);
  });
  return issues;
}

/** The draft's type patterns, as {@link matchOf} sends them: whitespace and commas separate them, empty parts are nothing. */
function typesOf(draft: MatchDraft): string[] {
  return draft.types.split(/[\s,]+/).map(t => t.trim()).filter(Boolean);
}

/** Whether the draft names nothing to match: no type, no label (one with a name) and no minimum priority, so it takes every notification. */
export const everyNotification = (draft: MatchDraft): boolean =>
  !typesOf(draft).length && !draft.labels.some(l => l.name.trim()) && !draft.minPriority;

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
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, NaFieldError],
  template: `
    <div class="na-form-grid">
      <mat-form-field subscriptSizing="dynamic" class="na-field-lg"><mat-label>Types</mat-label>
        <input matInput [name]="name() + '-types'" [(ngModel)]="draft().types" [disabled]="disabled()" placeholder="sample.alert.*, sample.job.failed"
          data-testid="match-types" [naError]="errors()" naPointer="/match/type|/match$">
        <mat-hint>Patterns, comma-separated; * for any run of characters. Empty: any type.</mat-hint>
        @for (e of errors().of('/match/type|/match$'); track $index) {
          <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error>
        }</mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Minimum priority</mat-label>
        <mat-select [name]="name() + '-priority'" [(ngModel)]="draft().minPriority" [disabled]="disabled()" data-testid="match-priority"
          [naError]="errors()" naPointer="/match/minPriority">
          <mat-option value="">Any</mat-option>
          @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }
        </mat-select>
        @for (e of errors().of('/match/minPriority'); track $index) {
          <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error>
        }</mat-form-field>
    </div>
    @for (label of draft().labels; track $index; let i = $index) {
      <div class="na-form-grid" data-testid="match-label">
        <mat-form-field subscriptSizing="dynamic" class="na-field-sm"><mat-label>Label</mat-label>
          <input matInput [name]="name() + '-label-' + i" [(ngModel)]="label.name" [disabled]="disabled()" data-testid="match-label-name"
            [naError]="errors()" [naPointer]="labelPointer(label)">
          @for (e of errors().of(labelPointer(label)); track $index) {
            @if (e.pointer !== '/match/labels' || e.message.startsWith('Label matcher ' + (i + 1) + ' ')) {
              <mat-error data-testid="field-error" [attr.data-pointer]="e.pointer">{{ e.message }}</mat-error>
            }
          }</mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Matches</mat-label>
          <input matInput [name]="name() + '-matcher-' + i" [(ngModel)]="label.matcher" [disabled]="disabled()" placeholder="prod, !=dev, in(high,critical)"
            data-testid="match-label-matcher"></mat-form-field>
        @if (!disabled()) {
          <button mat-icon-button type="button" (click)="draft().labels.splice(i, 1)" aria-label="Remove the label matcher"><mat-icon>close</mat-icon></button>
        }
      </div>
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

  /** Where a label row's errors are: those under its name, or (a matcher without a name) the list's own. */
  protected labelPointer(label: { name: string; matcher: string }): string {
    const name = label.name.trim();
    return name ? '/match/labels/' + pointerSegment(name) : label.matcher.trim() ? '/match/labels$' : '/match/labels/';
  }
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

/** A key's check (1–63 lowercase letters, digits or hyphens, starting with a letter), named for its form: the issue, or null. */
export function keyIssue(value: string, what: string, pointer = '/key'): FieldIssue | null {
  if (!value) return { pointer, message: `Key: give the ${what} a key, 1–63 lowercase letters, digits or hyphens, starting with a letter.` };
  if (!new RegExp(`^(?:${KEY_PATTERN})$`).test(value)) {
    return { pointer, message: 'Key: use 1–63 lowercase letters, digits or hyphens, starting with a letter.' };
  }
  return null;
}
