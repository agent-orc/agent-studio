import type { AspectVerdictTone } from '../../../../../components/aspect-findings';
import type { StructuredTooltip, TooltipSeverity } from 'coding-agent-chat/shared';
import type { ProtocolVerdict } from '../../protocol-pane/protocol-verdict';

export interface DecisionBadgeVm {
  verdict: string;
  label: string;
  tone: AspectVerdictTone;
  severity: TooltipSeverity;
  tooltip: StructuredTooltip;
}

export function outcomeDecisionBadge(outcome: ProtocolVerdict | null): DecisionBadgeVm | null {
  if (!outcome) return null;
  const status = outcome.status;
  const tone: AspectVerdictTone = status === 'failed'
    ? 'danger'
    : status === 'succeeded' ? 'ok' : status === 'needs-decision' ? 'warn' : 'neutral';
  const severity: TooltipSeverity = tone === 'danger' ? 'error' : tone === 'warn' ? 'warn' : tone === 'ok' ? 'success' : 'info';
  // AGT-2715: no rewriting here. The verdict label already comes from the lane
  // presentation, so the badge and the tooltip title read the same word the
  // Result header and the lane chip do. This used to patch "Human review lane"
  // into "Human review" for the badge only, leaving the tooltip saying "Run
  // outcome: Human review lane" — a fourth wording for one lane.
  return {
    verdict: status,
    label: outcome.label,
    tone,
    severity,
    tooltip: { title: `Run outcome: ${outcome.label}`, body: outcome.detail },
  };
}
