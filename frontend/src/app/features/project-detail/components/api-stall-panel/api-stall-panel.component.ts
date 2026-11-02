import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import type { ApiStallSnapshot } from '../../../../models/api-stall.model';
import { ApiStallService } from '../../../../services/api-stall.service';

const POLL_MS = 60_000;
const SHOWN_STALLS = 5;

/**
 * Operations: responsiveness of the Studio API itself (AGT-3369). The API's
 * stall detector records every timer tick that ran late; a tick five seconds
 * late or more inside the last hour is an alarm. The panel stays quiet while
 * no stall is recorded and hides itself when the route is not readable.
 */
@Component({
  selector: 'app-api-stall-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './api-stall-panel.component.html',
  styleUrl: './api-stall-panel.component.scss',
})
export class ApiStallPanelComponent {
  private readonly stalls = inject(ApiStallService);
  readonly snapshot = signal<ApiStallSnapshot | null>(null);
  readonly shown = computed(() => this.snapshot()?.recent.slice(0, SHOWN_STALLS) ?? []);
  readonly statusLabel = computed(() => {
    switch (this.snapshot()?.status) {
      case 'alarm': return 'Stalled';
      case 'warning': return 'Slow';
      default: return 'Responsive';
    }
  });

  constructor() {
    this.refresh();
    const timer = setInterval(() => this.refresh(), POLL_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  refresh(): void {
    this.stalls.getSnapshot().subscribe({
      next: value => this.snapshot.set(value),
      error: () => this.snapshot.set(null),
    });
  }

  seconds(ms: number): string {
    return `${(ms / 1000).toFixed(1)} s`;
  }

  time(iso: string): string {
    const at = new Date(iso);
    return Number.isNaN(at.getTime()) ? iso : at.toLocaleString('en-GB');
  }
}
