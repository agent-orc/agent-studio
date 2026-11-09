import type { EffectiveModelSelection } from '../../../../quota';
import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

/** What a step row shows when the launch route differs from the configured one. */
export interface EffectiveRouteView {
  rule: string;
  ruleLabel: string;
  tone: StatusTone;
  lead: string;
  route: string;
  configured: string;
  reason: string;
  reasonRecorded: boolean;
  detail: string;
}

const RULE_LABELS: Readonly<Record<EffectiveModelSelection['rule'], string>> = {
  'consumer-default': 'Consumer policy',
  'operator-switch': 'Operator switch',
  'quota-cap': 'Quota cap',
  pin: 'Step pin',
};

/** A quota fallback or a wait is the current, acute state; a policy route is informational. */
const RULE_TONES: Readonly<Record<EffectiveModelSelection['rule'], StatusTone>> = {
  'consumer-default': 'info',
  'operator-switch': 'info',
  'quota-cap': 'warning',
  pin: 'neutral',
};

function same(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').trim().toLowerCase() === (b ?? '').trim().toLowerCase();
}

function routeText(cli: string, model: string | null | undefined): string {
  return `${cli} · ${model?.trim() || 'default model'}`;
}

/**
 * Compares the CLI and model a step row shows as configured with the route
 * `GET /api/model-selection/effective` admits at launch. Returns null when they
 * agree or no route is known for the step. A blank configured value falls back
 * to the configured route the server resolved.
 */
export function effectiveRouteView(
  route: EffectiveModelSelection | null | undefined,
  configuredCli: string,
  configuredModel: string,
): EffectiveRouteView | null {
  if (!route) return null;
  const cli = configuredCli.trim() || route.configured.cliType;
  const model = configuredModel.trim() || route.configured.model || '';
  const effective = route.effective;
  if (effective && same(effective.cliType, cli) && same(effective.model, model)) return null;
  const reason = route.reason?.trim() ?? '';
  const ruleLabel = RULE_LABELS[route.rule] ?? route.rule;
  const lead = effective ? 'Runs' : 'Waits';
  const text = effective ? routeText(effective.cliType, effective.model) : 'no route admitted';
  const configured = routeText(cli, model);
  const thinking = effective?.thinkingLevel ? `, thinking ${effective.thinkingLevel}` : '';
  return {
    rule: route.rule,
    ruleLabel,
    tone: effective ? RULE_TONES[route.rule] ?? 'neutral' : 'warning',
    lead,
    route: text,
    configured,
    reason: reason || 'reason not recorded',
    reasonRecorded: reason.length > 0,
    detail: `Configured ${configured}. ${lead} ${text}${thinking}. Rule: ${ruleLabel}. Reason: ${reason || 'not recorded'}.`,
  };
}
