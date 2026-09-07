/**
 * The single place a {@link ReviewProjection} becomes operator copy (AGT-2717).
 *
 * Four surfaces describe the same review history: the escalation banner, the
 * Evidence tab, the Result header and the board card chip. Each of them used to
 * phrase it from whatever artifact family it happened to read, so AGT-2689 - a
 * card with seven remote review rounds, a passing build and a failed delivery
 * gate - was announced as "0 review rounds - Grade not recorded". Every surface
 * now renders strings built here, from the one canonical projection, so they
 * cannot disagree.
 *
 * The rules the copy obeys:
 *
 *   1. NEVER INVENT A ZERO. "No review round recorded" is said only when
 *      `roundCount === 0`; a card with rounds always names how many, and from
 *      which plane.
 *   2. ALWAYS NAME THE REASON. A blocking aspect is quoted with the reviewer's
 *      own summary, a failed build with the reason behind the verdict, a held
 *      delivery with the branch it did not reach.
 *   3. NEVER PARSE, ONLY PROJECT. Nothing here reads Markdown, dates from
 *      filenames, or plane-specific tokens; every string comes from a typed
 *      field of the projection.
 *
 * Pure module: no Angular imports, no side effects, no date library - so the
 * phrasing rules are unit-tested in isolation from any host component,
 * mirroring the sibling `escalation-summary.util`.
 */
import type {
  ReviewProjection,
  ReviewRoundAspect,
} from '../../../../models/review-projection.model';

/** Operator copy for one review projection, one string per surface need. */
export interface ReviewHeadline {
  /** "7 review rounds (remote)" - or "No review round recorded" at zero. */
  rounds: string;
  /** "latest 31.08. 17:11 ProductFailure" - null when no round exists. */
  latest: string | null;
  /** "blocked by documentation-impact: <reason>" / "build failed: ..." / null. */
  blocked: string | null;
  /** "build and tests pass (verify-1, verify-2)" - always present, reason-backed. */
  buildTests: string;
  /** "delivery gate failed, not in develop" / "in develop" / "no delivery yet". */
  delivery: string;
  /** The recommendation sentence, verbatim from `projection.recommendationReason`. */
  action: string;
  /** All of the above joined with ' · ' - the banner one-liner. */
  label: string;
  /** Short chip text for the board card, e.g. "7 rounds · documentation-impact". */
  chip: string;
  /** Pending-reason suffix for the develop/main chips, or null when integrated. */
  pendingReason: string | null;
}

/** Separator between the headline pieces, shared by the banner and the chip. */
const SEPARATOR = ' · ';

/** Branch named when the projection resolved none; the repo default target. */
const DEFAULT_INTEGRATION_BRANCH = 'develop';

/** Upper bound on the board chip, which sits inside a narrow card column. */
const CHIP_MAX_LENGTH = 40;

/**
 * Turn a projection into the copy every review surface renders.
 *
 * Every field is derived; nothing is optional-by-omission, so a caller can bind
 * a field without a null check except where a null carries meaning
 * ({@link ReviewHeadline.latest}, {@link ReviewHeadline.blocked},
 * {@link ReviewHeadline.pendingReason}).
 */
export function buildReviewHeadline(projection: ReviewProjection): ReviewHeadline {
  const rounds = roundsPhrase(projection);
  const latest = latestPhrase(projection);
  const blocked = blockedPhrase(projection);
  const buildTests = buildTestsPhrase(projection);
  const delivery = deliveryPhrase(projection);
  const action = projection.recommendationReason.trim();
  const label = [rounds, latest, blocked, buildTests, delivery, action]
    .filter((part): part is string => !!part)
    .join(SEPARATOR);
  return {
    rounds,
    latest,
    blocked,
    buildTests,
    delivery,
    action,
    label,
    chip: chipPhrase(projection),
    pendingReason: pendingReasonPhrase(projection),
  };
}

/**
 * "7 review rounds (remote)". At zero this says "No review round recorded"
 * rather than "0 review rounds": the count is a fact about the records, and
 * spelling it as a zero is what made the old banner read like a verdict.
 */
function roundsPhrase(projection: ReviewProjection): string {
  const count = projection.roundCount;
  if (count <= 0) return 'No review round recorded';
  const noun = count === 1 ? '1 review round' : `${count} review rounds`;
  return projection.plane === 'none' ? noun : `${noun} (${projection.plane})`;
}

/**
 * "latest 31.08. 17:11 ProductFailure". Null when the card has no round at all.
 * The outcome is the plane-native token, verbatim - the operator recognises
 * `ProductFailure` from the remote report, and translating it would only hide
 * which plane spoke.
 */
function latestPhrase(projection: ReviewProjection): string | null {
  const outcome = projection.latestOutcome?.trim() || '';
  const stamp = formatStamp(projection.latestAt);
  if (!outcome && !stamp) return null;
  return `latest ${[stamp, outcome].filter(Boolean).join(' ')}`;
}

/**
 * The one thing that holds the card, or null when nothing does.
 *
 * A failed build wins over a semantic aspect: it is the cheaper, more certain
 * gap, and it is what the operator has to fix before any aspect verdict can be
 * trusted again.
 */
function blockedPhrase(projection: ReviewProjection): string | null {
  if (projection.buildTests.result === 'failed') {
    const reason = projection.buildTests.reason.trim();
    return reason ? `build failed: ${reason}` : 'build failed (no reason recorded)';
  }
  const aspect = firstBlockingAspect(projection);
  if (!aspect) return null;
  const summary = aspect.summary.trim();
  return summary
    ? `blocked by ${aspect.name}: ${summary}`
    : `blocked by ${aspect.name} (no reason recorded)`;
}

/**
 * "build and tests pass (verify-1, verify-2)". Always present: a card that
 * proved nothing says so, with the reason the projection recorded, so the
 * operator can tell "no proof" apart from "proof failed".
 */
function buildTestsPhrase(projection: ReviewProjection): string {
  const { result, reason, steps } = projection.buildTests;
  switch (result) {
    case 'passed': {
      const named = steps.filter((step) => !!step.trim());
      return named.length > 0
        ? `build and tests pass (${named.join(', ')})`
        : 'build and tests pass';
    }
    case 'failed':
      return 'build and tests failed';
    default: {
      const why = reason.trim();
      return why
        ? `build and tests not proven: ${why}`
        : 'build and tests not proven: no build or test command recorded';
    }
  }
}

/** Where the reviewed work ended up, named with the branch it targeted. */
function deliveryPhrase(projection: ReviewProjection): string {
  const branch = integrationBranchOf(projection);
  switch (projection.delivery.state) {
    case 'integrated':
      return `in ${branch}`;
    case 'gate-failed':
      return `delivery gate failed, not in ${branch}`;
    default:
      return 'no delivery recorded';
  }
}

/**
 * Suffix for the develop/main delivery chips, which already say WHERE the work
 * is; this says why it is not there yet. Null when it is integrated, so the
 * chip stays a plain "in develop".
 */
function pendingReasonPhrase(projection: ReviewProjection): string | null {
  switch (projection.delivery.state) {
    case 'integrated':
      return null;
    case 'gate-failed': {
      const aspect = firstBlockingAspect(projection);
      return aspect ? `delivery gate failed (${aspect.name})` : 'delivery gate failed';
    }
    default:
      return 'no delivery recorded';
  }
}

/**
 * Board card chip: the round count plus the one word that says what is wrong,
 * capped so it cannot push the card layout around. A blocking aspect names
 * itself, a failed build says so, and an otherwise clean card falls back to the
 * latest plane-native outcome.
 */
function chipPhrase(projection: ReviewProjection): string {
  const count = projection.roundCount;
  const head = count <= 0 ? 'no review' : count === 1 ? '1 round' : `${count} rounds`;
  const tail = chipTail(projection);
  return capLength(tail ? `${head}${SEPARATOR}${tail}` : head, CHIP_MAX_LENGTH);
}

/** The chip's second segment: the blocker, else the failing build, else the outcome. */
function chipTail(projection: ReviewProjection): string | null {
  const aspect = firstBlockingAspect(projection);
  if (aspect) return aspect.name;
  if (projection.buildTests.result === 'failed') return 'build failed';
  return projection.latestOutcome?.trim() || null;
}

/** The blocking aspect the copy quotes: the first one the projection listed. */
function firstBlockingAspect(projection: ReviewProjection): ReviewRoundAspect | null {
  return projection.blockingAspects.find((aspect) => !!aspect.name.trim()) ?? null;
}

/** Branch the delivery targeted, falling back to the repo default. */
function integrationBranchOf(projection: ReviewProjection): string {
  return projection.delivery.integrationBranch?.trim() || DEFAULT_INTEGRATION_BRANCH;
}

/**
 * `DD.MM. HH:mm` in the operator's own timezone, zero-padded, 24h. Deliberately
 * hand-rolled: the banner needs one stable shape, not a locale-dependent one,
 * and a date library would be the only dependency in this module. Returns an
 * empty string for a missing or unparseable instant so callers can simply drop
 * the segment.
 */
export function formatStamp(iso: string | null | undefined): string {
  if (!iso) return '';
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return '';
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${pad(at.getDate())}.${pad(at.getMonth() + 1)}. ${pad(at.getHours())}:${pad(at.getMinutes())}`;
}

/** Trim to a hard length, marking the cut so nothing reads as a complete word. */
function capLength(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max - 1).trimEnd()}…`;
}
