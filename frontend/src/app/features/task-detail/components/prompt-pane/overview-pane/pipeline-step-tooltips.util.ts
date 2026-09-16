/**
 * Pure tooltip builders for one pipeline step row, split out of
 * `overview-pane.component.ts` in AGT-2819. Every function here is a total
 * function of its arguments, which is what makes the row copy directly
 * testable.
 */
import type { StructuredTooltip, TooltipSeverity } from 'coding-agent-chat/shared';
import type { SteeringInfo } from '../../../../../components/steering-detail';
import type { PipelineRowVm } from './pipeline-row.vm';

/** Title-case label for execution detail that belongs behind a verdict pill. */
export function verdictTitle(verdict: string | null): string | null {
  switch ((verdict ?? '').toLowerCase()) {
    case 'concern':
    case 'concerns':      return 'Concerns';
    case 'blocked':
    case 'block':         return 'Blocking concern';
    // Auto-mode Ralph-loop guard verdicts (pre-loop-guard step).
    case 'looping':       return 'Loop forming';
    case 'loop-detected': return 'Loop detected';
    case 'open-items':    return 'Open items';
    case 'escalated':
    case 'escalate':      return 'Escalation reason';
    case 'selected':      return 'Economy selection';
    case 'override':      return 'Card override';
    case 'fallback':      return 'Default fallback';
    default:              return null;
  }
}

/**
 * Reconcile the CORE step's self-reported verdict against its deterministic
 * status so the row can never show a red "Failed" icon next to a green
 * "SUCCESS" badge (bug ASS-2). The status (icon) is authoritative — it is the
 * classified run status, not a prompt-based self-report — so a non-passed CORE
 * step that still claims a success-class outcome ('success'/'noop') has its
 * verdict dropped. The backend now writes a reconciled record, but this also
 * guards legacy on-disk records persisted before the fix, which are only
 * rewritten when the task re-runs. Every consistent pairing passes through.
 */
export function reconcileCoreVerdict(
  status: PipelineRowVm['status'],
  verdict: string | null,
): string | null {
  if (status === 'passed') return verdict;
  const claim = (verdict ?? '').toLowerCase();
  if (claim === 'success' || claim === 'noop') return null;
  return verdict;
}

/**
 * Build the structured tooltip for detail behind a step verdict. Aspect
 * concerns, loop-guard findings, and reissue open-item/escalation decisions
 * all use the same compact verdict pill and details-dialog concern section.
 * A pass verdict or a step with no recorded detail stays bare.
 */
export function buildConcernTooltip(
  label: string,
  verdict: string | null,
  summary: string | null,
): StructuredTooltip | null {
  const text = summary?.trim();
  if (!text) return null;
  const kind = verdictTitle(verdict);
  if (!kind) return null;
  return { title: `${label} · ${kind}`, body: text };
}

/** Show failures, skips, and the honest coverage scope behind a passed test gate. */
export function buildStepStatusTooltip(
  label: string,
  status: PipelineRowVm['status'],
  detail: string | null,
): StructuredTooltip | null {
  const body = detail?.trim();
  if (!body) return null;
  const passedTestCoverage = status === 'passed' && /(?:^|;\s*)test-level=/i.test(body);
  if (status !== 'failed' && status !== 'skipped' && status !== 'notApplicable' && status !== 'not-run' && !passedTestCoverage) return null;
  const title = status === 'failed'
    ? 'Failed'
    : status === 'skipped'
      ? 'Skipped'
      : status === 'notApplicable'
        ? 'Not applicable'
      : status === 'not-run' ? 'Not run' : 'Passed';
  return { title: `${label}: ${title}`, body };
}

/** Map a steering tone to the tooltip accent colour. */
export function decisionTooltipSeverity(tone: SteeringInfo['tone']): TooltipSeverity {
  switch (tone) {
    case 'ok':     return 'success';
    case 'warn':   return 'warn';
    case 'danger': return 'error';
    default:       return 'info';
  }
}

/**
 * Build the decision badge tooltip: the orchestrator's reasoning headline, the
 * open items behind the ruling, and the run context, composed into the body so
 * the inline badge stays compact and the detail is available on hover / focus.
 */
export function buildDecisionTooltip(info: SteeringInfo): StructuredTooltip {
  const lines: string[] = [];
  if (info.reason) lines.push(info.reason);
  if (info.openItems.length > 0) {
    if (lines.length > 0) lines.push('');
    lines.push('Open items:');
    for (const item of info.openItems) {
      const verdict = item.verdict ? ` [${item.verdict}]` : '';
      const reason = item.reason ? `: ${item.reason}` : '';
      lines.push(`• ${item.aspect}${verdict}${reason}`);
    }
  }
  if (info.context.length > 0) {
    if (lines.length > 0) lines.push('');
    for (const line of info.context) lines.push(`${line.key}: ${line.value}`);
  }
  const body = lines.join('\n').trim();
  return {
    title: `Decision · ${info.verdictLabel}`,
    body: body || info.verdictLabel,
  };
}
