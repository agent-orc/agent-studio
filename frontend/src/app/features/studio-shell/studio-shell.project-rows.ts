import { excludeEpics } from '../board';
import type { GroupedJobs, TaskInfo } from '../../models/task.model';
import { projectIdentity } from '../../services/project-identity.util';
import type { StructuredTooltip } from 'coding-agent-chat/shared';
import { laneName } from '../../models/lane-presentation';
import { TaskState } from '../../models/task.model';

export interface ExplorerLaneCounts {
  ready: number;
  progress: number;
  humanReview: number;
}

/**
 * Hover help for the three Explorer board lane counters (grey Ready /
 * amber In Progress / purple Human Review). Each entry feeds the canonical
 * `[cacTooltip]` directive as a {@link StructuredTooltip} so the number says
 * which lane it counts and what that lane means. Prose mirrors the lane
 * concept docs (`docs/app/help/lane-guides/lane-{2-ready,3-progress,5-human-review}.md`).
 */
export const BOARD_LANE_COUNT_TOOLTIPS: Record<keyof ExplorerLaneCounts, StructuredTooltip> = {
  ready: {
    title: laneName(TaskState.Ready),
    body: 'Refined tasks queued for a coding agent. The orchestrator runs the top card next when a slot frees up.',
  },
  progress: {
    title: laneName(TaskState.Progress),
    body: 'Tasks the orchestrator is actively running now, or resuming between attempts. One per project at a time.',
  },
  humanReview: {
    title: laneName(TaskState.HumanReview),
    body: 'Finished runs waiting for your review, including escalations that need a decision. Accept the work or send it back for another pass.',
  },
};

export interface ProjectSidebarRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  laneCounts: ExplorerLaneCounts;
  isActive: boolean;
}

interface ProjectAccumulator {
  laneCounts: ExplorerLaneCounts;
}

const EMPTY_LANE_COUNTS: ExplorerLaneCounts = { ready: 0, progress: 0, humanReview: 0 };

function emptyLaneCounts(): ExplorerLaneCounts {
  return { ...EMPTY_LANE_COUNTS };
}

export function laneCountsFor(project: { laneCounts?: ExplorerLaneCounts }): ExplorerLaneCounts {
  return project.laneCounts ?? EMPTY_LANE_COUNTS;
}

export function boardLaneCountsLabel(project: { laneCounts?: ExplorerLaneCounts }): string {
  const counts = laneCountsFor(project);
  return `${counts.ready} ready, ${counts.progress} in progress, ${counts.humanReview} human review`;
}

export function buildProjectSidebarRows(
  grouped: GroupedJobs,
  knownProjectNames: readonly string[],
  active: string | null,
): ProjectSidebarRow[] {
  const projects = new Map<string, ProjectAccumulator>();
  const ensureProject = (name: string): ProjectAccumulator => {
    let value = projects.get(name);
    if (!value) {
      value = { laneCounts: emptyLaneCounts() };
      projects.set(name, value);
    }
    return value;
  };

  // Count exactly what the board lanes render. The board strips epic
  // containers from every lane (`excludeEpics`, wired into `App.displayGrouped`)
  // because an epic is a container, not a board work-item - it lives in the
  // Epics view instead. Re-using that same function rather than re-deriving
  // `kind !== 'epic'` here keeps the two surfaces in lockstep by construction:
  // an operator saw a green lane dot on a project whose board lanes all showed
  // 0 tasks, because the tree counted an epic parked in 5-human-review that no
  // lane was allowed to draw (operator 2026-08-23).
  const boardGrouped = excludeEpics(grouped);

  for (const [laneKey, lane] of Object.entries(boardGrouped)) {
    // `review` is the legacy alias of `autoReview` (see GroupedJobs) - iterating
    // it would double-count every auto-review card. `archive` is terminal.
    // Neither feeds a visible board chip, so skip both outright.
    if (laneKey === 'archive' || laneKey === 'review') continue;
    for (const job of lane as TaskInfo[]) {
      // Register the project so a row still appears for it even when all its
      // work sits in lanes that do not feed the active-work count (backlog,
      // completed, ...).
      const project = ensureProject(job.projectName ?? '');
      if (laneKey === 'ready') project.laneCounts.ready++;
      else if (laneKey === 'progress') project.laneCounts.progress++;
      // Escalations wait on the human just like a plain human-review card
      // (arguably more urgently), so they fold into the Human Review chip
      // rather than falling out of every counter.
      else if (laneKey === 'humanReview' || laneKey === 'escalated') project.laneCounts.humanReview++;
    }
  }

  for (const name of knownProjectNames) ensureProject(name);

  return Array.from(projects.entries())
    .map(([name, project]) => {
      const id = projectIdentity(name);
      const { ready, progress, humanReview } = project.laneCounts;
      return {
        name,
        initial: id.initial,
        color: id.color,
        // The project number is "active work" and, by construction, the exact
        // sum of the three board chips shown directly under it (invariant:
        // every aggregate equals the sum of its visible children). Backlog,
        // Delivered/Completed and Archive are intake / done - not running
        // work - so they count nowhere here.
        totalJobs: ready + progress + humanReview,
        laneCounts: project.laneCounts,
        isActive: active === name,
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));
}
