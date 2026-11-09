import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import type { Subscription } from 'rxjs';
import { laneTone } from '../../../../models/lane-presentation';
import { RemoteHostsService } from '../../../remote-hosts';
import { FlowHealthService, hostPlaneSlots, type FlowHealthLoad } from '../../services/flow-health.service';
import { deriveFlowHealth, formatAge, formatFigure, formatTarget, joinFlowSources } from '../../state/flow-health.derive';
import type { FlowBlockKey, FlowFigure, FlowStatus, FlowTrend } from '../../models/flow-health.model';

/** Detail pages a block links to: Deploy's Run steps view, or a Project rail. */
export type FlowDetailTarget = 'steps' | 'cycle-time' | 'overview';

interface DetailLink { target: FlowDetailTarget; label: string }

const BLOCK_DETAIL: Readonly<Record<FlowBlockKey | 'now', DetailLink>> = {
  now: { target: 'cycle-time', label: 'Lane dwell and bounces' },
  grouping: { target: 'steps', label: 'Gate economics' },
  gate: { target: 'steps', label: 'Pipeline health' },
  review: { target: 'overview', label: 'Review economics' },
};

const STATUS_LABELS: Readonly<Record<FlowStatus, string>> = {
  met: 'Met', missed: 'Missed', unknown: 'Unknown', 'not-applicable': 'Not applicable',
};

const TREND_LABELS: Readonly<Record<FlowTrend, string>> = {
  better: 'Better', worse: 'Worse', steady: 'Steady', unknown: 'No trend',
};

/** Live queues and slots refresh on this cadence; the window rollups on open and on Refresh. */
const LIVE_POLL_MS = 60_000;

/**
 * Project flow health: grouping, gate and review figures over 7 and 30 days
 * against their stated targets, the cards waiting per stage now, and one
 * sentence naming the main brake. A projection of existing read models only.
 */
@Component({
  selector: 'app-flow-health-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [FlowHealthService],
  templateUrl: './flow-health-page.html',
  styleUrl: './flow-health-page.scss',
})
export class FlowHealthPageComponent implements OnDestroy {
  readonly projectName = input.required<string>();
  readonly openDetail = output<FlowDetailTarget>();

  private readonly service = inject(FlowHealthService);
  private readonly hosts = inject(RemoteHostsService);
  private request: Subscription | null = null;
  private liveRequest: Subscription | null = null;
  private readonly poll = setInterval(() => this.refreshLive(), LIVE_POLL_MS);

  readonly loaded = signal<FlowHealthLoad | null>(null);
  readonly loading = signal(false);
  readonly nowMs = signal(Date.now());
  readonly view = computed(() => {
    const loaded = this.loaded();
    if (!loaded) return null;
    const nowMs = this.nowMs();
    return deriveFlowHealth(joinFlowSources(this.projectName(), nowMs,
      { ...loaded, ...hostPlaneSlots(this.hosts.hosts(), nowMs) }));
  });
  readonly detail = BLOCK_DETAIL;

  constructor() {
    effect(() => {
      const project = this.projectName();
      untracked(() => this.reload(project));
    });
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
    this.request?.unsubscribe();
    this.liveRequest?.unsubscribe();
  }

  refresh(): void { this.reload(this.projectName()); }

  private reload(project: string): void {
    this.request?.unsubscribe();
    this.liveRequest?.unsubscribe();
    this.loading.set(true);
    this.request = this.service.load(project).subscribe(value => {
      this.nowMs.set(Date.now());
      this.loaded.set(value);
      this.loading.set(false);
    });
  }

  private refreshLive(): void {
    const current = this.loaded();
    if (!current || this.loading()) return;
    this.liveRequest?.unsubscribe();
    this.liveRequest = this.service.live(this.projectName()).subscribe(live => {
      this.nowMs.set(Date.now());
      this.loaded.update(value => value && {
        ...value,
        pipelineHealth: live.pipelineHealth ?? value.pipelineHealth,
        gateSlots: live.gateSlots ?? value.gateSlots,
        economics: { ...value.economics, '7d': live.economics7d ?? value.economics['7d'] },
      });
    });
  }

  value(item: FlowFigure, which: 'value7d' | 'value30d' | 'all7d'): string {
    if (which === 'all7d' && item.all7d === undefined) return 'Per project';
    return formatFigure(item[which] ?? null, item.unit);
  }

  target(item: FlowFigure): string { return formatTarget(item.target); }
  age(minutes: number | null): string { return minutes == null ? '–' : formatAge(minutes); }
  waitTarget(minutes: number): string { return `≤ ${formatAge(minutes)}`; }
  status(status: FlowStatus): string { return STATUS_LABELS[status]; }
  trendLabel(trend: FlowTrend): string { return TREND_LABELS[trend]; }
  tone(lane: string | null): string | null { return lane ? laneTone(lane) : null; }
}
