import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
} from '@angular/core';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { NowTickService } from '../../../../services/now-tick.service';
import { shortModelName, stateLabel } from '../../../../services/format.util';
import { TaskState } from '../../../../models/task.model';

/**
 * "Where am I right now" header for the orchestrator side sheet.
 *
 * Answers the operator's first question when they open the orchestrator in
 * a project: which project, which task (when a task detail is in scope),
 * which lane / state, and whether a CLI run is live right now (with model
 * and a ticking duration). It is deliberately data-only — the host feeds
 * it the resolved scope so the same component can later be reused verbatim
 * for a task-focused orchestrator surface (the larger multichat concept is
 * a separate planning task; this header is built to drop straight into it).
 *
 * Visual language matches the sidesheet chrome (studio tokens + the sheet's
 * `--orch-accent` alias) rather than inventing a new palette. The live-run
 * pulse is the only motion; static scopes carry no animation so the header
 * stays calm when nothing is executing.
 */
@Component({
  selector: 'app-orchestrator-context-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent, TooltipDirective, StatusIndicatorComponent],
  templateUrl: './orchestrator-context-header.component.html',
  styleUrl: './orchestrator-context-header.component.scss',
})
export class OrchestratorContextHeaderComponent {
  /** Active project name shown as the primary scope. Null renders nothing. */
  readonly project = input<string | null>(null);
  /** Title of the task in scope; absence means the operator is on the board. */
  readonly taskTitle = input<string | null>(null);
  /** Short task key (e.g. `AGT-1916`) shown next to the title when present. */
  readonly taskKey = input<string | null>(null);
  /** Canonical lane key (e.g. `3-progress`) for the task in scope. */
  readonly taskState = input<string | null>(null);

  /** Whether a CLI run is executing in the current scope right now. */
  readonly runActive = input(false);
  /** Model id of the live run; formatted via {@link shortModelName}. */
  readonly runModel = input<string | null>(null);
  /** ISO start timestamp of the live run; drives the ticking duration. */
  readonly runStartedAt = input<string | null>(null);

  /**
   * MC-2: whether the sheet's context is pinned (frozen) rather than
   * following navigation. Data-only, like the rest of this header — the
   * pin toggle itself lives in the sheet toolbar; here it only renders a
   * subtle "Pinned" chip so the operator can see the scope is frozen.
   */
  readonly pinned = input(false);
  /** Canonical navigation context key (`project:<P>` / `task:<P>/<K>`). */
  readonly contextKey = input<string | null>(null);

  private readonly nowTick = inject(NowTickService);

  readonly hasProject = computed<boolean>(() => !!this.project()?.trim());
  readonly hasTask = computed<boolean>(() => !!this.taskTitle()?.trim());

  /** "Task" when a task is open, otherwise the board is the scope. */
  readonly scopeLabel = computed<string>(() => (this.hasTask() ? 'Task' : 'Board'));

  /** Human lane label for the task in scope, else null (board scope). */
  readonly laneLabel = computed<string | null>(() => {
    const state = this.taskState()?.trim();
    if (!state) return null;
    return stateLabel(state);
  });

  /**
   * Coarse lane tone so the state pill can shift colour without every
   * consumer re-deriving the mapping. Mirrors the board's lane grouping:
   * progress → live accent, review lanes → warn, delivered → success.
   */
  readonly laneTone = computed<'progress' | 'review' | 'done' | 'neutral'>(() => {
    const state = this.taskState()?.trim();
    if (!state) return 'neutral';
    if (state === TaskState.Progress) return 'progress';
    if (state === TaskState.AutoReview || state === TaskState.HumanReview || state === TaskState.Escalated) {
      return 'review';
    }
    if (state === TaskState.Completed || state === TaskState.Archive) return 'done';
    return 'neutral';
  });

  readonly runModelLabel = computed<string>(() => shortModelName(this.runModel()));

  /**
   * Live run duration, ticking off the shared 15 s clock. Reads
   * {@link NowTickService.now} so the value stays stable inside a change
   * detection cycle (no NG0100) while still refreshing as the run continues.
   */
  readonly runDurationLabel = computed<string | null>(() => {
    const started = this.runStartedAt();
    if (!this.runActive() || !started) return null;
    const startMs = new Date(started).getTime();
    if (Number.isNaN(startMs)) return null;
    const elapsedMs = Math.max(0, this.nowTick.now() - startMs);
    return formatElapsed(elapsedMs);
  });

  readonly runTooltip = computed<string>(() => {
    const model = this.runModelLabel();
    const dur = this.runDurationLabel();
    return dur ? `Live run · ${model} · running ${dur}` : `Live run · ${model}`;
  });
}

/**
 * Compact elapsed formatter for a live run: seconds under a minute, then
 * `Xm`, then `Xh Ym`. Rounds down so the label never claims time that has
 * not elapsed yet. Kept module-private (pure) so the component controller
 * stays declarative.
 */
export function formatElapsed(ms: number): string {
  const totalSec = Math.floor(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const totalMin = Math.floor(totalSec / 60);
  if (totalMin < 60) return `${totalMin}m`;
  const hrs = Math.floor(totalMin / 60);
  const mins = totalMin % 60;
  return mins > 0 ? `${hrs}h ${mins}m` : `${hrs}h`;
}
