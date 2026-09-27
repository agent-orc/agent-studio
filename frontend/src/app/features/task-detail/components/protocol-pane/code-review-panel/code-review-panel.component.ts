import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';

import { FormsModule } from '@angular/forms';
import type { CliType, TaskInfo } from '../../../../../models/task.model';
import { CodeReviewListEntry, TaskService } from '../../../../../services/task.service';
import { CodeReviewActivityStore } from '../../../../../services/code-review-activity.store';

import { CliModelSelectorComponent } from '../../../../../components/cli-model-selector';
import { FileSourceHistoryComponent } from '../../../../../components/file-source-history/file-source-history.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { cleanStepResultMarkdown } from '../../prompt-pane/pipeline-step-result/pipeline-step-result.util';
import { CLAUDE_FALLBACK_MODEL_ID } from '../../../../cli';
import { generatedFileProvenance } from '../../generated-file-provenance.util';
import { codeReviewVerdictTone, type CodeReviewVerdictTone } from '../../code-review-verdict.util';
import { describeDiffSize, isLargeDiff } from '../../../../../utils/large-diff-gate';
import { formatDateTimeUtc } from '../../../../../services/format.util';
import { buildTokenCostTooltip } from '../../../../tokens';
import { CouncilReviewReactionComponent } from '../council-review-reaction/council-review-reaction.component';

/** localStorage key holding the last CLI+model the operator ran a review with. */
const LAST_AGENT_STORAGE_KEY = 'atp.codeReview.lastAgent';

/**
 * User-triggered code-review panel that lives in the protocol pane.
 *
 * <p>Three jobs:</p>
 * <ul>
 *   <li>Render the existing <code>code-review-*.md</code> artifacts as a
 *       newest-first list with verdict chip + one-line summary; rows
 *       expand to show the MD body inline.</li>
 *   <li>Drive the "Run Code Review" action: a unified
 *       <code>&lt;app-cli-model-selector&gt;</code> (CLI + model) defaults
 *       to <code>claude</code> plus the shared Claude fallback model, the Run
 *       button POSTs the chosen pair and shows a spinner until the
 *       response arrives.</li>
 *   <li>Cover the user's "Progress an die Karte, dass da gerade eine
 *       Code-Review läuft" two ways: the in-panel spinner stays visible for
 *       the whole CLI call, and the run is registered in the shared
 *       {@link CodeReviewActivityStore} so the kanban card renders a "code
 *       review…" badge even when the operator navigates away from the
 *       detail pane.</li>
 * </ul>
 *
 * <p>The CLI list is intentionally not filtered (see
 * <code>docs/quality/frontend/audits/cli-model-selector-audit.md</code>): the backend
 * <code>POST /api/tasks/{id}/code-review</code> endpoint accepts an
 * arbitrary <code>cliType</code> field, so the operator may run a review
 * with any installed CLI even though Claude remains the default.</p>
 */
@Component({
  selector: 'app-code-review-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    CliModelSelectorComponent,
    FileSourceHistoryComponent,
    TooltipDirective,
    CouncilReviewReactionComponent,
  ],
  templateUrl: './code-review-panel.component.html',
  styleUrl: './code-review-panel.component.scss',
})
export class CodeReviewPanelComponent implements OnInit {
  readonly job = input.required<TaskInfo>();
  /** Optional override for the model dropdown's initial value. */
  readonly defaultModel = input<string>(CLAUDE_FALLBACK_MODEL_ID);
  /** Optional override for the CLI dropdown's initial value. */
  readonly defaultCli = input<CliType>('claude');

  readonly entries = signal<CodeReviewListEntry[]>([]);
  readonly loading = signal(true);
  readonly running = signal(false);
  readonly runningMode = signal<'verdict' | 'grade' | null>(null);
  readonly error = signal<string | null>(null);
  readonly expandedFile = signal<string | null>(null);
  readonly expandedBody = signal<string | null>(null);
  readonly expandedBodyRevealed = signal(false);
  readonly selectedModel = signal<string>(CLAUDE_FALLBACK_MODEL_ID);
  readonly selectedThinkingLevel = signal<string | null>(null);
  readonly selectedCli = signal<CliType>('claude');

  private readonly jobs = inject(TaskService);
  private readonly activity = inject(CodeReviewActivityStore);

  /** True when there is at least one MD listed and the user can drill in. */
  readonly hasEntries = computed(() => this.entries().length > 0);
  /** Last available grade when the newest review belongs to a newer delivery. */
  readonly olderGrade = computed<CodeReviewListEntry | null>(() => {
    const entries = [...this.entries()].sort((a, b) => (b.runAt ?? '').localeCompare(a.runAt ?? ''));
    const graded = entries.find((entry) => !!entry.grade?.trim()) ?? null;
    if (!graded) return null;
    const newerReviewExists = graded !== entries[0];
    const reviewAt = Date.parse(graded.runAt);
    const legacyCommit = this.job().commit;
    const commits = this.job().commits ?? (legacyCommit ? [legacyCommit] : []);
    const deliveryTimes = commits
      .map((commit) => commit.at)
      .map((value) => Date.parse(value ?? ''))
      .filter(Number.isFinite);
    const reviewedCommit = graded.commit?.trim().toLowerCase() || null;
    const latestCommit = commits.at(-1)?.sha?.trim().toLowerCase() || null;
    const newerCommitExists = !!reviewedCommit
      && !!latestCommit
      && !latestCommit.startsWith(reviewedCommit)
      && !reviewedCommit.startsWith(latestCommit);
    const newerDeliveryExists = Number.isFinite(reviewAt)
      && deliveryTimes.some((timestamp) => timestamp > reviewAt);
    return newerReviewExists || newerCommitExists || newerDeliveryExists ? graded : null;
  });
  readonly bodyIsLarge = computed<boolean>(() => isLargeDiff(this.expandedBody()));
  readonly bodySizeLabel = computed<string>(() => describeDiffSize(this.expandedBody()));
  readonly bodyGated = computed<boolean>(() => this.bodyIsLarge() && !this.expandedBodyRevealed());
  readonly reviewContentTransform = cleanStepResultMarkdown;

  ngOnInit(): void {
    // Seed precedence (directly serves the operator's "remember the last
    // model" + "configurable default if there is no last one" asks):
    //   1. last-used pair persisted in localStorage,
    //   2. the deployment-configured default from the backend,
    //   3. the shared input fallbacks.
    const remembered = this.readLastAgent();
    if (remembered) {
      this.selectedCli.set(remembered.cliType);
      this.selectedModel.set(remembered.model);
      this.selectedThinkingLevel.set(remembered.thinkingLevel ?? null);
    } else {
      // Provisional fallback while the configured default loads.
      this.selectedModel.set(this.defaultModel());
      this.selectedCli.set(this.defaultCli());
      this.jobs.codeReviewDefaults().subscribe({
        next: (defaults) => {
          // Only adopt server defaults if the operator hasn't picked since.
          if (this.readLastAgent()) return;
          if (defaults.cliType) this.selectedCli.set(defaults.cliType as CliType);
          if (defaults.model) this.selectedModel.set(defaults.model);
        },
        error: () => {
          // Keep the hard-coded fallbacks already set above.
        },
      });
    }
    this.refresh();
  }

  /** Read the remembered last-used pair, tolerating absent/corrupt storage. */
  private readLastAgent(): { cliType: CliType; model: string; thinkingLevel?: string | null } | null {
    try {
      const raw = localStorage.getItem(LAST_AGENT_STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as { cliType?: string; model?: string; thinkingLevel?: string | null };
      if (!parsed?.cliType || !parsed?.model) return null;
      return { cliType: parsed.cliType as CliType, model: parsed.model, thinkingLevel: parsed.thinkingLevel ?? null };
    } catch {
      return null;
    }
  }

  /** Persist the chosen pair so the next visit seeds from it. */
  private rememberLastAgent(cliType: CliType, model: string, thinkingLevel: string | null = null): void {
    if (!cliType || !model) return;
    try {
      localStorage.setItem(
        LAST_AGENT_STORAGE_KEY,
        JSON.stringify({ cliType, model, thinkingLevel }),
      );
    } catch {
      // Private-mode / quota failures are non-fatal; the picker still works.
    }
  }

  /** Re-pull the listing. Public so the parent can call it after a run. */
  refresh(): void {
    const job = this.job();
    if (!job?.id) return;
    this.loading.set(true);
    this.error.set(null);
    this.jobs.listCodeReviews(job.id, job.watchPath).subscribe({
      next: (resp) => {
        this.entries.set(resp.entries ?? []);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.message ?? 'Failed to load code reviews.');
        this.loading.set(false);
      },
    });
  }

  /**
   * Trigger one review pass against the job's most recent commit. The
   * request body intentionally omits <code>commit</code> so the backend
   * resolves HEAD at request time. The button stays disabled and the
   * spinner stays up until the POST resolves.
   */
  runReview(mode: 'verdict' | 'grade' = 'verdict'): void {
    const job = this.job();
    if (!job?.id) return;
    if (this.running()) return;
    this.running.set(true);
    this.runningMode.set(mode);
    this.error.set(null);
    // Register the run in the shared store so the kanban card shows a
    // "code review…" badge for the whole synchronous call, even if the
    // operator navigates away from this detail pane.
    const activityKey = CodeReviewActivityStore.key(job.watchPath, job.id);
    this.activity.markRunning(activityKey);
    const body: { model?: string; cliType?: string; thinkingLevel?: string | null } = {
      cliType: this.selectedCli(),
    };
    const model = this.selectedModel().trim();
    if (model) body.model = model;
    if (this.selectedThinkingLevel()) body.thinkingLevel = this.selectedThinkingLevel();
    this.jobs.runCodeReview(job.id, { ...body, mode }, job.watchPath).subscribe({
      next: (resp) => {
        // Remember the pair the backend actually ran with, so the next
        // visit seeds from a real run rather than a transient picker state.
        const ranCli = (resp?.cliType as CliType) || this.selectedCli();
        const ranModel = resp?.model || this.selectedModel();
        this.selectedCli.set(ranCli);
        this.selectedModel.set(ranModel);
        const ranThinkingLevel = resp?.thinkingLevel ?? this.selectedThinkingLevel();
        this.selectedThinkingLevel.set(ranThinkingLevel ?? null);
        this.rememberLastAgent(ranCli, ranModel, ranThinkingLevel ?? null);
        this.running.set(false);
        this.runningMode.set(null);
        this.activity.clear(activityKey);
        this.refresh();
      },
      error: (err) => {
        this.error.set(err?.message ?? 'Code review failed.');
        this.running.set(false);
        this.runningMode.set(null);
        this.activity.clear(activityKey);
      },
    });
  }

  /** Atomic commit from the shared selector picker. */
  onAgentCommit(change: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    this.selectedCli.set(change.cliType);
    this.selectedModel.set(change.model);
    this.selectedThinkingLevel.set(change.thinkingLevel);
    this.rememberLastAgent(change.cliType, change.model, change.thinkingLevel);
  }

  /** Toggle the inline body view for one row. */
  toggle(entry: CodeReviewListEntry): void {
    if (this.expandedFile() === entry.fileName) {
      this.expandedFile.set(null);
      this.expandedBody.set(null);
      this.expandedBodyRevealed.set(false);
      return;
    }
    this.expandedFile.set(entry.fileName);
    this.expandedBody.set(null);
    this.expandedBodyRevealed.set(false);
    const job = this.job();
    if (!job?.id) return;
    this.jobs.readCodeReview(job.id, entry.fileName, job.watchPath).subscribe({
      next: (resp) => {
        if (this.expandedFile() === entry.fileName) {
          this.expandedBody.set(cleanStepResultMarkdown(resp.content));
        }
      },
      error: () => {
        if (this.expandedFile() === entry.fileName) {
          this.expandedBody.set('Failed to load review body.');
        }
      },
    });
  }

  revealExpandedBody(): void {
    this.expandedBodyRevealed.set(true);
  }

  verdictTone(v: string): CodeReviewVerdictTone {
    return codeReviewVerdictTone(v);
  }

  gradeTone(grade: string | null | undefined): string {
    const normalized = grade?.trim().toUpperCase();
    if (normalized === 'A' || normalized === 'B') return 'pass';
    if (normalized === 'C') return 'concerns';
    if (normalized === 'D') return 'block';
    return 'neutral';
  }

  trackByFile(_index: number, entry: CodeReviewListEntry): string {
    return entry.fileName;
  }

  formatTimestamp(iso: string): string {
    return formatDateTimeUtc(iso);
  }

  tokenLabel(entry: CodeReviewListEntry): string | null {
    const input = entry.inputTokens ?? entry.generation?.tokensIn ?? 0;
    const output = entry.outputTokens ?? entry.generation?.tokensOut ?? 0;
    const cacheRead = entry.cacheReadTokens ?? entry.generation?.cacheReadTokens ?? 0;
    const cacheWrite = entry.cacheCreationTokens ?? entry.generation?.cacheCreationTokens ?? 0;
    const total = entry.totalTokens ?? entry.generation?.tokensTotal ?? (input + output + cacheRead + cacheWrite);
    if (total <= 0) return null;
    return `${this.compactTokens(input)} in / ${this.compactTokens(output)} out (${this.compactTokens(total)})`;
  }

  tokenTooltip(entry: CodeReviewListEntry): string {
    const input = entry.inputTokens ?? entry.generation?.tokensIn ?? 0;
    const output = entry.outputTokens ?? entry.generation?.tokensOut ?? 0;
    const cacheRead = entry.cacheReadTokens ?? entry.generation?.cacheReadTokens ?? 0;
    const cacheWrite = entry.cacheCreationTokens ?? entry.generation?.cacheCreationTokens ?? 0;
    const total = entry.totalTokens ?? entry.generation?.tokensTotal ?? (input + output + cacheRead + cacheWrite);
    return buildTokenCostTooltip({
      costUsd: entry.estimatedApiCostUsd,
      priceKnown: entry.priceKnown === true,
      context: [
        `${input.toLocaleString('en-US')} input + ${output.toLocaleString('en-US')} output`,
        `${cacheRead.toLocaleString('en-US')} cache read + ${cacheWrite.toLocaleString('en-US')} cache write`,
        `${total.toLocaleString('en-US')} total tokens`,
        `Pricing date: ${entry.runAt || 'recorded execution time'}.`,
      ].join('\n'),
    });
  }

  private compactTokens(value: number): string {
    if (value >= 1_000_000) return `${Number((value / 1_000_000).toFixed(1))}m`;
    if (value >= 1_000) return `${Number((value / 1_000).toFixed(1))}k`;
    return `${value}`;
  }

  provenanceFor(entry: CodeReviewListEntry) {
    return generatedFileProvenance(entry.generation);
  }
}
