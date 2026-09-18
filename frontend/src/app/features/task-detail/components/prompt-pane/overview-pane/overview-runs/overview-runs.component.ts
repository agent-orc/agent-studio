import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { RunRecord } from '../../../../../run-timeline';
import type { CliType, TaskInfo } from '../../../../../../models/task.model';
import {
  cliTypeLabel,
  formatCompactDateTime,
  formatTokens,
  shortModelName,
} from '../../../../../../services/format.util';
import { formatDuration } from '../overview-pane-formatters';

const KNOWN_CLIS: readonly CliType[] = ['claude', 'codex', 'gemini'];

interface OverviewRunVm {
  record: RunRecord;
  startedAt: string | null;
  trigger: string;
  result: string;
  duration: string;
  engine: string | null;
  tokens: string | null;
  reason: string | null;
  reportHref: string | null;
}

@Component({
  selector: 'app-overview-runs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overview-runs.component.html',
  styleUrl: './overview-runs.component.scss',
})
export class OverviewRunsComponent {
  /** Run records from the currently open task only. */
  readonly runs = input<readonly RunRecord[]>([]);
  readonly job = input<TaskInfo | null>(null);

  /**
   * Persisted CORE duration used only when an interrupted run has no timeline
   * duration. The visible run rows remain exclusively timeline-backed.
   */
  readonly fallbackDurationSeconds = input(0);

  /** Latest run first, preserving each timeline index as the durable row id. */
  readonly rows = computed<OverviewRunVm[]>(() =>
    [...this.runs()]
      .sort((left, right) => right.index - left.index)
      .map((run) => ({
        record: run,
        startedAt: this.startedAtLabel(run),
        trigger: this.triggerLabel(run),
        result: this.resultLabel(run),
        duration: this.durationLabel(run),
        engine: this.engineLabel(run),
        tokens: this.tokenLabel(run),
        reason: this.reasonLabel(run),
        reportHref: this.triggerReportHref(run),
      })),
  );

  /** Aggregate count is always the sum of the visible card-scoped rows. */
  runCount(): number { return this.rows().length; }

  /**
   * Lift the common agent/model out of repeated rows. A single-run panel also
   * uses this summary; mixed panels retain only the per-run deviations.
   */
  sharedEngine(): string | null { return this.rows().at(-1)?.engine ?? null; }

  readonly totalDurationSeconds = computed(() => {
    const recorded = this.rows().reduce(
      (total, row) => total + Math.max(0, row.record.durationSeconds ?? 0),
      0,
    );
    return recorded > 0 ? recorded : Math.max(0, this.fallbackDurationSeconds());
  });

  countLabel(): string {
    const count = this.runCount();
    return count === 1 ? '1 run' : `${count} runs`;
  }

  totalDurationLabel(): string { return `${formatDuration(this.totalDurationSeconds())} total`; }

  private triggerLabel(run: RunRecord): string {
    const reason = run.triggerReason?.trim();
    const actor = run.triggeredBy?.trim();
    const suffix = reason ? `: ${reason}` : '';
    switch (run.trigger) {
      case 'initial': return run.index === 1 ? 'Initial start' : 'Initial trigger recorded on a later run';
      case 'operator-continue': return `Operator continue${actor ? ` by ${actor.replace(/^operator\s+/i, '')}` : ''}${suffix}`;
      case 'review-finding': return `Review finding${this.reviewTriggerContext(run) ?? suffix}`;
      case 'review-concern': return `Review concern${this.reviewTriggerContext(run) ?? suffix}`;
      case 'integration-recovery': return `Integration recovery${suffix}`;
      case 'gate-failure': return `Gate failure${suffix}`;
      case 'timeout-continuation': return `Timeout continuation${suffix}`;
      case 'recovery-after-crash': return `Recovery after crash${suffix}`;
      case 'restart': return `Restart${suffix}`;
      case 'replan': return `Replan${suffix}`;
      case 'dependency-release': return `Dependency release${suffix}`;
      default: return 'Not recorded';
    }
  }

  private reviewTriggerContext(run: RunRecord): string | null {
    const review = this.triggerSourceField(run, 'review');
    const aspects = this.triggerSourceField(run, 'aspects')
      ?.split(',')
      .map((aspect) => aspect.replaceAll('-', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase()))
      .join(', ');
    if (!review && !aspects) return null;
    return `: ${aspects || 'Review'}${review ? ` (${review})` : ''}`;
  }

  private triggerReportHref(run: RunRecord): string | null {
    const job = this.job();
    const review = this.triggerSourceField(run, 'review');
    const failure = this.triggerSourceField(run, 'failure');
    if (!job || (!review && !failure)) return null;
    const firstAspect = this.triggerSourceField(run, 'aspects')?.split(',')[0]?.trim();
    const file = failure
      ? 'pipeline-execution.json'
      : review!.startsWith('local-review-') && firstAspect
        ? `aspect-${firstAspect}.json`
        : `remote-review-grade-${review!.replace(/[^A-Za-z0-9_.-]/g, '_')}.md`;
    return `/api/tasks/${encodeURIComponent(job.id)}/files/${encodeURIComponent(file)}?watchPath=${encodeURIComponent(job.watchPath)}`;
  }

  private triggerSourceField(run: RunRecord, key: string): string | null {
    const entry = run.triggerSource?.split(';').find((part) => part.startsWith(`${key}=`));
    return entry?.slice(key.length + 1).trim() || null;
  }

  private resultLabel(run: RunRecord): string {
    if (this.isLegacyUnrecorded(run)) return 'Not recorded (legacy run)';
    const outcome = run.result?.trim().toLowerCase();
    const raw = outcome === 'environmentfailure'
      ? run.status
      : outcome || run.status.trim() || 'unknown';
    const label = raw.replaceAll('-', ' ');
    return label.charAt(0).toUpperCase() + label.slice(1);
  }

  private durationLabel(run: RunRecord): string {
    if (run.durationSeconds != null && run.durationSeconds >= 0) {
      return formatDuration(run.durationSeconds);
    }
    if (run.status.trim().toLowerCase() === 'running') return 'In progress';
    return this.isLegacyUnrecorded(run)
      ? 'Not recorded (legacy run)'
      : 'Unknown (not recorded)';
  }

  private isLegacyUnrecorded(run: RunRecord): boolean {
    return !run.result?.trim() && run.closeoutSource?.startsWith('legacy-') === true;
  }

  private tokenLabel(run: RunRecord): string | null {
    const usage = run.tokenSummary;
    if (!usage) return null;
    return `${formatTokens(Math.max(0, usage.totalTokens))} tokens`;
  }

  /**
   * Absolute wall-clock start, never a relative "x ago": the rows are rendered
   * inside a polled change-detection pass, so a Date.now()-derived label would
   * churn (and risk NG0100). Unparseable stamps are dropped rather than shown
   * as "Invalid Date".
   */
  private startedAtLabel(run: RunRecord): string | null {
    const raw = run.startedAt?.trim();
    return raw && !Number.isNaN(Date.parse(raw)) ? formatCompactDateTime(raw) : null;
  }

  /**
   * "Which agent ran this attempt" — CLI plus the model it actually reported.
   * The runner-resolved fields are authoritative for new runs; the run's own
   * execution context and token rollup remain read fallbacks for older
   * records, so a run never inherits the card's current model.
   */
  private engineLabel(run: RunRecord): string | null {
    const cli = run.cli?.trim() ?? '';
    const cliLabel = (KNOWN_CLIS as readonly string[]).includes(cli)
      ? cliTypeLabel(cli as CliType)
      : cli;
    const rawModel =
      run.model?.trim()
      || run.executionContext?.model?.trim()
      || run.tokenSummary?.lastModel?.trim()
      || '';
    const model = rawModel ? shortModelName(rawModel) : '';
    const thinkingLevel = run.thinkingLevel?.trim() ?? '';
    const modelLabel = model && thinkingLevel ? `${model} · ${thinkingLevel}` : model;
    if (cliLabel && modelLabel) return `${cliLabel} · ${modelLabel}`;
    return cliLabel || modelLabel || null;
  }

  /**
   * Why the run ended the way it did (recovery cause, escalation reason). Only
   * carried for runs that did not simply complete — a reason on a green run is
   * noise, and the full text stays available as the row's title.
   */
  private reasonLabel(run: RunRecord): string | null {
    const reason = run.reason?.trim();
    if (!reason) return null;
    return run.status.trim().toLowerCase() === 'completed' ? null : reason;
  }
}
