import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, ChangeDetectorRef, Component, computed, inject, input, output, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap } from 'rxjs';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import type { EnvironmentStatus } from '../../models/deployment.model';
import type { DeployHistoryEntry, InstallationOverview } from '../../models/observed-deployment.model';
import { DEPLOYMENT_CLOCK, DeploymentStatusService } from '../../services/deployment-status.service';

function groupBy<T>(items: T[], key: (item: T) => string) {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    const name = key(item);
    const group = groups.get(name);
    if (group) group.push(item);
    else groups.set(name, [item]);
  }
  return [...groups].map(([name, entries]) => ({ name, entries }));
}

@Component({
  selector: 'app-installation-overview',
  imports: [DisclosureMarkerComponent, DatePipe, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './installation-overview.html',
  styleUrl: './installation-overview.scss',
})
export class InstallationOverviewComponent {
  readonly facts = input.required<InstallationOverview>();
  readonly instanceStatus = input<EnvironmentStatus>();
  readonly reportedBuilds = computed(() => {
    const release = this.instanceStatus()?.currentRelease;
    return release && this.drift(release.commitSha) === 'Aligned' ? release.artifactDigests : [];
  });
  readonly project = input.required<string>();
  readonly title = input('Production');
  readonly hasRecords = input(false);
  readonly openCard = output<string>();
  readonly openDeployment = output<string>();
  protected readonly changeDetector = inject(ChangeDetectorRef);
  private readonly now = inject(DEPLOYMENT_CLOCK);
  private readonly service = inject(DeploymentStatusService);
  readonly offset = signal(0);
  readonly machines = computed(() => groupBy(this.facts().components, component => component.machine));
  readonly areas = computed(() => groupBy(this.facts().waiting, card => card.area || 'Area not assigned'));
  readonly missingIdentity = computed(() => this.facts().components.some(row => !row.commit || !row.version));
  readonly oldest = computed(() => this.facts().waiting.map(card => card.integratedAtUtc)
    .filter((date): date is string => !!date).sort()[0]);
  readonly alignment = computed(() => {
    const { components } = this.facts();
    const different = components.filter(component => this.drift(component.commit) === 'Different commit');
    if (different.length) return different.map(component => component.name).join(', ') + ' on a different commit';
    if (!components.length || components.some(component => this.drift(component.commit) === 'Unknown'
      || component.health === 'Stale heartbeat')) return 'Commit alignment not fully observed';
    return components.length === 1 ? 'One reporting component' : 'All on the same commit';
  });
  readonly records = toSignal(toObservable(computed(() => ({ enabled: this.hasRecords(), offset: this.offset() })))
    .pipe(switchMap(({ enabled, offset }) => enabled ? this.service.deployRecords(offset).pipe(
      map(page => ({ ...page, error: false })), catchError(() => of({ items: [], total: 0, error: true }))) : of(null))));
  readonly usingRecords = computed(() => this.hasRecords() && !this.records()?.error);
  readonly history = computed(() => this.usingRecords() ? this.records()?.items ?? []
    : this.facts().history.slice(this.offset(), this.offset() + 25));
  readonly total = computed(() => this.usingRecords() ? this.records()?.total ?? 0 : this.facts().history.length);
  readonly maxDuration = computed(() => Math.max(1, ...this.history().map(entry => this.duration(entry) ?? 0)));

  waitDays(date: string): string {
    const days = Math.max(0, (this.now() - Date.parse(date)) / 86_400_000);
    return new Intl.NumberFormat('en', { maximumFractionDigits: 2 }).format(days);
  }

  drift(commit: string | null): string {
    const live = this.facts().commit?.toLowerCase();
    const observed = commit?.toLowerCase();
    if (!live || !observed || !/^[0-9a-f]{7,40}$/.test(observed) || !/^[0-9a-f]{7,40}$/.test(live)) return 'Unknown';
    return live.startsWith(observed) || observed.startsWith(live) ? 'Aligned' : 'Different commit';
  }

  age(date: string): string {
    const minutes = Math.max(0, Math.floor((this.now() - Date.parse(date)) / 60_000));
    return minutes < 60 ? minutes + ' min' : minutes < 1440 ? Math.floor(minutes / 60) + ' h' : Math.floor(minutes / 1440) + ' d';
  }

  duration(entry: Pick<DeployHistoryEntry, 'startedAtUtc' | 'endedAtUtc'>): number | null {
    if (!entry.startedAtUtc || !entry.endedAtUtc) return null;
    const seconds = (Date.parse(entry.endedAtUtc) - Date.parse(entry.startedAtUtc)) / 1000;
    return seconds >= 0 ? seconds : null;
  }

  stepBar(entry: DeployHistoryEntry, step: DeployHistoryEntry['steps'][number]) {
    const total = this.duration(entry);
    const seconds = this.duration(step);
    if (!total || seconds === null || !step.startedAtUtc || !entry.startedAtUtc) return null;
    const offset = (Date.parse(step.startedAtUtc) - Date.parse(entry.startedAtUtc)) / 1000;
    if (offset < 0 || offset + seconds > total) return null;
    return { start: 100 * offset / total, width: 100 * seconds / total };
  }

  elapsed(seconds: number | null): string {
    if (seconds === null) return 'Duration not observed';
    return seconds < 60 ? Math.round(seconds) + ' s' : Math.floor(seconds / 60) + ' min ' + Math.floor(seconds % 60) + ' s';
  }

  outcome(value: string): string {
    return value.replace(/[-_]/g, ' ').replace(/^./, first => first.toUpperCase());
  }
}
