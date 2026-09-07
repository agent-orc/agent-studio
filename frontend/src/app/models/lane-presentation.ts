import { TaskState } from './task.model';

/**
 * Lane presentation — the single source for how a lane is *shown*.
 *
 * AGT-2715. The operator counted four wordings and two tones for one lane
 * (`5-human-review`): the header chip said "Review", the Result tab header
 * said "Human review lane", a badge normaliser rewrote that to "Human
 * review", and the project workflow section said "Awaiting human review."
 * Five independent label maps and half a dozen ad-hoc colour rules each
 * defined their own string, so every new surface re-invented the lane.
 *
 * Everything a surface needs to *name* or *tint* a lane lives here:
 *
 *  - {@link LanePresentation.name}     the one display name, e.g. "Human review";
 *  - {@link LanePresentation.sentence} the prose form for role/help copy;
 *  - {@link LanePresentation.tone}     the tone key feeding `--studio-lane-*`;
 *  - {@link LanePresentation.glyph}    the lane emoji;
 *  - {@link LanePresentation.docTopic} the lane-guide concept doc.
 *
 * Rules for anyone touching lane UI:
 *
 *  1. No component, template, or utility keeps its own lane string. Import
 *     {@link laneName} / {@link laneSentence} / {@link laneGlyph} instead.
 *     `npm run lint:structure` fails the build on a hard-coded lane name
 *     outside this module (`scripts/check-lane-strings.mjs`).
 *  2. A lane has exactly ONE tone. Bind `[attr.data-lane-tone]="laneTone(state)"`
 *     and `@include lane-tone.vars;` rather than hand-rolling a per-surface
 *     colour rule. The tokens live in `styles/_tokens-semantic.scss`.
 *  3. There is deliberately no second "short name" field. A short name is
 *     exactly what caused the reported defect — "Review" on the chip next to
 *     "Human review lane" on the Result header. One lane, one word.
 *
 * Casing note: the names below keep the wording each lane already shipped
 * with ("In Progress", "Post Processing"), because those strings are asserted
 * by e2e specs and printed in the committed lane-guide docs. Only
 * `5-human-review` changes, which is the defect the operator reported.
 * Normalising the whole set to sentence case is a separate, deliberate pass.
 */
export interface LanePresentation {
  /** Canonical lane key this presentation describes. */
  readonly state: string;
  /** The one display name. Used by every surface that names the lane. */
  readonly name: string;
  /** Prose form for role descriptions, help copy, and verdict details. */
  readonly sentence: string;
  /** Tone key; resolves to the `--studio-lane-<tone>-*` token triplet. */
  readonly tone: LaneTone;
  /** Lane emoji, shared by board headers and settings lists. */
  readonly glyph: string;
  /** Concept-doc topic behind the lane-info button, or null when none exists. */
  readonly docTopic: string | null;
}

/**
 * Tone keys. One per lane, plus `unknown` for a state the frontend has no
 * catalogue entry for. Mirrored one-for-one by the `--studio-lane-<tone>-fg`
 * / `-bg` / `-border` tokens and by `$lane-tones` in `styles/_lane-tone.scss`.
 */
export type LaneTone =
  | 'backlog'
  | 'preparation'
  | 'orchestrator-prep'
  | 'ready'
  | 'progress'
  | 'failed-pickup'
  | 'code-not-complete'
  | 'auto-review'
  | 'human-review'
  | 'escalated'
  | 'completed'
  | 'archive'
  | 'unknown';

function lane(
  state: string,
  name: string,
  sentence: string,
  tone: LaneTone,
  glyph: string,
  docTopic: string | null,
): LanePresentation {
  return { state, name, sentence, tone, glyph, docTopic };
}

/**
 * The catalogue, in board order. `LANE_PRESENTATIONS` is the ordering source
 * for every lane list (board columns, settings sort rows, pickers); consumers
 * filter it rather than re-listing lanes.
 */
export const LANE_PRESENTATIONS: readonly LanePresentation[] = [
  lane(TaskState.Backlog, 'Backlog',
    'Captured but not yet scheduled.',
    'backlog', '🗒️', 'lane-0-backlog'),
  lane(TaskState.Preparation, 'Preparation',
    'Intake and preparation before the task is workable.',
    'preparation', '📋', 'lane-1-preparation'),
  lane(TaskState.OrchestratorPrep, 'Orchestrator Prep',
    'The orchestrator is preparing the task before pickup.',
    'orchestrator-prep', '🛂', 'lane-1a-orchestrator-prep'),
  lane(TaskState.Ready, 'Ready',
    'Queued and ready for pickup.',
    'ready', '📦', 'lane-2-ready'),
  lane(TaskState.Progress, 'In Progress',
    'A run is executing the task (runner-owned).',
    'progress', '🔵', 'lane-3-progress'),
  lane(TaskState.FailedPickup, 'Failed pickup',
    'Pickup failed; the runner could not start a run.',
    'failed-pickup', '🚑', null),
  lane(TaskState.CodeNotComplete, 'Code not complete',
    'The run stopped before it reached review.',
    'code-not-complete', '🚧', null),
  lane(TaskState.AutoReview, 'Post Processing',
    'Automated review gates run here (orchestrator-owned).',
    'auto-review', '🤖', 'lane-4-auto-review'),
  lane(TaskState.Escalated, 'Escalated',
    'Escalated for operator attention.',
    'escalated', '⚠️', 'lane-5e-escalated'),
  lane(TaskState.HumanReview, 'Human review',
    'Waiting for a human decision.',
    'human-review', '👁️', 'lane-5-human-review'),
  lane(TaskState.Completed, 'Delivered',
    'Delivered and accepted.',
    'completed', '🟢', 'lane-6-completed'),
  lane(TaskState.Archive, 'Archive',
    'Archived; out of the active workflow.',
    'archive', '🗄️', 'lane-7-archive'),
];

/**
 * Non-canonical keys that must still present as a lane: the board's virtual
 * Ready sub-lane and two legacy backend states that older job folders and
 * payloads still carry. They borrow their parent lane's word and tone so a
 * historical card never introduces a fifth wording.
 */
const LANE_ALIASES: readonly LanePresentation[] = [
  lane('2-ready-intake', 'Preparation',
    'Orchestrator intake is preparing this card for pickup.',
    'orchestrator-prep', '🛂', 'lane-2-ready'),
  lane('4-review', 'Post Processing',
    'Automated review gates run here (orchestrator-owned).',
    'auto-review', '🤖', 'lane-4-auto-review'),
  lane('1b-needs-human-review', 'Human review',
    'Waiting for a human decision.',
    'human-review', '👁️', 'lane-5-human-review'),
];

const BY_STATE: ReadonlyMap<string, LanePresentation> = new Map(
  [...LANE_PRESENTATIONS, ...LANE_ALIASES].map((p) => [p.state, p]),
);

/**
 * Presentation for an unknown lane key. The name degrades to the readable
 * part of the slug (`9-something-new` -> "Something new") so a lane added
 * backend-first still renders as words rather than a raw key, and the tone
 * falls back to the neutral `unknown` token.
 */
function unknownLane(state: string): LanePresentation {
  const slug = state.replace(/^\d+[a-z]?-/, '').replace(/-/g, ' ').trim();
  const name = slug ? slug.charAt(0).toUpperCase() + slug.slice(1) : state;
  return lane(state, name, `The task is in ${name.toLowerCase()}.`, 'unknown', '•', null);
}

/**
 * Resolve any lane key — canonical, virtual, legacy, or unknown — to its
 * presentation. Never returns null: an unrecognised key degrades to
 * {@link unknownLane} so a surface can always render something readable.
 */
export function lanePresentation(state: string | null | undefined): LanePresentation {
  if (!state) return unknownLane('');
  return BY_STATE.get(state) ?? unknownLane(state);
}

/** The lane's one display name, e.g. "Human review". */
export function laneName(state: string | null | undefined): string {
  return lanePresentation(state).name;
}

/** The lane's prose form, e.g. "Waiting for a human decision." */
export function laneSentence(state: string | null | undefined): string {
  return lanePresentation(state).sentence;
}

/** The lane's tone key; bind it to `data-lane-tone`. */
export function laneTone(state: string | null | undefined): LaneTone {
  return lanePresentation(state).tone;
}

/** The lane's emoji. */
export function laneGlyph(state: string | null | undefined): string {
  return lanePresentation(state).glyph;
}

/** Resolve a lane to its concept-doc topic, or `null` when none exists. */
export function laneDocTopic(state: string | null | undefined): string | null {
  return lanePresentation(state).docTopic;
}

/**
 * Every lane name the app can render, deduplicated. Consumed by
 * `scripts/check-lane-strings.mjs` (via source parsing) and by the
 * presentation spec; keep it exported so both stay honest.
 */
export const ALL_LANE_NAMES: readonly string[] = [
  ...new Set([...LANE_PRESENTATIONS, ...LANE_ALIASES].map((p) => p.name)),
];
