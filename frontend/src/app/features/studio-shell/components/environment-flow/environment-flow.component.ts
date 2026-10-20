import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { TaskInfo } from '../../../../models/task.model';
import { RemoteHostsService } from '../../../remote-hosts/services/remote-hosts.service';
import type { RemoteHost } from '../../../remote-hosts/models/remote-host.model';
import { freshHostTelemetry } from '../../../remote-hosts/models/running-truth';
import { hostZonePolicy } from '../../../remote-hosts/models/zone-policy';
import type { WatcherCase } from '../../../watcher/models/watcher.model';
import { minutes, type EnvironmentFlowSnapshot } from './environment-flow.model';
import { EnvironmentHealthOverviewComponent } from '../../../environment-health';

interface DecisionReceipt {
  receiptId: string; taskId: string | null; chosenAction: string; callStatus: string; endedAt: string;
}

@Component({
  selector: 'app-environment-flow',
  standalone: true,
  imports: [EnvironmentHealthOverviewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './environment-flow.component.html',
  styleUrl: './environment-flow.component.scss',
})
export class EnvironmentFlowComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  readonly hostsService = inject(RemoteHostsService);
  readonly tasks = input.required<readonly TaskInfo[]>();
  readonly closed = output<void>();
  readonly flow = signal<EnvironmentFlowSnapshot | null>(null);
  readonly cases = signal<readonly WatcherCase[]>([]);
  readonly receipts = signal<readonly DecisionReceipt[]>([]);
  readonly error = signal<string | null>(null);
  readonly updatedAt = signal<Date | null>(null);
  readonly hosts = computed(() => this.hostsService.hosts());
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => {
      if (!document.hidden) this.refresh();
    }, 15_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  refresh(): void {
    this.hostsService.refresh();
    this.http.get<EnvironmentFlowSnapshot>('/api/environment/flow').subscribe({
      next: data => { this.flow.set(data); this.updatedAt.set(new Date(data.capturedAt)); this.error.set(null); },
      error: () => this.error.set('Flow data is temporarily unavailable.'),
    });
    this.http.get<{ cases: WatcherCase[] }>('/api/watcher/cases').subscribe({
      next: data => this.cases.set((data.cases ?? []).filter(item =>
        item.state === 'open' || item.state === 'decision-required')),
      error: () => this.cases.set([]),
    });
    this.http.get<DecisionReceipt[]>('/api/orchestration/decision-receipts?limit=100').subscribe({
      next: data => this.receipts.set(data ?? []),
      error: () => this.receipts.set([]),
    });
  }

  readonly minutes = minutes;
  readonly zonePolicy = hostZonePolicy;
  private hostFresh(host: RemoteHost): boolean {
    const heartbeatAt = host.lastHeartbeatAt ? Date.parse(host.lastHeartbeatAt) : Number.NaN;
    return host.status !== 'offline' && host.status !== 'retired'
      && Number.isFinite(heartbeatAt) && Date.now() - heartbeatAt <= 5 * 60_000;
  }
  loadPerCore(host: RemoteHost): string {
    const point = freshHostTelemetry(host);
    const cores = point?.cpuCores;
    return point?.load1 != null && cores ? (point.load1 / cores).toFixed(2) : 'Unavailable';
  }
  memory(host: RemoteHost): string {
    const point = freshHostTelemetry(host);
    if (point?.memoryUsedBytes != null && point.memoryTotalBytes)
      return `${Math.round(point.memoryUsedBytes / point.memoryTotalBytes * 100)}%`;
    if (this.hostFresh(host) && host.stats?.ramTotalMb)
      return `${Math.round((1 - host.stats.ramFreeMb / host.stats.ramTotalMb) * 100)}%`;
    return 'Unavailable';
  }
  usedSlots(host: RemoteHost): number | null {
    if (!this.hostFresh(host)) return null;
    return freshHostTelemetry(host)?.activeSlots ?? host.activeTaskCount ?? null;
  }
  totalSlots(host: RemoteHost): number | null {
    return host.effectiveMaxParallelism ?? host.runtimeCapacity?.maxParallelism ?? host.roleMaxParallelism ?? null;
  }
  freeSlots(host: RemoteHost): number | null {
    const total = this.totalSlots(host);
    const used = this.usedSlots(host);
    return total === null || used === null ? null : Math.max(0, total - used);
  }
  auth(host: RemoteHost): readonly { name: string; status: string }[] {
    return (host.capabilityHealth ?? [])
      .filter(cap => cap.category.toLowerCase().includes('provider') || cap.category.toLowerCase().includes('auth'))
      .map(cap => ({ name: cap.key, status: cap.credentialHealth ?? cap.healthOutcome ?? cap.signal ?? cap.healthState }));
  }
  running(host: RemoteHost): readonly TaskInfo[] {
    return this.tasks().filter(task => {
      const coding = task.state === '3-progress' && task.runner
        && (task.runner.runnerId === host.id || task.runner.hostname === host.name
          || task.runner.runnerName === host.name);
      const local = host.role === 'local' && task.state === '3-progress' && !task.runner?.isRemote
        && (task.runActivity?.kind === 'active' || task.runActivity?.kind === 'continuing-after-restart');
      const review = task.liveStatus?.queue?.state === 'reviewing'
        && (task.liveStatus.queue.executorId === host.id
          || task.liveStatus.queue.executorId === host.clientId);
      return coding || local || review;
    });
  }
  runningEmptyLabel(host: RemoteHost): string {
    return (this.usedSlots(host) ?? 0) > 0 ? 'Attempt IDs unavailable' : 'None reported';
  }
  age(task: TaskInfo): string {
    const started = task.liveStatus?.queue?.state === 'reviewing'
      ? task.liveStatus.queue.since : task.runner?.acquiredAt ?? task.phaseEnteredAt ?? task.enteredLaneAt;
    const minutesElapsed = started ? (Date.now() - Date.parse(started)) / 60_000 : null;
    return minutes(minutesElapsed != null && Number.isFinite(minutesElapsed) ? Math.max(0, minutesElapsed) : null);
  }
  hostMarkers(host: RemoteHost): number {
    return this.cases().filter(item => item.lastSummary.toLowerCase().includes(host.name.toLowerCase())
      || this.tasks().some(task => item.affectedCards.includes(task.id) || item.affectedCards.includes(task.taskKey)
        ? task.runner?.runnerId === host.id || task.runner?.hostname === host.name : false)).length;
  }
  laneMarkers(lane: string): number {
    return this.cases().filter(item => this.tasks().some(task => task.state === lane
      && (item.affectedCards.includes(task.id) || item.affectedCards.includes(task.taskKey)))).length;
  }
  queueMarkers(queue: string): number {
    const lane = queue === 'Coding' ? '2-ready' : queue === 'Review' ? '4-auto-review'
      : queue === 'Integration' ? '5-human-review' : null;
    return lane ? this.laneMarkers(lane) : 0;
  }
  latestReceipt(item: WatcherCase): DecisionReceipt | null {
    const ids = new Set(item.affectedCards);
    for (const task of this.tasks()) {
      if (ids.has(task.id) || ids.has(task.taskKey)) ids.add(task.id);
    }
    return this.receipts().filter(receipt => receipt.taskId && ids.has(receipt.taskId))
      .sort((a, b) => Date.parse(b.endedAt) - Date.parse(a.endedAt))[0] ?? null;
  }
}
