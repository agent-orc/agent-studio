/**
 * Frontend mirror of the canonical review projection (AGT-2717).
 *
 * Source of truth: `backend/Features/Review/Projection/ReviewProjection.cs` and
 * `backend/Features/Review/Projection/ReviewRoundRecord.cs`. Field names here are
 * the camelCase wire names the backend serializes; keep the two in lockstep.
 *
 * This is the ONE review head every surface reads - the escalation banner, the
 * Evidence tab, the Result header and the board card chip. Before it existed
 * each surface picked its own artifact family (`code-review-grade-*.md` locally,
 * `remote-review-grade-*.md` plus `aspect-*.json` remotely), so the banner could
 * report "0 review rounds - Grade not recorded" for a card that carried seven
 * remote review reports (AGT-2689). Reading only this projection removes the
 * choice, and with it the disagreement.
 *
 * The projection answers three questions, in this order, and only from recorded
 * round data: how many rounds and when ({@link ReviewProjection.roundCount},
 * {@link ReviewProjection.latestAt}), what blocked
 * ({@link ReviewProjection.blockingAspects} or {@link ReviewProjection.buildTests}),
 * and what to do ({@link ReviewProjection.recommendation}). "Not recorded",
 * "not proven" and "0" appear only when the records say so, and then with a
 * reason.
 */

/** Which plane produced a single review round. */
export type ReviewPlane = 'local' | 'remote';

/** Which planes contributed the card's review rounds, collapsed over all rounds. */
export type ReviewProjectionPlane = 'none' | 'local' | 'remote' | 'mixed';

/**
 * Normalized verdict vocabulary shared by aspects and build-tests rows. The
 * planes speak different dialects (`pass`/`block` locally, `pass`/`fail`
 * remotely); the backend writers normalize onto these four so no reader has to
 * know which plane produced a row. `missing` means the plane recorded no
 * verdict for that row.
 */
export type ReviewVerdict = 'pass' | 'concerns' | 'block' | 'missing';

/** Collapsed build-tests result across one round or the whole card. */
export type ReviewBuildTestsResult = 'passed' | 'failed' | 'not-proven';

/** Where the delivery produced by a review round ended up. */
export type ReviewDeliveryState = 'integrated' | 'gate-failed' | 'not-attempted';

/** What recorded the human-decision requirement. */
export type ReviewDecisionSource = 'parked-blocker' | 'escalation-event' | 'review-lane-change';

/**
 * The operator's next step, derived from the blocking evidence:
 * `reissue` sends the card back naming the concrete gap, `accept-with-override`
 * means the work is sound and a human overrides the gate that held it, `wait`
 * means a review is still in flight, `none` means nothing is open.
 */
export type ReviewRecommendation = 'reissue' | 'accept-with-override' | 'wait' | 'none';

/**
 * One build or test command a review round ran, with the proof it produced.
 * The single source the Evidence tab reads for build-tests.
 */
export interface ReviewRoundCommandRow {
  /** Plan step that owns the command, e.g. `verify-1`. */
  stepId: string;
  /** Command line as executed. Empty when the plane recorded none. */
  command: string;
  /** Process exit code, or null when the command was signalled or not run. */
  exitCode: number | null;
  /** Normalized verdict for the row. */
  status: ReviewVerdict;
  /** Verdict summary as the reviewer phrased it. */
  summary: string;
}

/** One semantic aspect verdict inside a review round. */
export interface ReviewRoundAspect {
  /** Aspect name, e.g. `documentation-impact`. */
  name: string;
  /** Normalized verdict for the aspect. */
  verdict: ReviewVerdict;
  /**
   * The reviewer's reason, quoted verbatim on every surface. A blocking aspect
   * without a reason is why the banner used to say nothing useful.
   */
  summary: string;
}

/** What the delivery gate decided for one review round. */
export interface ReviewRoundDeliveryGate {
  /** Gate outcome for the round. */
  result: ReviewDeliveryState;
  /** Why the gate reached that result; empty when it needs no reason. */
  reason: string;
  /** Integration branch the gate targeted, when one was resolved. */
  integrationBranch: string | null;
}

/**
 * The canonical record of one review round, written by both review planes.
 *
 * Named "round" rather than "attempt" because two other things already own that
 * word: the fenced remote `ReviewAttempt` in the attempt authority, and the
 * `.metadata/review-attempt.json` epoch marker. This record is one entry in the
 * operator-visible review history, keyed by {@link attemptId} inside its
 * {@link plane}. The rendered Markdown report stays an artifact and is
 * referenced from {@link reportRef}; nothing parses Markdown for state.
 */
export interface ReviewRoundRecord {
  /** Bumped when the on-disk shape changes incompatibly. */
  schemaVersion: number;
  /** Plane that produced the round. */
  plane: ReviewPlane;
  /**
   * Identity of the round inside its plane. Remote uses the fenced
   * ReviewAttempt id; local uses the grade run timestamp.
   */
  attemptId: string;
  /** Commit the round reviewed. Null when the plane recorded none. */
  subjectSha: string | null;
  /** ISO instant the round was recorded, UTC. */
  receivedAt: string;
  /**
   * Plane-native terminal outcome, verbatim: `Pass`, `ProductFailure`,
   * `ReviewInfra`, `Inconclusive` or `Cancellation` for remote; `pass`,
   * `concerns` or `block` for the local grade pass.
   */
  outcome: string;
  /** Quality grade `A`-`D`, or null when the plane assigns none. */
  grade: string | null;
  /** One-line reviewer summary; empty when the plane supplied none. */
  summary: string;
  /**
   * Job-folder-relative name of the rendered report this record was written
   * beside, so a surface can link back to the artifact.
   */
  reportRef: string | null;
  /**
   * Deterministic build and test proof for this round. Empty when the round
   * carried no build-tests verdict at all, which is what `not-proven` means
   * downstream.
   */
  buildTests: ReviewRoundCommandRow[];
  /** Semantic aspect verdicts, in report order. */
  aspects: ReviewRoundAspect[];
  /**
   * What the delivery gate did with this round. Null when the round never
   * reached the gate, which reads as "not attempted".
   */
  deliveryGate: ReviewRoundDeliveryGate | null;
}

/** Collapsed build-tests proof across the card's review rounds. */
export interface ReviewBuildTestsView {
  /** Collapsed result over every round that carried build-tests rows. */
  result: ReviewBuildTestsResult;
  /** One sentence naming the commands, or why nothing is proven. */
  reason: string;
  /** Plan steps the verdict covers, e.g. `verify-1`, `verify-2`. */
  steps: string[];
}

/** Where the reviewed delivery ended up. */
export interface ReviewDeliveryView {
  /** Delivery state collapsed over the card's rounds. */
  state: ReviewDeliveryState;
  /** Why the delivery is in that state; empty when it needs no reason. */
  reason: string;
  /** Branch the delivery targeted, when one was resolved. */
  integrationBranch: string | null;
}

/** Why the card is waiting on a person, and what recorded that. */
export interface ReviewDecisionRequirement {
  /** What recorded the requirement. */
  source: ReviewDecisionSource;
  /** The reason as its source recorded it. */
  reason: string;
}

/**
 * The one review head every surface reads. Served on the task detail and as
 * `GET /api/tasks/{id}/review-projection`.
 */
export interface ReviewProjection {
  /** Job the projection describes. */
  jobId: string;
  /** How many review rounds exist, across both planes. */
  roundCount: number;
  /** Which planes contributed those rounds. */
  plane: ReviewProjectionPlane;
  /** Every round, oldest first, with its full evidence. */
  rounds: ReviewRoundRecord[];
  /** Newest round, or null when none exists. */
  latest: ReviewRoundRecord | null;
  /** Plane-native outcome of {@link latest}; null when no round exists. */
  latestOutcome: string | null;
  /** ISO instant {@link latest} was recorded; null when no round exists. */
  latestAt: string | null;
  /**
   * Newest recorded quality grade. Null only when no round carried one, which
   * is the normal case for a purely remote-reviewed card.
   */
  grade: string | null;
  /**
   * Blocking semantic verdicts on the newest round, each with the reviewer's
   * own reason. Empty when nothing semantic blocks.
   */
  blockingAspects: ReviewRoundAspect[];
  /** Collapsed build and test proof, with the reason behind it. */
  buildTests: ReviewBuildTestsView;
  /** Where the reviewed delivery ended up, with the reason when it did not land. */
  delivery: ReviewDeliveryView;
  /** Why a human is on the hook, or null when nobody is waiting on a decision. */
  decisionRequired: ReviewDecisionRequirement | null;
  /** The operator's next step. */
  recommendation: ReviewRecommendation;
  /**
   * The concrete next step, naming the gap: the aspect and its quoted reason,
   * the failing command, or the gate result to override.
   */
  recommendationReason: string;
}

/**
 * The projection of a card that has no review round yet. Callers use this
 * instead of branching on null, so every surface renders the same honest
 * "no review round recorded" copy rather than inventing a zero grade.
 */
export const EMPTY_REVIEW_PROJECTION: ReviewProjection = {
  jobId: '',
  roundCount: 0,
  plane: 'none',
  rounds: [],
  latest: null,
  latestOutcome: null,
  latestAt: null,
  grade: null,
  blockingAspects: [],
  buildTests: { result: 'not-proven', reason: '', steps: [] },
  delivery: { state: 'not-attempted', reason: '', integrationBranch: null },
  decisionRequired: null,
  recommendation: 'none',
  recommendationReason: '',
};

/**
 * Fresh empty projection for one job. Prefer this over spreading
 * {@link EMPTY_REVIEW_PROJECTION} when the caller knows the job id, and always
 * when the result may be mutated - the shared constant holds shared arrays.
 */
export function emptyReviewProjection(jobId = ''): ReviewProjection {
  return {
    ...EMPTY_REVIEW_PROJECTION,
    jobId,
    rounds: [],
    blockingAspects: [],
    buildTests: { result: 'not-proven', reason: '', steps: [] },
    delivery: { state: 'not-attempted', reason: '', integrationBranch: null },
  };
}
