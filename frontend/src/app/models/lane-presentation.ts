import { TaskState } from './task.model';

/**
 * Lane presentation: the one place that decides how a lane is *named*,
 * *worded*, *coloured*, and *illustrated* anywhere in the UI.
 *
 * Why this exists (operator report 2026-09-06, AGT-2692): the single lane
 * `5-human-review` used to be rendered four different ways at once — the task
 * header chip said "Review", the Result tab header said "Human review lane",
 * a badge helper rewrote that string to "Human review", and the project
 * workflow section said "Awaiting human review." Two different tones came
 * along for the ride (blue chip, amber dot). Every one of those surfaces
 * defined its own literal; none of them read a shared source.
 *
 * The rule now: **no component, service, util, or template keeps its own lane
 * string or lane colour.** They read {@link lanePresentation} (or one of the
 * `laneName` / `laneShortName` / `laneSentence` / `laneGlyph` / `laneTone` /
 * `laneDocTopic` accessors) and render what it returns. `TaskState` stays the
 * source of truth for lane *keys*; this module is the source of truth for lane
 * *presentation*.
 *
 * Enforced by `npm run lint:structure`
 * (`frontend/scripts/check-lane-presentation.mjs`), which fails when a lane key
 * and a lane display string appear together outside this module.
 *
 * Colour: {@link LanePresentation.tone} names a CSS custom property declared in
 * `src/styles/_tokens-semantic.scss`. Surfaces do not read it through
 * JavaScript — they stamp `data-lane="<state>"` on an element and
 * `src/styles/_lane-tone.scss` resolves `--studio-lane-tone` for the subtree,
 * in both themes. See `docs/system/domains/frontend.md` (Lane Presentation
 * Contract).
 */
export interface LanePresentation {
  /** Canonical lane key, e.g. `5-human-review`. */
  readonly state: string;
  /** Primary display name. Board column headings, tables, tooltips. */
  readonly name: string;
  /** Compact form for chips, pills, and the lane `<select>`. */
  readonly shortName: string;
  /** One sentence explaining what the lane means, terminated with a period. */
  readonly sentence: string;
  /** CSS custom property holding this lane's tone, declared in `_tokens-semantic.scss`. */
  readonly tone: string;
  /** Lane glyph used by board headers, rails, and lane pickers. */
  readonly glyph: string;
  /** Concept-doc topic for the lane-info modal, or `null` when the lane has no guide. */
  readonly docTopic: string | null;
}

function lane(
  state: string,
  name: string,
  shortName: string,
  sentence: string,
  toneSlug: string,
  glyph: string,
  docTopic: string | null,
): LanePresentation {
  return { state, name, shortName, sentence, tone: `--studio-lane-${toneSlug}`, glyph, docTopic };
}

/**
 * Canonical lanes in board order (left to right), which is *not*
 * `ALL_TASK_STATES` order — that one is declaration order and puts
 * `5e-escalated` before `5-human-review`.
 */
export const LANE_PRESENTATION_ORDER: readonly string[] = [
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.OrchestratorPrep,
  TaskState.Ready,
  TaskState.Progress,
  TaskState.FailedPickup,
  TaskState.CodeNotComplete,
  TaskState.AutoReview,
  TaskState.Escalated,
  TaskState.HumanReview,
  TaskState.Completed,
  TaskState.Archive,
];

/**
 * Every lane the UI can render, canonical and virtual.
 *
 * Wording note: `5-human-review` is "Human review" everywhere. The bare word
 * "Review" was ambiguous next to `4-auto-review` ("Post Processing") and next
 * to the pipeline's own review step, and it is the lane the operator is asked
 * to act on, so it names the actor.
 */
const CANONICAL: readonly LanePresentation[] = [
  lane(TaskState.Backlog, 'Backlog', 'Backlog',
    'Captured but not yet scheduled.', 'backlog', '🗒️', 'lane-0-backlog'),
  lane(TaskState.Preparation, 'In Preparation', 'Preparation',
    'Intake and preparation before the task is workable.', 'preparation', '📋', 'lane-1-preparation'),
  lane(TaskState.OrchestratorPrep, 'Orchestrator Prep', 'Orchestrator Prep',
    'The orchestrator is preparing the task for pickup.', 'orchestrator-prep', '🛂', 'lane-1a-orchestrator-prep'),
  lane(TaskState.Ready, 'Ready', 'Ready',
    'Queued and ready for pickup.', 'ready', '📦', 'lane-2-ready'),
  lane(TaskState.Progress, 'In Progress', 'In Progress',
    'A run is executing the task (runner-owned).', 'progress', '🔵', 'lane-3-progress'),
  lane(TaskState.FailedPickup, 'Failed pickup', 'Failed pickup',
    'Pickup failed before a run could start.', 'failed-pickup', '⚠️', null),
  lane(TaskState.CodeNotComplete, 'Code not complete', 'Code not complete',
    'A run stopped before the code was complete.', 'code-not-complete', '🚧', null),
  lane(TaskState.AutoReview, 'Post Processing', 'Post Processing',
    'Automated review gates run here (orchestrator-owned).', 'auto-review', '🤖', 'lane-4-auto-review'),
  lane(TaskState.Escalated, 'Escalated', 'Escalated',
    'Escalated for operator attention.', 'escalated', '⚠️', 'lane-5e-escalated'),
  lane(TaskState.HumanReview, 'Human review', 'Human review',
    'Waiting for a human decision.', 'human-review', '👁️', 'lane-5-human-review'),
  lane(TaskState.Completed, 'Delivered', 'Delivered',
    'Delivered and accepted.', 'completed', '🟢', 'lane-6-completed'),
  lane(TaskState.Archive, 'Archive', 'Archive',
    'Archived; out of the active workflow.', 'archive', '🗄️', 'lane-7-archive'),
];

/**
 * Virtual and legacy lane keys that reach the UI from older payloads or from
 * board-side splits. They resolve to a presentation so no surface has to carry
 * its own compatibility branch.
 *
 * - `2-ready-intake` — the board splits `2-ready` into a human-ready lane and
 *   an orchestrator-intake sub-lane; both share the `2-ready` guide.
 * - `4-review` — the pre-ADR-0025 key for what is now `4-auto-review`.
 * - `1b-needs-human-review` — retired lane, folded into human review.
 */
const VIRTUAL: readonly LanePresentation[] = [
  lane('2-ready-intake', 'Preparation', 'Preparation',
    'Orchestrator intake is preparing this task.', 'ready-intake', '🛂', 'lane-2-ready'),
  lane('4-review', 'Post Processing', 'Post Processing',
    'Automated review gates run here (orchestrator-owned).', 'auto-review', '🤖', 'lane-4-auto-review'),
  lane('1b-needs-human-review', 'Human review', 'Human review',
    'Waiting for a human decision.', 'human-review', '👁️', 'lane-5-human-review'),
];

/** Every known lane key mapped to its presentation. */
export const LANE_PRESENTATION: Readonly<Record<string, LanePresentation>> =
  Object.freeze(Object.fromEntries([...CANONICAL, ...VIRTUAL].map((l) => [l.state, l])));

/** Canonical lanes in board order, ready to iterate. */
export const LANES_IN_BOARD_ORDER: readonly LanePresentation[] =
  LANE_PRESENTATION_ORDER.map((state) => LANE_PRESENTATION[state]);

const LANE_KEY_PREFIX = /^\d+[a-z]?-/;

/**
 * Fallback for a lane key the frontend does not know (a backend lane added
 * ahead of the UI). The key is humanised rather than shown raw, and the tone
 * falls back to the neutral `--studio-lane-unknown` so nothing renders
 * colourless or claims a lane hue it has no right to.
 */
function unknownLane(state: string): LanePresentation {
  const stripped = state.replace(LANE_KEY_PREFIX, '').replace(/-/g, ' ').trim();
  const name = stripped ? stripped.charAt(0).toUpperCase() + stripped.slice(1) : state;
  return {
    state,
    name,
    shortName: name,
    sentence: '',
    tone: '--studio-lane-unknown',
    glyph: '•',
    docTopic: null,
  };
}

/**
 * Resolve a lane key to its presentation. Never returns null: an unknown or
 * empty key yields a humanised fallback, so callers render one shape.
 */
export function lanePresentation(state: string | null | undefined): LanePresentation {
  if (!state) return unknownLane('');
  return LANE_PRESENTATION[state] ?? unknownLane(state);
}

/** True when the lane key has a curated presentation (not the fallback). */
export function isKnownLane(state: string | null | undefined): boolean {
  return !!state && state in LANE_PRESENTATION;
}

/** Primary display name, e.g. `Human review`. */
export function laneName(state: string | null | undefined): string {
  return lanePresentation(state).name;
}

/** Compact display name for chips, pills, and lane selects. */
export function laneShortName(state: string | null | undefined): string {
  return lanePresentation(state).shortName;
}

/** One-sentence meaning of the lane, terminated with a period. */
export function laneSentence(state: string | null | undefined): string {
  return lanePresentation(state).sentence;
}

/** Lane glyph for board headers, rails, and pickers. */
export function laneGlyph(state: string | null | undefined): string {
  return lanePresentation(state).glyph;
}

/**
 * Name of the CSS custom property holding this lane's tone. Templates should
 * prefer stamping `data-lane="<state>"` and reading `var(--studio-lane-tone)`;
 * this accessor exists for the rare surface that must resolve the token name
 * itself (and for the presentation spec).
 */
export function laneTone(state: string | null | undefined): string {
  return lanePresentation(state).tone;
}

/**
 * Concept-doc topic for the lane-info modal, or `null` when the lane has no
 * guide. Each topic matches a committed file at
 * `docs/app/help/lane-guides/{topic}.md` served by
 * `GET /api/concept-docs/{topic}`.
 */
export function laneDocTopic(state: string | null | undefined): string | null {
  return state ? (LANE_PRESENTATION[state]?.docTopic ?? null) : null;
}
