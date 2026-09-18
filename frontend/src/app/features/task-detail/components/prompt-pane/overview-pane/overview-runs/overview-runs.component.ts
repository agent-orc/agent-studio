import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { RunRecord } from '../../../../../run-timeline';
import type { CliType } from '../../../../../../models/task.model';
import {
  cliTypeLabel,
  formatCompactDateTime,
  shortModelName,
} from '../../../../../../services/format.util';
import { formatDuration, formatTokens } from '../overview-pane-formatters';

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
    switch (run.intent.trim().toLowerCase()) {
      case 'start':
        return 'Initial start';
      case 'continue':
        return run.userFollowup?.trim() ? 'User follow-up' : 'Continue';
      case 'recovery':
        return 'Recovery';
      case 'restart':
        return 'Restart';
      case 'reissue':
        return 'Review reissue';
      default:
        return run.intent.trim() || 'Run';
    }
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
