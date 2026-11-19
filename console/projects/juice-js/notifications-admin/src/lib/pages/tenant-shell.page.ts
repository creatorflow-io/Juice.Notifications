import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterOutlet } from '@angular/router';
import { AccessService, AccessTenant, sameId } from '../core/access.service';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';

/**
 * A tenant's pages (`/tenants/{id}/…`), under a bar naming the tenant. Someone who administers more than one picks another there and
 * stays on the same page in it (phase-4c-plan p4c-d2); a tenant admin has one and sees only its name.
 */
@Component({
  selector: 'na-tenant-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, RouterOutlet],
  styles: `
    .na-tenant-bar { display: flex; align-items: center; gap: 12px; padding: 8px 16px; border-bottom: 1px solid var(--mat-sys-outline-variant, #ddd); }
    .na-tenant-bar .label { color: var(--mat-sys-on-surface-variant, #555); font-size: 13px; }
    .na-tenant-bar .name { font-weight: 500; }
    .na-tenant-bar mat-form-field { width: 440px; }
    .na-tenant-page { padding: 16px; }
  `,
  template: `
    <div class="na-tenant-bar" data-testid="tenant-bar">
      <span class="label">Tenant</span>
      @if (tenants().length > 1) {
        <mat-form-field subscriptSizing="dynamic">
          <mat-select [ngModel]="current()?.id" (ngModelChange)="switch($event)" aria-label="Tenant" data-testid="tenant-switcher">
            @for (t of tenants(); track t.id) { <mat-option [value]="t.id">{{ label(t) }}</mat-option> }
          </mat-select>
        </mat-form-field>
      } @else if (current(); as t) {
        <span class="name" data-testid="tenant-name">{{ label(t) }}</span>
      }
    </div>
    <div class="na-tenant-page"><router-outlet /></div>`,
})
export class TenantShellPage {
  private readonly access = inject(AccessService);
  private readonly router = inject(Router);
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  readonly tenantId = input.required<string>();

  private readonly loaded = toSignal(this.access.load(), { initialValue: { platformAdmin: false, tenants: [] } });
  protected readonly tenants = computed(() => this.loaded().tenants);
  protected readonly current = computed(() => this.tenants().find(t => sameId(t.id, this.tenantId())) ?? null);

  protected label(t: AccessTenant): string {
    return t.name ? `${t.name} (${t.id})` : t.id;
  }

  /** The same page in the other tenant: the path after the tenant id is kept, its query (filters, ids of this tenant) isn't. */
  protected switch(id: string): void {
    const prefix = under(this.base, `tenants/${this.tenantId()}`);
    const path = this.router.url.split('?')[0];
    const rest = path.toLowerCase().startsWith(prefix.toLowerCase()) ? path.slice(prefix.length) : '';
    // A detail page names a row of the other tenant: go to its list instead.
    const page = rest.split('/').filter(Boolean)[0] ?? '';
    void this.router.navigateByUrl(under(this.base, `tenants/${id}${page ? `/${page}` : ''}`));
  }
}
