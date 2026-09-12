import { ChangeDetectionStrategy, Component } from '@angular/core';

import { TaskState } from '../../../app/models/task.model';
import type { TaskInfo, TaskRunActivity } from '../../../app/models/task.model';
import { buildRunActivityBadge, type RunActivityBadge } from '../../../app/services/run-activity.util';

/**
 * ASS-1751 visual harness for the run-activity pill that makes a 3-progress card
 * self-explanatory. A progress task can look "untouched" for three different
 * reasons; this gallery renders the production pill markup + SCSS (copied
 * verbatim from `task-card.component`) for each, driven by the real, shipped
 * `buildRunActivityBadge` so the screenshot proves the actual product copy:
 *   (a) failed + rapid-crash backoff → "failed · Backoff bis HH:MM",
 *   (b) failed but idle (no backoff)  → "failed · kein aktiver Run",
 *   (c) orphan / no active run        → "kein aktiver Run",
 *   (d) active run                    → "Run aktiv" (+ PID in the tooltip).
 *
 * Backend-free: the pill is a pure function of `TaskInfo`, so no services, HTTP
 * or SignalR are needed — the same precedent as the other src/mockups/*. The
 * tooltip body is rendered inline next to each pill so the screenshot also
 * captures the Attempt/last-error detail the directive would show on hover.
 */

// A fixed "now" so the backoff clock renders deterministically (12:00 local).
const NOW = Date.parse('2026-06-10T12:00:00');

function makeProgressJob(runActivity: TaskRunActivity): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'test::task-1',
    key: 'ASS-1751',
    title: 'Make the 3-progress state legible',
    state: TaskState.Progress,
    order: 1,
    agent: 'codex',
    createdAt: '2026-06-10T09:00:00Z',
    watchPath: '/tmp/watch',
    projectName: 'Agent Task Processor',
    folderPath: '/tmp/watch/3-progress/task-1',
    lastActivity: '2026-06-10T11:30:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    runActivity,
  } as TaskInfo;
}

interface Scenario {
  id: string;
  title: string;
  truth: string;
  job: TaskInfo;
}

const SCENARIOS: readonly Scenario[] = [
  {
    id: 'continuing-after-restart',
    title: 'Studio restart bridged to the same worker',
    truth: 'The replacement Studio reattached to the original PID and attempt.',
    job: makeProgressJob({
      kind: 'active',
      processId: 48212,
      attempt: 0,
      continuingAfterRestart: true,
    }),
  },
  {
    id: 'active',
    title: '(c) Run aktiv — PID lebt, belegt einen Slot',
    truth: 'Ein Run-Prozess läuft und arbeitet (ggf. still). Belegt einen Slot.',
    job: makeProgressJob({ kind: 'active', processId: 48213, attempt: 0 }),
  },
  {
    id: 'failed-backoff',
    title: '(a) failed + Rapid-Crash-Backoff aktiv',
    truth: 'Letzter Run failed; Re-Pickup ist bis 12:03 gesperrt. Attempt 3, letzter Fehler sichtbar.',
    job: makeProgressJob({
      kind: 'failed-backoff',
      backoffUntil: new Date(NOW + 3 * 60_000).toISOString(),
      attempt: 3,
      lastError: 'git push rejected (non-fast-forward)',
    }),
  },
  {
    id: 'failed-idle',
    title: 'failed · kein aktiver Run (kein Backoff)',
    truth: 'Letzter Run failed, nichts läuft mehr, kein Backoff — wieder aufnahmebereit.',
    job: makeProgressJob({
      kind: 'failed-idle',
      attempt: 1,
      lastError: 'missing [[TASK_DONE]] sentinel',
    }),
  },
  {
    id: 'no-active-run',
    title: '(b) kein aktiver Run — Restart-/Recovery-Waise',
    truth: 'Run vom Backend-Neustart gekillt, noch nicht re-picked. Liegt rum, aber kein Prozess.',
    job: makeProgressJob({ kind: 'no-active-run', attempt: 0 }),
  },
];

@Component({
  selector: 'mockup-run-activity-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [],
  templateUrl: './run-activity-gallery.component.html',
  styleUrl: './run-activity-gallery.component.scss',
})
export class RunActivityGalleryComponent {
  readonly scenarios = SCENARIOS;

  badge(job: TaskInfo): RunActivityBadge | null {
    return buildRunActivityBadge(job, NOW);
  }
}
