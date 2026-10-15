import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { DisclosureMarkerComponent } from '../../../../../../components/disclosure-marker/disclosure-marker.component';
import { DecisionAskStateService } from '../../../../services/decision-ask-state.service';
import type {
  PipelineExecutionRecord,
  TaskPipelineResponse,
} from '../../../../../task-pipeline/models/task-pipeline.model';
import { formatClock, formatStepDuration } from '../overview-pane-formatters';
import { summarizeDecisionCost } from '../../../../../task-pipeline';
import { buildDecisionTransitions, type DecisionTransitionVm } from './decision-chain.util';

interface ReceiptSummary {
  receiptId: string; occurrenceId: string; taskId: string | null; projectId: string | null;
  profile: string; chosenAction: string; callStatus: string; model: string | null;
  thinkingLevel: string | null; totalTokens: number; inputTokens: number; outputTokens: number;
  estimatedUsd: number | null; unknownPriceCalls: number; priceStatus: string;
  evidenceRef: string; endedAt: string; actor: string;
}
interface ReceiptDetail {
  reasoning: string | null; responseContent: string | null;
  grounds: { facts: { id: string; name: string; value: string; source: { ref: string; kind: string } }[] };
}
interface SpendSummary {
  occurrences: number; inputTokens: number; outputTokens: number;
  pricedUsd: number; unknownPriceCalls: number;
}
interface OperatorIntent {
  action: 'raise' | 'cut' | 'stop'; reason: string; requestedEmergencyHours: number | null;
}

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
  imports: [TooltipDirective, DisclosureMarkerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-decision-chain.component.html',
  styleUrl: './pipeline-decision-chain.component.scss',
})
export class PipelineDecisionChainComponent {
  private readonly http = inject(HttpClient);
  private readonly askState = inject(DecisionAskStateService);
  readonly taskId = input<string | null>(null);
  readonly projectId = input<string | null>(null);
  readonly askActive = input(false);
  readonly receipts = signal<ReceiptSummary[]>([]);
  readonly cardSpend = signal<SpendSummary | null>(null);
  readonly projectSpend = signal<SpendSummary | null>(null);
  readonly openReceipt = signal<string | null>(null);
  readonly receiptDetail = signal<ReceiptDetail | null>(null);
  readonly receiptError = signal<string | null>(null);
  readonly operatorIntent = signal<OperatorIntent | null>(null);
  readonly intentReason = signal('');
  readonly emergencyHours = signal(24);
  readonly intentBusy = signal(false);
  readonly intentError = signal<string | null>(null);
  private loadSequence = 0;
  /** The run selected in the Run-Switcher. */
  readonly execution = input<PipelineExecutionRecord | null>(null);
  /** The pipeline read response: step labels and the card-level rollup. */
  readonly response = input<TaskPipelineResponse | null>(null);
  /** The card rollup covers every attempt, so it shows only on the current run. */
  readonly current = input(true);
  readonly documentRequested = output<string>();

  constructor() {
    effect(() => {
      const taskId = this.taskId();
      const projectId = this.projectId();
      const sequence = ++this.loadSequence;
      this.receipts.set([]);
      this.cardSpend.set(null);
      this.projectSpend.set(null);
      if (!taskId) return;
      void this.loadReceipts(taskId, projectId, sequence);
    });
  }

  private async loadReceipts(taskId: string, projectId: string | null, sequence: number): Promise<void> {
    const cardQuery = encodeURIComponent(taskId);
    const projectQuery = projectId ? encodeURIComponent(projectId) : null;
    try {
      const [receipts, card, project] = await Promise.all([
        firstValueFrom(this.http.get<ReceiptSummary[]>(`/api/orchestration/decision-receipts?taskId=${cardQuery}&limit=100`)),
        firstValueFrom(this.http.get<SpendSummary>(`/api/orchestration/decision-receipts/spend?taskId=${cardQuery}`)),
        projectQuery ? firstValueFrom(this.http.get<SpendSummary>(
          `/api/orchestration/decision-receipts/spend?projectId=${projectQuery}`)) : Promise.resolve(null),
      ]);
      if (sequence !== this.loadSequence) return;
      this.receipts.set([...receipts].reverse());
      this.cardSpend.set(card);
      this.projectSpend.set(project);
    } catch {
      // The pipeline history remains available when an older server has no receipt endpoint.
    }
  }

  async showReceipt(item: ReceiptSummary): Promise<void> {
    if (this.openReceipt() === item.receiptId) { this.openReceipt.set(null); return; }
    this.openReceipt.set(item.receiptId);
    this.receiptDetail.set(null);
    this.receiptError.set(null);
    this.operatorIntent.set(null);
    this.intentError.set(null);
    try {
      const [detail, intent] = await Promise.all([
        firstValueFrom(this.http.get<ReceiptDetail>(item.evidenceRef)),
        firstValueFrom(this.http.get<OperatorIntent>(`${item.evidenceRef}/operator-intent`))
          .catch(() => null),
      ]);
      if (this.openReceipt() === item.receiptId) this.receiptDetail.set(detail);
      if (this.openReceipt() === item.receiptId) this.operatorIntent.set(intent);
      if (intent && item.taskId) this.askState.record(item.taskId, intent.action);
    } catch {
      if (this.openReceipt() === item.receiptId) this.receiptError.set('Could not load the decision receipt.');
    }
  }

  async submitIntent(item: ReceiptSummary, action: OperatorIntent['action']): Promise<void> {
    const reason = this.intentReason().trim();
    if (!reason || this.intentBusy()) return;
    this.intentBusy.set(true);
    this.intentError.set(null);
    try {
      const intent = await firstValueFrom(this.http.post<OperatorIntent>(
        `${item.evidenceRef}/operator-intent`, {
          action, reason, requestedEmergencyHours: action === 'raise' ? this.emergencyHours() : null,
        }));
      if (this.openReceipt() === item.receiptId) this.operatorIntent.set(intent);
      if (item.taskId) this.askState.record(item.taskId, intent.action);
    } catch {
      this.intentError.set('Could not record the operator choice. Check the reason and limit.');
    } finally {
      this.intentBusy.set(false);
    }
  }

  receiptCost(item: ReceiptSummary): string {
    const priced = item.estimatedUsd == null ? 'No priced subtotal'
      : item.estimatedUsd === 0 && item.totalTokens > 0 ? '<$0.0001 priced'
      : `$${item.estimatedUsd.toFixed(4)} priced`;
    return item.unknownPriceCalls ? `${priced} + ${item.unknownPriceCalls} unknown price` : priced;
  }

  spendText(spend: SpendSummary): string {
    const tokens = spend.inputTokens + spend.outputTokens;
    const priced = tokens === 0 ? 'no usage data'
      : spend.pricedUsd === 0 && spend.unknownPriceCalls > 0 ? 'no priced subtotal'
      : spend.pricedUsd === 0 ? '<$0.0001 priced'
      : `$${spend.pricedUsd.toFixed(4)} priced`;
    return `${spend.occurrences} occurrence${spend.occurrences === 1 ? '' : 's'} · ${spend.inputTokens.toLocaleString('en-US')} in / ${spend.outputTokens.toLocaleString('en-US')} out · ${priced}${spend.unknownPriceCalls ? ` + ${spend.unknownPriceCalls} unknown price` : ''}`;
  }

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
