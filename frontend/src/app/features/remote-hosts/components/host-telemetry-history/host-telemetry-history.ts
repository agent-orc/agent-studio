import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type {
  HostTelemetryFinding,
  HostTelemetryPoint,
  RemoteHost,
} from '../../models/remote-host.model';
import {
  freshHostTelemetry,
  latestHostTelemetry,
} from '../../models/running-truth';

@Component({
  selector: 'app-host-telemetry-history',
  standalone: true,
  imports: [SegmentedControlComponent, DatePipe, AppTooltipDirective],
  templateUrl: './host-telemetry-history.html',
  styleUrl: './host-telemetry-history.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostTelemetryHistoryComponent {
  readonly windowOptions = (['1h', '6h', '48h', '14d'] as const).map(value => ({ value, label: value, testid: 'remote-host-window-' + value }));
  readonly host = input.required<RemoteHost>();
  readonly now = input<number>(Date.now());
  readonly window = signal<'1h' | '6h' | '48h' | '14d'>('6h');
  readonly hoveredIndex = signal<number | null>(null);
  readonly latest = computed(() => latestHostTelemetry(this.host()));
  readonly live = computed(() => freshHostTelemetry(this.host(), this.now()));
  readonly stale = computed(() => this.latest() !== null && this.live() === null);
  readonly points = computed(() => {
    const hours = { '1h': 1, '6h': 6, '48h': 48, '14d': 336 }[this.window()];
    const cutoff = this.now() - hours * 60 * 60 * 1000;
    return (this.host().telemetry?.points ?? [])
      .filter(point => Date.parse(point.timestamp) >= cutoff);
  });
  readonly hovered = computed(() => {
    const points = this.points();
    const index = this.hoveredIndex();
    if (index === null) return null;
    const point = points[index] ?? null;
    if (!point) return null;
    return {
      index,
      point,
      position: points.length > 1 ? index * 100 / (points.length - 1) : 50,
      values: [
        { key: 'cpu', label: 'CPU', value: formatNumber(point.cpuPercent, '%') },
        { key: 'memory', label: 'Memory', value: formatNumber(
          point.memoryUsedBytes === null ? null : point.memoryUsedBytes / 1_000_000_000,
          ' GB',
        ) },
        { key: 'load', label: 'Load / cores', value: formatNumber(point.load1, ' load') },
        {
          key: 'slots',
          label: 'Active slots',
          value: `${point.activeSlots} ${point.activeSlots === 1 ? 'slot' : 'slots'}`,
        },
      ],
    };
  });
  readonly rows = computed(() => {
    const points = this.points();
    const hoveredPoint = this.hovered()?.point ?? null;
    return [
      { key: 'cpu', label: 'CPU', value: (p: HostTelemetryPoint) => p.cpuPercent, max: 100 },
      {
        key: 'memory',
        label: 'Memory',
        value: (p: HostTelemetryPoint) =>
          p.memoryUsedBytes !== null && p.memoryTotalBytes
            ? p.memoryUsedBytes * 100 / p.memoryTotalBytes
            : null,
        max: 100,
      },
      {
        key: 'load',
        label: 'Load / cores',
        value: (p: HostTelemetryPoint) => p.load1,
        max: Math.max(1, ...points.map(p => p.cpuCores), ...points.map(p => p.load1 ?? 0)),
      },
      {
        key: 'slots',
        label: 'Active slots',
        value: (p: HostTelemetryPoint) => p.activeSlots,
        max: Math.max(1, ...points.map(p => p.activeSlots)),
      },
    ].map(row => ({
      ...row,
      path: sparkline(points, row.value, row.max),
      hoverY: hoveredPoint ? sparklineY(row.value(hoveredPoint), row.max) : null,
    }));
  });
  readonly context = computed(() => {
    const point = this.latest();
    if (!point) return '';
    const freshness = this.live() ? '' : ' at last sample · stale';
    return `${point.activeSlots} RUN active${freshness} · host load ${(point.load1 ?? 0).toFixed(1)} of ${point.cpuCores} cores`;
  });
  readonly findings = computed(() => {
    const byPhase = new Map<string, HostTelemetryFinding>();
    for (const finding of this.host().telemetry?.findings ?? []) {
      const phase = finding.isActive === false ? 'history' : 'active';
      byPhase.set(`${finding.kind}:${phase}`, finding);
    }
    return [...byPhase.values()].sort((left, right) => {
      const activity = Number(right.isActive !== false) - Number(left.isActive !== false);
      return activity || right.until.localeCompare(left.until);
    });
  });
  readonly visibleFindings = computed(() => this.findings().slice(0, 3));
  readonly additionalFindingCount = computed(() =>
    Math.max(0, this.findings().length - this.visibleFindings().length));

  selectWindow(window: '1h' | '6h' | '48h' | '14d'): void {
    this.hoveredIndex.set(null);
    this.window.set(window);
  }

  showPoint(event: PointerEvent): void {
    const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect();
    if (bounds.width <= 0) return;
    const ratio = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width));
    const lastIndex = this.points().length - 1;
    if (lastIndex >= 0) this.hoveredIndex.set(Math.round(ratio * lastIndex));
  }

  hidePoint(event: PointerEvent): void {
    if (event.pointerType !== 'touch') this.hoveredIndex.set(null);
  }

  focus(): void {
    const lastIndex = this.points().length - 1;
    if (lastIndex >= 0 && this.hoveredIndex() === null) this.hoveredIndex.set(lastIndex);
  }

  move(event: KeyboardEvent): void {
    const lastIndex = this.points().length - 1;
    if (lastIndex < 0) return;
    const current = this.hoveredIndex() ?? lastIndex;
    let next: number;
    if (event.key === 'ArrowLeft') next = Math.max(0, current - 1);
    else if (event.key === 'ArrowRight') next = Math.min(lastIndex, current + 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = lastIndex;
    else if (event.key === 'Escape') {
      this.hoveredIndex.set(null);
      return;
    } else return;
    event.preventDefault();
    this.hoveredIndex.set(next);
  }

  clear(): void { this.hoveredIndex.set(null); }

  findingTooltip(finding: HostTelemetryFinding): string {
    const range = `${finding.since} to ${finding.until}`;
    return finding.isActive === false
      ? `${finding.occurrences ?? 1} completed phase(s), ${range}`
      : range;
  }
}

function sparkline(
  points: readonly HostTelemetryPoint[],
  value: (point: HostTelemetryPoint) => number | null,
  max: number,
): string {
  if (points.length < 2) return '';
  return points.map((point, index) => ({ index, value: value(point) }))
    .filter(item => item.value !== null)
    .map(item => `${(item.index * 100 / (points.length - 1)).toFixed(1)},${sparklineY(item.value, max)}`)
    .join(' ');
}

function sparklineY(value: number | null, max: number): string | null {
  if (value === null) return null;
  return (28 - Math.max(0, Math.min(1, value / max)) * 26).toFixed(1);
}

function formatNumber(value: number | null, unit: string): string {
  if (value === null || !Number.isFinite(value)) return '-';
  return `${Number(value.toFixed(2))}${unit}`;
}
