import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { NotificationsAdminApi } from '../core/api.service';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/ui';

/**
 * Storms (phase-4-plan I43): a route taking more notifications a minute than the tenant's threshold sends its recipients to digests until
 * it calms. The current ones, or every one recorded.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatProgressBarModule, MatSlideToggleModule],
  template: `
    <div class="na-head"><h1>Storms</h1><span class="na-spacer"></span>
      <mat-slide-toggle [ngModel]="all()" (ngModelChange)="all.set($event)" data-testid="storms-all">Past ones too</mat-slide-toggle>
      <button mat-stroked-button type="button" (click)="storms.reload()">Refresh</button></div>
    @if (storms.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (storms.error()) { <p class="na-error" data-testid="storms-error">{{ error() }}</p> }
    <table class="na-table" data-testid="storms">
      <thead><tr><th>Route</th><th>Started</th><th>Ended</th><th>Peak a minute</th><th>Sent to digests</th></tr></thead>
      <tbody>
        @for (s of storms.value() ?? []; track s.id) {
          <tr data-testid="storm">
            <td>{{ s.route }}</td>
            <td class="na-nowrap">{{ s.startedAt | date: 'MMM d, HH:mm' }}</td>
            <td class="na-nowrap">@if (s.endedAt) { {{ s.endedAt | date: 'MMM d, HH:mm' }} } @else { <span class="na-badge na-warn">ongoing</span> }</td>
            <td>{{ s.peak }}</td>
            <td>{{ s.digested }}</td>
          </tr>
        } @empty { @if (!storms.isLoading()) { <tr><td colspan="5" class="na-muted">{{ all() ? 'No storms recorded.' : 'No storm now.' }}</td></tr> } }
      </tbody>
    </table>`,
})
export class StormsPage {
  private readonly api = inject(NotificationsAdminApi);
  readonly tenantId = input.required<string>();
  protected readonly all = signal(false);
  protected readonly storms = apiResource({ params: () => ({ tenantId: this.tenantId(), all: this.all() }),
    stream: ({ params }) => this.api.storms(params.tenantId, params.all) });
  protected readonly error = computed(() => problemText(this.storms.error()));
}
