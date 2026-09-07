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
  // No label rewriting here: the verdict already carries the shared lane
  // wording. This used to patch 'Human review lane' into 'Human review',
  // which is how one lane ended up with two names in two panes (AGT-2715).
  return {
    verdict: status,
    label: outcome.label,
    tone,
    severity,
    tooltip: { title: `Run outcome: ${outcome.label}`, body: outcome.detail },
  };
}
