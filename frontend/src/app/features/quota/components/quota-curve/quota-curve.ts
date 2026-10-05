import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { QuotaHistoryWindow } from '../../models/quota-history.model';
import { nearestCurvePoint, quotaCurveGeometry } from '../../quota-forecast.util';

/**
 * Weekly quota curve (AGT-3001): the recorded used-percent of one window over
 * the requested range, a dashed extrapolation toward 100% at the current
 * three-hour rate, and "now" and reset markers. A crosshair snaps to the
 * nearest reading on hover; a visually hidden table carries the same values.
 */
@Component({
  selector: 'app-quota-curve',
  standalone: true,
  templateUrl: './quota-curve.html',
  styleUrl: './quota-curve.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuotaCurveComponent {
  readonly window = input.required<QuotaHistoryWindow>();
  readonly from = input.required<string>();
  readonly to = input.required<string>();

  readonly hover = signal<number>(-1);

  readonly geometry = computed(() => quotaCurveGeometry(this.window(), this.from(), this.to()));

  readonly hovered = computed(() => {
    const index = this.hover();
    return index >= 0 ? this.geometry().points[index] ?? null : null;
  });

  readonly summary = computed(() => {
    const w = this.window();
    const f = w.forecast;
    const current = f.currentPct != null ? `${Math.round(f.currentPct)}% used` : 'no current reading';
    return `${w.label}: ${current}, ${w.points.length} readings in range.`;
  });

  onPointerMove(event: PointerEvent): void {
    const svg = event.currentTarget as SVGSVGElement;
    const rect = svg.getBoundingClientRect();
    if (rect.width === 0) return;
    const plotX = ((event.clientX - rect.left) / rect.width) * this.geometry().width;
    this.hover.set(nearestCurvePoint(this.geometry().points, plotX));
  }

  clearHover(): void {
    this.hover.set(-1);
  }

  tooltipLeftPct(x: number): number {
    return (x / this.geometry().width) * 100;
  }

  formatTime(iso: string): string {
    return new Date(iso).toLocaleString('en-US', {
      weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false,
    });
  }
}
