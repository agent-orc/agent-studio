import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type {
  PipelineExecutionRecord,
  TaskPipelineResponse,
} from '../../../../../task-pipeline/models/task-pipeline.model';
import { formatClock, formatStepDuration } from '../overview-pane-formatters';
import { summarizeDecisionCost } from '../../../../../task-pipeline';
import { buildDecisionTransitions, type DecisionTransitionVm } from './decision-chain.util';

/**
 * The decisions between the pipeline steps (AGT-3015). Lives inside the
 * Overview pipeline block under the step table: one line per decision
 * execution, naming the step that ended, the verdict, the deciding model,
 * duration, cost and evidence, plus the card's cost of deciding against its
 * agent runs.
 */
@Component({
  selector: 'app-pipeline-decision-chain',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-decision-chain.component.html',
  styleUrl: './pipeline-decision-chain.component.scss',
})
export class PipelineDecisionChainComponent {
  /** The run selected in the Run-Switcher. */
  readonly execution = input<PipelineExecutionRecord | null>(null);
  /** The pipeline read response: step labels and the card-level rollup. */
  readonly response = input<TaskPipelineResponse | null>(null);
  /** The card rollup covers every attempt, so it shows only on the current run. */
  readonly current = input(true);
  readonly documentRequested = output<string>();

  private readonly decisionCost = computed(() => this.current() ? this.response()?.decisionCost ?? null : null);

  private readonly labels = computed(() => {
    const pipeline = this.response()?.pipeline;
    const steps = pipeline?.allSteps ?? [...(pipeline?.pre ?? []), ...(pipeline?.core ?? []), ...(pipeline?.post ?? [])];
    return new Map(steps.map(step => [step.id.toLowerCase(), step.displayName || step.id]));
  });

  readonly transitions = computed<DecisionTransitionVm[]>(() => {
    const labels = this.labels();
    return buildDecisionTransitions(this.execution(), id => labels.get(id.toLowerCase()) ?? id);
  });

  readonly summary = computed(() => summarizeDecisionCost(this.decisionCost()));

  readonly formatClock = formatClock;
  readonly formatStepDuration = formatStepDuration;

  modelTooltip(item: DecisionTransitionVm): string {
    if (!item.model) return 'Decided by rule or by a person; no model was called.';
    const level = item.thinkingLevel ? ` at ${item.thinkingLevel}` : '';
    const source = item.modelSource ? `, chosen by ${item.modelSource}` : '';
    return `${item.model}${level}${source}. ${item.tokens.toLocaleString('en-US')} tokens.`;
  }

  costTooltip(item: DecisionTransitionVm): string {
    switch (item.costTone) {
      case 'unpriced':
        return `${item.model ?? 'This model'} has no catalogue price; the cost is unknown, not zero.`;
      case 'zero':
        return 'Measured zero: this decision ran without a model call.';
      case 'unmeasured':
        return 'No model was recorded for this execution, so its cost is not measured.';
      default:
        return 'Estimated from historical list prices.';
    }
  }
}
