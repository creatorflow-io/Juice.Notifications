import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AccessService, AccessTenant, EMPTY_ACCESS, sameId } from '../core/access.service';
import { NOTIFICATIONS_ADMIN_OPTIONS, under } from '../core/options';

/**
 * A tenant's pages (`/tenants/{id}/…`), under a bar naming the tenant. Someone who administers more than one picks another there and
 * stays on the same page in it (phase-4c-plan p4c-d2); a tenant admin has one and sees only its name. A platform admin also finds the
 * applications here.
 */
@Component({
  selector: 'na-tenant-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, RouterLink, RouterOutlet],
  styles: `
    .na-tenant-bar { display: flex; align-items: center; gap: 12px; padding: 8px 16px; border-bottom: 1px solid var(--mat-sys-outline-variant, #ddd); }
    .na-tenant-bar .label { color: var(--mat-sys-on-surface-variant, #555); font-size: 13px; }
    .na-tenant-bar .name { font-weight: 500; }
    .na-tenant-bar .platform { margin-left: auto; }
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
      @if (loaded().platformAdmin) {
        <a class="platform" [routerLink]="applications" data-testid="platform-applications">Applications and their tenants</a>
      }
    </div>
    <div class="na-tenant-page"><router-outlet /></div>`,
})
export class TenantShellPage {
  private readonly access = inject(AccessService);
  private readonly router = inject(Router);
  private readonly base = inject(NOTIFICATIONS_ADMIN_OPTIONS).basePath;
  /** The platform's pages, for a platform admin (the menu, the same for everyone, has none). */
  protected readonly applications = under(this.base, 'applications');
  readonly tenantId = input.required<string>();

  protected readonly loaded = toSignal(this.access.load(), { initialValue: EMPTY_ACCESS });
  // The listed tenants, and this one when it was opened by its link without being listed (a platform admin's, every tenant theirs).
  protected readonly tenants = computed(() => {
    const listed = this.loaded().tenants;
    return listed.some(t => sameId(t.id, this.tenantId())) ? listed : [{ id: this.tenantId(), name: null }, ...listed];
  });
  protected readonly current = computed(() => this.tenants().find(t => sameId(t.id, this.tenantId())) ?? null);

  protected label(t: AccessTenant): string {
    return t.name ? `${t.name} (${t.id})` : t.id;
  }

  /**
   * The same page in the other tenant: the page after the tenant id is kept; its query, fragment, matrix parameters and any row id (all
   * this tenant's) aren't.
   */
  protected switch(id: string): void {
    const segments = this.router.parseUrl(this.router.url).root.children['primary']?.segments.map(s => s.path) ?? [];
    const at = segments.findIndex((s, i) => i > 0 && segments[i - 1] === 'tenants' && sameId(s, this.tenantId()));
    const page = at >= 0 ? segments[at + 1] : undefined;
    void this.router.navigateByUrl(under(this.base, `tenants/${id}${page ? `/${page}` : ''}`));
  }
}
