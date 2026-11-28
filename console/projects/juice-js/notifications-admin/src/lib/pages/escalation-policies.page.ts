import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { AccessService, hasPage } from '../core/access.service';
import { NotificationsAdminApi } from '../core/api.service';
import { ESCALATION_RECIPIENT_KINDS, EscalationPolicy, EscalationRecipient, EscalationStep } from '../core/models';
import { FieldIssue, FormErrors, FormErrorsComponent, NaFieldError, isBlankNumber, keyIssue, managedNote, rowAction, submit, validateAll, whenChanged } from '../shared/forms';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';
import { tenantRights } from '../shared/rights';

/** A recipient as the form edits it: every field there, the ones its kind doesn't take left out when saved. */
interface RecipientDraft { kind: EscalationRecipient['kind']; id: string; key: string; resolver: string; }
interface StepDraft { afterMinutes: number | null; recipients: RecipientDraft[]; }

/**
 * The tenant's escalation policies (phase-3-plan M22): "after N minutes, if the correlation is still open, add these recipients", step
 * after step, each waiting after the one before. A type's `escalate` step names a policy; acknowledging stops it unless the policy says not.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatProgressBarModule,
    MatSelectModule, NaFieldError, FormErrorsComponent],
  template: `
    <div class="na-head"><h1>Escalation policies</h1><span class="na-spacer"></span>
      @if (!editing()) { @if (can.configure()) { <button mat-flat-button type="button" (click)="edit(null)" [disabled]="!policies.hasValue()" data-testid="policy-new">New policy</button> } }
      <button mat-stroked-button type="button" (click)="reload()" data-testid="refresh">Refresh</button></div>
    @if (editing(); as e) {
      <form class="na-form na-card" (ngSubmit)="save()" data-testid="policy-form">
        <h2>{{ e.isNew ? 'New policy' : 'Policy ' + draft.key }}</h2>
        <div class="na-form-grid">
          <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Key</mat-label>
            <input matInput name="key" [(ngModel)]="draft.key" [disabled]="!e.isNew" required data-testid="policy-key" [naError]="errors" naPointer="/key">
            @for (m of errors.of('/key'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
          </mat-form-field>
          <mat-checkbox name="stopOnAcknowledge" [(ngModel)]="draft.stopOnAcknowledge" data-testid="policy-stop-on-ack">Acknowledging stops it</mat-checkbox>
        </div>
        @for (step of draft.steps; track $index; let s = $index) {
          <div class="na-card" data-testid="policy-step">
            <div class="na-form-grid">
              <strong>Step {{ s + 1 }}</strong>
              <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>After (minutes)</mat-label>
                <input matInput type="number" min="0" [name]="'after-' + s" [(ngModel)]="step.afterMinutes" data-testid="step-after"
                  [naError]="errors" [naPointer]="'/steps/' + s + '/afterMinutes'">
                <mat-hint>{{ s === 0 ? 'After the notification' : 'After step ' + s }}.</mat-hint>
                @for (m of errors.of('/steps/' + s + '/afterMinutes'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
              </mat-form-field>
              <button mat-icon-button type="button" (click)="draft.steps.splice(s, 1)" aria-label="Remove the step"><mat-icon>delete</mat-icon></button>
            </div>
            @for (r of step.recipients; track $index; let j = $index) {
              <div class="na-form-grid" data-testid="step-recipient">
                <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Recipient</mat-label>
                  <mat-select [name]="'kind-' + s + '-' + j" [(ngModel)]="r.kind" (ngModelChange)="kindChanged(r)" data-testid="recipient-kind"
                    [naError]="errors" [naPointer]="kindPointers(s, j, r)">
                    @for (k of kinds(); track k) { <mat-option [value]="k">{{ k === 'route' ? "the route's targets" : k }}</mat-option> }
                  </mat-select>
                  @for (m of errors.of(kindPointers(s, j, r)); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
                </mat-form-field>
                @if (r.kind === 'topic') {
                  <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Topic key</mat-label>
                    <input matInput [name]="'key-' + s + '-' + j" [(ngModel)]="r.key" data-testid="recipient-key"
                      [naError]="errors" [naPointer]="namePointers(s, j)">
                    @for (m of errors.of(namePointers(s, j)); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
                  </mat-form-field>
                } @else if (r.kind !== 'route') {
                  <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>{{ r.kind }} id</mat-label>
                    <input matInput [name]="'id-' + s + '-' + j" [(ngModel)]="r.id" data-testid="recipient-id"
                      [naError]="errors" [naPointer]="namePointers(s, j)">
                    @for (m of errors.of(namePointers(s, j)); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
                  </mat-form-field>
                }
                @if (r.kind !== 'route') {
                  <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Resolver</mat-label>
                    <mat-select [name]="'resolver-' + s + '-' + j" [(ngModel)]="r.resolver" data-testid="recipient-resolver"
                      [naError]="errors" [naPointer]="'/steps/' + s + '/recipients/' + j + '/resolver'">
                      @for (v of resolversFor(r.kind); track v.key) { <mat-option [value]="v.key">{{ v.key }} ({{ v.kind }})</mat-option> }
                      <!-- As stored, though not usable now: shown, so the select never hides what would be saved. -->
                      @if (r.resolver && !usable(r)) {
                        <mat-option [value]="r.resolver">{{ r.resolver }} <span class="na-muted">(disabled, missing or not for {{ r.kind }})</span></mat-option>
                      }
                    </mat-select>
                    @for (m of errors.of('/steps/' + s + '/recipients/' + j + '/resolver'); track $index) { <mat-error data-testid="field-error" [attr.data-pointer]="m.pointer">{{ m.message }}</mat-error> }
                  </mat-form-field>
                }
                <button mat-icon-button type="button" (click)="step.recipients.splice(j, 1)" aria-label="Remove the recipient"><mat-icon>close</mat-icon></button>
              </div>
            }
            <button mat-button type="button" (click)="step.recipients.push(recipient())" data-testid="step-add-recipient">Add a recipient</button>
          </div>
        }
        <div><button mat-stroked-button type="button" (click)="draft.steps.push(step())" data-testid="policy-add-step">Add a step</button></div>
        <na-form-errors [errors]="errors" [shown]="placed" [reload]="reloadFn" />
        <div class="na-actions">
          <button mat-flat-button type="submit" [disabled]="busy()" data-testid="policy-save">{{ e.isNew ? 'Create' : 'Save' }}</button>
          <button mat-button type="button" (click)="editing.set(null)">Cancel</button>
        </div>
      </form>
    }
    @if (policies.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (policies.error()) { <p class="na-error" data-testid="policies-error">{{ error() }}</p> }
    @if (resolvers.error()) { <p class="na-error" data-testid="resolvers-error">The resolvers couldn't be read, so none can be chosen: {{ resolversError() }}</p> }
    <table class="na-table" data-testid="policies">
      <thead><tr><th>Policy</th><th>Steps</th><th>Acknowledging</th><th>Updated</th><th></th></tr></thead>
      <tbody>
        @for (p of policies.value() ?? []; track p.key) {
          <tr data-testid="policy-row" [attr.data-key]="p.key">
            <td>{{ p.key }}@if (p.managedBy === 'config') { <div class="na-muted na-small">from configuration</div> }</td>
            <td class="na-small">@for (s of p.steps; track $index) { <div>after {{ s.afterMinutes }} min: {{ recipientsText(s) }}</div> }</td>
            <td>{{ p.stopOnAcknowledge ? 'stops it' : "doesn't stop it" }}</td>
            <td class="na-nowrap">{{ p.updatedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">
              @if (p.managedBy === 'api') {
                @if (can.configure()) { <button mat-button type="button" (click)="edit(p)" data-testid="policy-edit">Edit</button> }
                @if (confirming() === p.key) {
                  <button mat-flat-button type="button" (click)="delete(p.key)" [disabled]="busy()" data-testid="policy-delete-confirm">Delete</button>
                  <button mat-button type="button" (click)="confirming.set(null)">Keep</button>
                } @else {
                  @if (can.configure()) { <button mat-button type="button" (click)="confirming.set(p.key)" data-testid="policy-delete">Delete…</button> }
                }
              } @else { <span class="na-muted na-small" [title]="note(p)">read only</span> }
            </td>
          </tr>
        } @empty { @if (!policies.isLoading()) { <tr><td colspan="5" class="na-muted">No escalation policies yet.</td></tr> } }
      </tbody>
    </table>
    @if (rowError()) { <p class="na-error" data-testid="policy-row-error">{{ rowError() }}</p> }`,
})
export class EscalationPoliciesPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  /** The user's rights here: controls they may not use are hidden (the API refuses them anyway). */
  protected readonly can = tenantRights(() => this.tenantId());

  protected readonly policies = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.escalationPolicies(params) });
  private readonly access = toSignal(inject(AccessService).load());
  /**
   * Whether the deployment has people (resolvers): an embedded host has none (phase 5, M44), so its steps go to the route's targets only, and
   * the resolvers, which it doesn't serve, aren't read.
   */
  protected readonly hasPeople = computed(() => { const a = this.access(); return !!a && hasPage(a, 'resolvers'); });
  protected readonly resolvers = apiResource({ params: () => this.hasPeople() ? this.tenantId() : undefined,
    stream: ({ params }) => this.api.resolvers(params) });
  protected readonly error = computed(() => problemText(this.policies.error()));
  protected readonly resolversError = computed(() => problemText(this.resolvers.error()));
  protected readonly kinds = computed(() => this.hasPeople() ? ESCALATION_RECIPIENT_KINDS : ESCALATION_RECIPIENT_KINDS.filter(k => k === 'route'));
  protected readonly errors = new FormErrors();
  protected readonly editing = signal<{ isNew: boolean } | null>(null);
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly rowError = signal('');
  protected draft: { key: string; steps: StepDraft[]; stopOnAcknowledge: boolean } = this.blank();
  protected readonly note = (p: EscalationPolicy) => managedNote(p.managedBy);
  protected readonly reloadFn = () => this.reload();

  constructor() {
    // Reused for another tenant: what was open for the last one is closed.
    whenChanged(() => this.tenantId(), () => this.close());
    // Recipients added before the resolvers were read have none: each takes its kind's first once they are.
    whenChanged(() => this.resolvers.value(), () => {
      for (const s of this.draft.steps) for (const r of s.recipients) if (r.kind !== 'route' && !r.resolver) this.kindChanged(r);
    });
  }

  private blank() {
    return { key: '', steps: [this.step()], stopOnAcknowledge: true };
  }

  private close(): void {
    this.editing.set(null);
    this.confirming.set(null);
    this.errors.clear();
    this.rowError.set('');
    this.draft = this.blank();
  }

  protected step(): StepDraft {
    return { afterMinutes: 15, recipients: [this.recipient()] };
  }

  protected recipient(): RecipientDraft {
    return this.hasPeople() ? { kind: 'user', id: '', key: '', resolver: this.resolversFor('user')[0]?.key ?? '' }
      : { kind: 'route', id: '', key: '', resolver: '' };
  }

  /** The tenant's enabled resolvers that handle a kind of person. */
  protected resolversFor(kind: string) {
    return (this.resolvers.value() ?? []).filter(r => r.enabled && r.handles.includes(kind));
  }

  /** Whether the recipient's resolver is one its kind can use now. */
  protected usable(r: RecipientDraft): boolean {
    return this.resolversFor(r.kind).some(v => v.key === r.resolver);
  }

  /** A resolver the new kind can't use is replaced by its first one (or none). */
  protected kindChanged(r: RecipientDraft): void {
    if (!this.usable(r)) r.resolver = this.resolversFor(r.kind)[0]?.key ?? '';
  }

  /** What a recipient lacks for its kind: the topic key or the person's id, and the resolver that reaches them. */
  private recipientIssues(r: RecipientDraft, step: number, recipient: number): FieldIssue[] {
    if (r.kind === 'route') return [];
    const at = `/steps/${step}/recipients/${recipient}`;
    const who = `Step ${step + 1}, recipient ${recipient + 1}`;
    const issues: FieldIssue[] = [];
    if (r.kind === 'topic' ? !r.key.trim() : !r.id.trim()) issues.push({ pointer: at, message: `${who}: give the ${r.kind === 'topic' ? 'topic key' : r.kind + ' id'}.` });
    if (!r.resolver) issues.push({ pointer: `${at}/resolver`, message: `${who}: choose the resolver that reaches them.` });
    return issues;
  }

  /** Where a recipient's kind shows its errors: its own, and (a route has no id field) those for the recipient as a whole. */
  protected kindPointers(step: number, recipient: number, r: RecipientDraft): string {
    const at = `/steps/${step}/recipients/${recipient}`;
    return r.kind === 'route' ? `${at}$|${at}/kind` : `${at}/kind`;
  }

  /** Where a recipient's topic key or id shows its errors. */
  protected namePointers(step: number, recipient: number): string {
    const at = `/steps/${step}/recipients/${recipient}`;
    return `${at}$|${at}/id|${at}/key`;
  }

  /** Whether a field of the form shows this pointer's error; the rest are listed above the buttons. */
  protected readonly placed = (pointer: string): boolean => {
    if (pointer === '/key' || pointer.startsWith('/key/')) return true;
    const step = /^\/steps\/(\d+)\/(afterMinutes|recipients\/(\d+)(\/(kind|id|key|resolver))?)$/.exec(pointer);
    if (!step) return false;
    const s = this.draft.steps[Number(step[1])];
    if (!s) return false;
    if (step[3] === undefined) return true;
    const r = s.recipients[Number(step[3])];
    // A route recipient has no id, key or resolver field.
    return !!r && (r.kind !== 'route' || step[5] === undefined || step[5] === 'kind');
  };

  protected recipientsText(step: EscalationStep): string {
    return step.recipients.map(r => r.kind === 'route' ? "the route's targets"
      : r.kind === 'topic' ? `topic ${r.key} (${r.resolver})` : `${r.kind} ${r.id} (${r.resolver})`).join(', ');
  }

  protected reload(): void {
    this.close();
    this.policies.reload();
    this.resolvers.reload();
  }

  protected edit(p: EscalationPolicy | null): void {
    this.draft = p ? {
      key: p.key, stopOnAcknowledge: p.stopOnAcknowledge,
      steps: p.steps.map(s => ({ afterMinutes: s.afterMinutes, recipients: s.recipients.map(r => ({
        kind: r.kind, id: 'id' in r ? r.id : '', key: 'key' in r ? r.key : '', resolver: 'resolver' in r ? r.resolver : '' })) })),
    } : this.blank();
    this.errors.clear();
    this.rowError.set('');
    this.editing.set({ isNew: !p });
  }

  protected save(): void {
    const key = this.draft.key.trim();
    const isNew = !!this.editing()?.isNew;
    const issues = validateAll([
      // Sent empty, the PUT would go to the policies collection, which takes none.
      () => isNew ? keyIssue(key, 'policy') : null,
      // A PUT creates or replaces: a new policy is checked against the list, which must have been read.
      () => isNew && key && !this.policies.hasValue()
        ? { pointer: '/key', message: "Key: the policies couldn't be read, so this key can't be checked as new; refresh first." } : null,
      () => isNew && key && this.policies.value()?.some(p => p.key === key)
        ? { pointer: '/key', message: 'Key: there is a policy with this key already; edit it instead.' } : null,
      () => this.draft.steps.length ? null : { pointer: '/steps', message: 'Steps: add at least one step.' },
      // A cleared field reads null, which Number() would send as 0: escalating at once, not what was meant.
      () => this.draft.steps.flatMap((s, i) => (isBlankNumber(s.afterMinutes) || Number(s.afterMinutes) < 0 || !Number.isInteger(Number(s.afterMinutes))
        ? [{ pointer: `/steps/${i}/afterMinutes`, message: `Step ${i + 1}, After: a whole number of minutes, 0 or more (0 for at once).` }] : [])),
      () => this.draft.steps.flatMap((s, i) => s.recipients.length ? s.recipients.flatMap((r, j) => this.recipientIssues(r, i, j))
        : [{ pointer: `/steps/${i}/recipients`, message: `Step ${i + 1}: add at least one recipient.` }]),
    ]);
    if (issues.length) {
      this.errors.localAll(issues);
      return;
    }
    // Each recipient with only the fields its kind takes: the API refuses any other.
    const steps: EscalationStep[] = this.draft.steps.map(s => ({ afterMinutes: Number(s.afterMinutes), recipients: s.recipients.map(r =>
      r.kind === 'route' ? { kind: 'route' as const }
        : r.kind === 'topic' ? { kind: 'topic' as const, key: r.key.trim(), resolver: r.resolver }
          : { kind: r.kind, id: r.id.trim(), resolver: r.resolver }) }));
    submit(this.api.putEscalationPolicy(this.tenantId(), key, { steps, stopOnAcknowledge: this.draft.stopOnAcknowledge }), this.busy, this.errors,
      () => this.reload());
  }

  protected delete(key: string): void {
    rowAction(this.api.deleteEscalationPolicy(this.tenantId(), key),
      { busy: this.busy, error: this.rowError, disarm: () => this.confirming.set(null), done: () => this.reload() });
  }
}
