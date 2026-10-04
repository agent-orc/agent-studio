import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { laneName } from '../../../../models/lane-presentation';
import type {
  OperatorSweepCardState,
  OperatorSweepProjection,
  OperatorSweepStatus,
} from '../../../task-pipeline';

const SWEEP_LABELS: Record<string, string> = {
  'fix-rounds': 'Fix rounds',
  'gate-triage': 'Gate triage',
  salvage: 'Salvage',
};

const SWEEP_ROLES: Record<string, string> = {
  'fix-rounds': 'ProductFailure review to fix round',
  'gate-triage': 'Merge gate product failure to fix round',
  salvage: 'Timed-out run continued from its salvage',
};

/**
 * AGT-3011: health and operator control of the in-product operator sweeps,
 * rendered next to the pipeline health alarm. Shows when each sweep last ran
 * and what it did, the shared round budget per card, why each sweep did or did
 * not act, and which cards wait for a person.
 */
@Component({
  selector: 'app-operator-sweeps-block',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './operator-sweeps-block.html',
  styleUrl: './operator-sweeps-block.scss',
})
export class OperatorSweepsBlockComponent implements OnDestroy {
  readonly projectName = input.required<string>();
  readonly projection = signal<OperatorSweepProjection | null>(null);
  readonly busySweep = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly budgetCards = computed(() =>
    (this.projection()?.cards ?? []).filter(card => card.roundsUsed > 0 || card.decisions.length > 0));
  private readonly tasks = inject(TaskService);
  private readonly poll = setInterval(() => this.refresh(), 60_000);

  constructor() {
    effect(() => {
      if (this.projectName()) this.refresh();
    });
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
  }

  sweepLabel(sweep: string): string {
    return SWEEP_LABELS[sweep] ?? sweep;
  }

  sweepRole(sweep: string): string {
    return SWEEP_ROLES[sweep] ?? '';
  }

  laneLabel(lane: string): string {
    return laneName(lane);
  }

  stateLabel(status: string): string {
    switch (status) {
      case 'alarm': return 'Attention needed';
      case 'paused': return 'All paused';
      case 'disabled': return 'Disabled';
      default: return 'Healthy';
    }
  }

  lastRun(sweep: OperatorSweepStatus): string {
    if (!sweep.lastRunFinishedAtUtc) return 'Not run since start';
    return `Last run ${this.time(sweep.lastRunFinishedAtUtc)}`;
  }

  lastResult(sweep: OperatorSweepStatus): string {
    return `${sweep.lastActed} acted · ${sweep.lastHeld} held · ${sweep.lastWaitingForPerson} waiting`;
  }

  latestDecision(card: OperatorSweepCardState): string {
    const decision = card.decisions.find(item => item.action === 'Act')
      ?? card.decisions.find(item => item.action === 'WaitForPerson')
      ?? card.decisions[0];
    return decision ? `${this.sweepLabel(decision.sweep)}: ${decision.detail ?? decision.reason}` : 'No sweep trigger';
  }

  toggle(sweep: OperatorSweepStatus): void {
    const project = this.projectName();
    if (!project || this.busySweep()) return;
    this.busySweep.set(sweep.sweep);
    this.actionError.set(null);
    this.tasks.setOperatorSweepPaused(project, sweep.sweep, !sweep.paused).subscribe({
      next: projection => {
        this.projection.set(projection);
        this.busySweep.set(null);
      },
      error: () => {
        this.actionError.set(`Could not ${sweep.paused ? 'resume' : 'pause'} ${this.sweepLabel(sweep.sweep)}.`);
        this.busySweep.set(null);
      },
    });
  }

  private time(value: string): string {
    return new Date(value).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }

  private refresh(): void {
    const project = this.projectName();
    if (!project) return;
    this.tasks.getProjectOperatorSweeps(project).subscribe({
      next: projection => this.projection.set(projection),
      error: () => { /* additive signal: keep the last known projection */ },
    });
  }
}
