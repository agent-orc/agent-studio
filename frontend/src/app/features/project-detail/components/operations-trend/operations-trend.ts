import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { OperationsMarker, OperationsMetrics } from '../project-operations/project-operations.model';

type Measure = 'responseP95Ms' | 'errorRate' | 'requests' | 'cpuCores' | 'memoryBytes' | 'diskBytes';

@Component({
  selector: 'app-operations-trend', standalone: true,
  templateUrl: './operations-trend.html', styleUrl: './operations-trend.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationsTrendComponent {
  readonly label = input.required<string>();
  readonly measure = input.required<Measure>();
  readonly metrics = input.required<OperationsMetrics>();
  readonly markers = input.required<OperationsMarker[]>();
  readonly since = input.required<string>();
  readonly until = input.required<string>();
  readonly openRelease = output<string>();

  private readonly bounds = computed(() => ({ start: Date.parse(this.since()), end: Date.parse(this.until()) }));
  private x(at: string): number {
    const { start, end } = this.bounds();
    return end > start ? Math.max(0, Math.min(500, (Date.parse(at) - start) / (end - start) * 500)) : 0;
  }
  readonly positions = computed(() => this.markers()
    .filter(marker => Date.parse(marker.atUtc) >= this.bounds().start && Date.parse(marker.atUtc) <= this.bounds().end)
    .map(marker => ({ id: marker.deploymentId, releaseId: marker.releaseId, x: this.x(marker.atUtc),
      title: `${marker.trigger} · ${marker.version} · ${marker.atUtc}` })));
  onMarkerKeydown(event: KeyboardEvent, releaseId: string): void {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    this.openRelease.emit(releaseId);
  }
  readonly segments = computed(() => {
    const values = this.metrics().samples.map(sample => sample[this.measure()]);
    const max = Math.max(0.000001, ...values.filter((value): value is number => value !== null));
    const segments: string[] = [];
    let points: string[] = [];
    this.metrics().samples.forEach((sample, index) => {
      const value = values[index];
      if (value === null) {
        if (points.length) segments.push(points.join(' '));
        points = [];
      } else {
        points.push(`${this.x(sample.at)},${75 - Math.max(0, value) / max * 70}`);
      }
    });
    if (points.length) segments.push(points.join(' '));
    return segments;
  });
}
