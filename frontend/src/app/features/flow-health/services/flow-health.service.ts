import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { catchError, forkJoin, map, of, type Observable } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import { BatchGateService } from '../../../services/batch-gate.service';
import { ProjectCycleTimeService } from '../../project-cycle-time';
import { summarizeStatusBarSlotsByRole, type RemoteHost, type StatusBarPlaneSlots } from '../../remote-hosts';
import type { FlowHealthResponses, FlowSlots, FlowWindow } from '../models/flow-health.model';

/** Everything the page loads; the host-plane slots come from the shared host registry signal instead. */
export type FlowHealthLoad = Omit<FlowHealthResponses, 'reviewSlots' | 'codingSlots'>;
/** The sources the page re-reads on its poll: queues and slots, not the window rollups. */
export type FlowHealthLiveLoad = Pick<FlowHealthResponses, 'pipelineHealth' | 'gateSlots'>
  & { economics7d: FlowHealthResponses['economics']['7d'] };

interface ExecutionInfrastructure { gate?: { busy?: number | null; total?: number | null } | null }

/**
 * Loads the existing read models the flow-health page joins. Every endpoint
 * here reads files, stores and in-memory counters only; none starts a Git
 * process (the batch `/state` read, which diffs pending cards, is
 * deliberately not used). A failing source degrades to null, never to zero.
 */
@Injectable()
export class FlowHealthService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  private readonly batches = inject(BatchGateService);
  private readonly cycleTime = inject(ProjectCycleTimeService);

  load(project: string): Observable<FlowHealthLoad> {
    const cycle = (window: FlowWindow) => orNull(this.cycleTime.load(project, window));
    return forkJoin({
      economics30d: orNull(this.tasks.getGateEconomics('30d')),
      cycle7d: cycle('7d'),
      cycle30d: cycle('30d'),
      live: this.live(project),
      batchReport: orNull(this.batches.report(project)),
      batchingEnabled: orNull(this.batches.getSettings(project).pipe(map(value => value.settings.enabled))),
    }).pipe(map(loaded => ({
      economics: { '7d': loaded.live.economics7d, '30d': loaded.economics30d },
      cycleTime: { '7d': loaded.cycle7d, '30d': loaded.cycle30d },
      pipelineHealth: loaded.live.pipelineHealth,
      gateSlots: loaded.live.gateSlots,
      batchReport: loaded.batchReport,
      batchingEnabled: loaded.batchingEnabled,
    })));
  }

  live(project: string): Observable<FlowHealthLiveLoad> {
    return forkJoin({
      pipelineHealth: orNull(this.tasks.getProjectPipelineHealth(project)),
      gateSlots: orNull(this.http.get<ExecutionInfrastructure>('/api/system/execution-infrastructure')
        .pipe(map(value => slots(value.gate?.busy, value.gate?.total)))),
      economics7d: orNull(this.tasks.getGateEconomics('7d')),
    });
  }
}

/** Review and coding plane occupancy from the host registry the status bar already keeps fresh. */
export function hostPlaneSlots(hosts: readonly RemoteHost[], nowMs: number): Pick<FlowHealthResponses, 'reviewSlots' | 'codingSlots'> {
  const planes = summarizeStatusBarSlotsByRole(hosts, nowMs);
  const plane = (value: StatusBarPlaneSlots) => value.hostCount === 0 ? null
    : slots(value.hasUtilization ? value.active : null, value.ceiling);
  return { reviewSlots: plane(planes.review), codingSlots: plane(planes.coding) };
}

function slots(busy: number | null | undefined, total: number | null | undefined): FlowSlots | null {
  return busy == null && total == null ? null : { busy: busy ?? null, total: total ?? null };
}

function orNull<T>(source: Observable<T>): Observable<T | null> {
  return source.pipe(catchError(() => of(null)));
}
