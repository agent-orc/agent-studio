/**
 * Central model + projection for one orchestrator *steering step* (ASS-784 /
 * ASS-771 / ASS-776).
 *
 * A steering step is a single completion-loop decision the orchestrator drove:
 * it re-issued the run, escalated to a human, accepted the work, or handed the
 * agent a continuation prompt. The structured trace of that decision — the
 * verdict, the short reason, the verbatim steer prompt the agent received, and
 * the context it was given (open items, prior commits, resume-vs-fresh +
 * session, re-issue counter) — is persisted on the per-task timeline ledger
 * (`logs/timeline.jsonl`) in the event's `details` bag.
 *
 * This module is the single source of truth that turns one
 * {@link TaskTimelineEvent} into a typed {@link SteeringInfo} so both the
 * Timeline tab and the Overview pipeline-steps surface render the same
 * structured block (via {@link SteeringDetailComponent}) instead of a bare
 * verdict token or a raw text blob. The verdict→tone mapping and the context
 * extraction live here so every host reads steering the same way.
 */
import {
  aspectVerdictTone,
  parseAspectFindings,
  resolveAspectFindings,
  type AspectFinding,
  type AspectVerdictTone,
} from '../aspect-findings';

/** The four completion-loop steering verdicts (mirrors the orchestrator). */
export type SteeringVerdict = 'accept' | 'reissue' | 'escalate' | 'continuation';

/** One labelled context row (key/value) shown under the collapsed steer block. */
export interface SteeringContextLine {
  key: string;
  value: string;
}

/** Structured projection of one steering step, host-agnostic. */
export interface SteeringInfo {
  verdict: SteeringVerdict;
  /** Human label for the verdict chip (e.g. "Re-issue"). */
  verdictLabel: string;
  /** Central severity tone for the verdict chip (ASS-737). */
  tone: AspectVerdictTone;
  /** One-line gap / reason headline; null when none was recorded. */
  reason: string | null;
  /**
   * The open items behind the verdict — the per-aspect findings that triggered
   * the re-issue / escalation, resolved from the structured `findings` JSON or
   * by parsing the legacy `**{aspect}** [{verdict}]: {reason}` blob. Empty when
   * the step was not aspect-driven.
   */
  openItems: AspectFinding[];
  /** Verbatim steer prompt the agent received; null when not recorded. */
  prompt: string | null;
  /** Resume-vs-fresh + session, re-issue counter, cause — labelled rows. */
  context: SteeringContextLine[];
  /** Prior commits the orchestrator considered for the steer (capped upstream). */
  commits: string[];
}

/** Map a steering verdict to its central severity tone. */
export function steeringTone(verdict: SteeringVerdict): AspectVerdictTone {
  switch (verdict) {
    case 'accept':       return 'ok';
    case 'reissue':      return 'warn';
    case 'escalate':     return 'danger';
    case 'continuation': return 'neutral';
  }
}

/** Human label for a steering verdict chip. */
export function steeringVerdictLabel(verdict: SteeringVerdict): string {
  switch (verdict) {
    case 'accept':       return 'Accept';
    case 'reissue':      return 'Re-issue';
    case 'escalate':     return 'Escalate';
    case 'continuation': return 'Continuation';
  }
}

/**
 * The timeline event kinds that represent a steering step, mapped to their
 * verdict. Kept as a literal map (rather than importing TIMELINE_KIND) so this
 * shared component has no dependency on the task-timeline feature.
 */
const STEERING_KIND_VERDICT: Record<string, SteeringVerdict> = {
  orchestrator_verdict_accepted: 'accept',
  quality_loop_reopened: 'reissue',
  orchestrator_escalated: 'escalate',
  orchestrator_steered: 'continuation',
};

/** A subset of the timeline event shape this projection needs. */
export interface SteeringEventLike {
  kind: string;
  summary?: string;
  details?: Record<string, string> | null;
}

/** True when the event kind is a steering step we can render structurally. */
export function isSteeringKind(kind: string): boolean {
  return Object.prototype.hasOwnProperty.call(STEERING_KIND_VERDICT, kind);
}

/**
 * Split the persisted `priorCommits` detail into a list. The backend writes it
 * either as a JSON array of strings or as a newline-joined block; both are
 * tolerated so an older ledger row still renders. Returns [] when absent.
 */
function parseCommits(raw: string | null | undefined): string[] {
  if (!raw || !raw.trim()) return [];
  const trimmed = raw.trim();
  if (trimmed.startsWith('[')) {
    try {
      const parsed = JSON.parse(trimmed);
      if (Array.isArray(parsed)) {
        return parsed.map(c => String(c).trim()).filter(c => c.length > 0);
      }
    } catch {
      // fall through to newline parsing
    }
  }
  return trimmed
    .split(/\r?\n/)
    .map(line => line.replace(/^[-*\s]+/, '').trim())
    .filter(line => line.length > 0);
}

/**
 * Build the labelled context rows from the event's details bag. Only the rows
 * that are actually present are emitted, so a fresh-run reissue with no resume
 * session simply omits the session line rather than showing a blank.
 */
function buildContext(details: Record<string, string>): SteeringContextLine[] {
  const lines: SteeringContextLine[] = [];

  const attempt = details['attempt'];
  if (attempt) {
    const max = details['maxAttempts'];
    lines.push({ key: 'Attempt', value: max ? `${attempt} / ${max}` : attempt });
  }

  const priorReissues = details['priorReissues'];
  if (priorReissues) lines.push({ key: 'Prior re-issues', value: priorReissues });

  const cause = details['cause'];
  if (cause) lines.push({ key: 'Cause', value: cause });

  // Resume-vs-fresh: prefer an explicit `mode`, else infer from the session id.
  // A reissue with an attempt counter but no session was a fresh-run re-spawn.
  const session = details['resumeSessionId'];
  const mode = details['mode'] ?? (session ? 'resume' : (attempt ? 'fresh-run' : null));
  if (mode) lines.push({ key: 'Mode', value: mode });
  if (session) lines.push({ key: 'Session', value: session });

  return lines;
}

/**
 * Build the short single-line headline shown next to the verdict chip when the
 * step is aspect-driven. The per-aspect detail lives in the formatted OPEN
 * ITEMS section, so this stays a terse summary — `multi-aspect-block: N aspects
 * flagged` when any aspect blocks, else `N aspects flagged` — and never the raw
 * `**{aspect}** [{verdict}]: …` blob (which would duplicate the open items as a
 * raw markdown dump).
 */
function openItemsHeadline(items: AspectFinding[]): string {
  const n = items.length;
  const noun = n === 1 ? 'aspect' : 'aspects';
  const blocking = items.some(i => aspectVerdictTone(i.verdict) === 'danger');
  return blocking
    ? `multi-aspect-block: ${n} ${noun} flagged`
    : `${n} ${noun} flagged`;
}

/**
 * Project one timeline event into a {@link SteeringInfo}, or null when the
 * event is not a steering step. When the step is aspect-driven the headline is
 * a terse summary (the per-aspect detail lives in the formatted OPEN ITEMS
 * section); otherwise the reason prefers the structured `gap` (reopen) /
 * `reason` (escalate) detail and falls back to the event summary. The open
 * items come from the structured `findings` JSON with a parse-fallback to the
 * same blob.
 */
export function steeringInfoFromEvent(event: SteeringEventLike): SteeringInfo | null {
  const verdict = STEERING_KIND_VERDICT[event.kind];
  if (verdict == null) return null;

  const details = event.details ?? {};
  const blob = details['gap'] ?? details['reason'] ?? null;
  const openItems = resolveAspectFindings(details['findings'], blob);
  // When the blob is itself the per-aspect findings dump, the formatted OPEN
  // ITEMS section already carries it; collapse the headline to a terse summary
  // so the step never shows the raw `**`/`[]` blob twice (ASS-776 contract).
  const blobIsAspectDump = parseAspectFindings(blob).length > 0;
  const reason = blobIsAspectDump
    ? openItemsHeadline(openItems)
    : (blob ?? event.summary ?? '').trim() || null;
  const prompt = (details['followUpPrompt'] ?? '').trim() || null;

  return {
    verdict,
    verdictLabel: steeringVerdictLabel(verdict),
    tone: steeringTone(verdict),
    reason,
    openItems,
    prompt,
    context: buildContext(details),
    commits: parseCommits(details['priorCommits']),
  };
}
