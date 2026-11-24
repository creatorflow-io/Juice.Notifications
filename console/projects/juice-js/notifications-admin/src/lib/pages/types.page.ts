import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { NotificationsAdminApi } from '../core/api.service';
import { apiResource } from '../shared/resource';
import { TenantLinkPipe, problemText } from '../shared/ui';

/**
 * The types the tenant receives (phase-4c-plan §4, M39): each application's types linked to the tenant, at their latest version, and the
 * channels they have a template on, the application's or the tenant's own. A type opens to its versions, schema, group and templates.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, TenantLinkPipe],
  template: `
    <div class="na-head"><h1>Types</h1><span class="na-spacer"></span>
      <button mat-stroked-button type="button" (click)="types.reload()" data-testid="refresh">Refresh</button></div>
    <p class="na-hint">What the tenant's applications send. A type's templates are the application's; the tenant can put its own over them.</p>
    <div class="na-filters">
      <mat-form-field subscriptSizing="dynamic" class="na-field-md"><mat-label>Find</mat-label>
        <input matInput [ngModel]="find()" (ngModelChange)="find.set($event)" placeholder="key, name or application" data-testid="types-find"></mat-form-field>
    </div>
    @if (types.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (types.error()) { <p class="na-error" data-testid="types-error">{{ error() }}</p> }
    <table class="na-table" data-testid="types">
      <thead><tr><th>Type</th><th>Name</th><th>Application</th><th>Version</th><th>Templates</th></tr></thead>
      <tbody>
        @for (t of shown(); track t.application + '/' + t.key) {
          <tr data-testid="type-row" [attr.data-key]="t.key">
            <td><a class="na-mono" [routerLink]="['types', t.key] | naTenant: tenantId()">{{ t.key }}</a></td>
            <td>{{ t.name }}</td>
            <td>{{ t.application }}</td>
            <td>{{ t.version }}</td>
            <td class="na-small">
              @for (c of templated(t.channels); track c.channel; let last = $last) {
                <span [class.na-warn-text]="c.overridden" [title]="c.overridden ? 'The tenant has its own' : ''">{{ c.channel }}{{ c.overridden ? ' (own)' : '' }}</span>{{ last ? '' : ', ' }}
              } @empty { <span class="na-muted">none</span> }
            </td>
          </tr>
        } @empty { @if (!types.isLoading()) { <tr><td colspan="5" class="na-muted">{{ find() ? 'No type matches.' : 'No types yet: an application linked to the tenant declares them.' }}</td></tr> } }
      </tbody>
    </table>`,
})
export class TypesPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();

  protected readonly types = apiResource({ params: () => this.tenantId(), stream: ({ params }) => this.api.types(params) });
  protected readonly error = computed(() => problemText(this.types.error()));
  protected readonly find = signal('');
  protected readonly shown = computed(() => {
    const words = this.find().trim().toLowerCase();
    const all = this.types.value() ?? [];
    return words ? all.filter(t => [t.key, t.name, t.application].some(v => v.toLowerCase().includes(words))) : all;
  });
  protected readonly templated = (channels: { channel: string; declared: boolean; overridden: boolean }[]) => channels.filter(c => c.declared || c.overridden);
}
