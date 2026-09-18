import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import type { CliType, TaskInfo, TaskPromptHistoryEntry } from '../../../../../models/task.model';
import type { ReviewAttemptCycle, RunCommitInfo, RunPromptEntry, RunRecord } from '../../../../../features/run-timeline';
import { TaskService } from '../../../../../services/task.service';
import { cliTypeIcon, cliTypeLabel, formatTime as formatTimeValue, formatTokens } from '../../../../../services/format.util';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { ExecutionLocationBadgeComponent } from '../../../../../components/execution-location-badge/execution-location-badge.component';
import { RunExecutionContextComponent } from './run-execution-context/run-execution-context.component';
import { ReviewAttemptHistoryComponent } from './review-attempt-history/review-attempt-history.component';
/**
 * Run timeline panel rendered above the activity log in the protocol
 * pane. Each card represents one CLI invocation between user inputs
 * (one "run" - the unit of conversation defined in
 * `docs/quality/design-principles.md`). The collapsed card shows:
 *
 * - intent badge (start / continue / recovery / restart)
 * - status badge (running / completed / failed / cancelled)
 * - the user follow-up that triggered the run, if any
 * - duration + exit code
 *
 * Clicking a card expands it. The expanded card requests the
 * software-side change set (`/api/tasks/.../runs/{n}/commits`) and
 * renders the commit list. The activity-log filter for the run's
 * line-span is the next iteration; this component already emits the
 * selected run via `runSelected` so the parent can apply it.
 *
 * The component owns no run data of its own - it reads the timeline
 * from RunTimelinePollService and the commits from TaskService on
 * demand. That keeps the polling cadence in one place and avoids
 * stale per-card state when the timeline updates.
 */
@Component({
  selector: 'app-run-timeline',
  standalone: true,
  imports: [TooltipDirective, ExecutionLocationBadgeComponent, RunExecutionContextComponent, ReviewAttemptHistoryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-timeline.component.html',
  styleUrl: './run-timeline.component.scss'
})
export class RunTimelineComponent {
  readonly job = input<TaskInfo | null>(null);
  readonly runs = input<RunRecord[]>([]);
  readonly promptEntries = input<RunPromptEntry[]>([]);
  readonly promptMarkdown = input<string | null>(null);
  readonly promptHistory = input<TaskPromptHistoryEntry[]>([]);
  readonly reviewAttemptEpoch = input(0);
  readonly reviewAttemptCycles = input<ReviewAttemptCycle[]>([]);

  /** Emits when the user clicks "Filter activity log to this run". */
  readonly runFilter = output<RunRecord>();

  /** Emits when the user clicks "Open git viewer" on a run card. */
  readonly openGitViewer = output<RunRecord>();

  readonly expandedIndex = signal<number | null>(null);

  readonly selectedRun = computed<RunRecord | null>(() => {
    const idx = this.expandedIndex();
    if (idx == null) return null;
    return this.visibleRuns().find(r => r.index === idx) ?? null;
  });

  readonly selectedPrompt = computed<RunPromptEntry | null>(() => {
    const run = this.selectedRun();
    if (!run) return null;
    return this.promptItems().find(i => i.run.index === run.index)?.entry ?? null;
  });

  readonly selectedPromptText = computed<string | null>(() => {
    const run = this.selectedRun();
    const prompt = this.selectedPrompt();
    if (!run || !prompt) return null;
    return this.promptTextFor(prompt, run);
  });

  readonly statusSummary = computed(() => {
    const v = this.visibleRuns();
    let completed = 0, failed = 0, running = 0, other = 0;
    for (const r of v) {
      switch (r.status) {
        case 'completed': completed++; break;
        case 'failed':    failed++; break;
        case 'running':   running++; break;
        default:          other++; break;
      }
    }
    return { completed, failed, running, other };
  });

  // Per-card commit state. Keyed by run.index so a re-poll of the
  // timeline doesn't wipe a card's loaded commits.
  readonly commitsByRun = signal<Map<number, RunCommitInfo[]>>(new Map());
  readonly commitsState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly commitsError = signal<string | null>(null);

  // Per-card passed-context state. The context (rendered prompt) can be
  // multi-KB, so it is fetched lazily only when the user reveals it, keyed
  // by run.index so the cached text survives a timeline re-poll.
  readonly contextExpandedIndex = signal<number | null>(null);
  readonly contextByRun = signal<Map<number, string | null>>(new Map());
  readonly contextState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly contextError = signal<string | null>(null);

  readonly contextText = computed<string | null>(() => {
    const idx = this.contextExpandedIndex();
    if (idx == null) return null;
    return this.contextByRun().get(idx) ?? null;
  });

  /** Chronological order - reissues must read as separate run segments. */
  readonly visibleRuns = computed(() => [...this.runs()].sort((a, b) => a.index - b.index));

  readonly promptItems = computed(() => {
    const runs = this.visibleRuns();
    const runByIndex = new Map(runs.map(r => [r.index, r]));
    const entries = this.promptEntries();
    if (entries.length > 0) {
      return entries
        .map(entry => ({ entry, run: runByIndex.get(entry.runIndex) }))
        .filter((item): item is { entry: RunPromptEntry; run: RunRecord } => !!item.run)
        .sort((a, b) => a.entry.index - b.entry.index);
    }
    return runs.map((run, idx) => ({
      run,
      entry: this.fallbackPromptEntry(run, idx + 1),
    }));
  });

  readonly commits = computed<RunCommitInfo[]>(() => {
    const idx = this.expandedIndex();
    if (idx == null) return [];
    return this.commitsByRun().get(idx) ?? [];
  });

  readonly totalAdded = computed(() => this.commits().reduce((s, c) => s + c.added, 0));
  readonly totalRemoved = computed(() => this.commits().reduce((s, c) => s + c.removed, 0));
  readonly totalFiles = computed(() => this.commits().reduce((s, c) => s + c.filesChanged, 0));

  private readonly jobService = inject(TaskService);

  toggle(index: number): void {
    this.expandedIndex.set(index);
    this.contextExpandedIndex.set(null);
    this.loadCommits(index);
  }

  closePopover(): void {
    this.expandedIndex.set(null);
    this.contextExpandedIndex.set(null);
  }

  /** Reveal / hide the context block for a run, fetching the text on first reveal. */
  toggleContext(index: number): void {
    if (this.contextExpandedIndex() === index) {
      this.contextExpandedIndex.set(null);
      return;
    }
    if (this.expandedIndex() !== index) {
      this.expandedIndex.set(index);
      this.loadCommits(index);
    }
    this.contextExpandedIndex.set(index);
    this.loadContext(index);
  }

  nextRunAfter(index: number): RunRecord | null {
    const runs = this.visibleRuns();
    const pos = runs.findIndex(r => r.index === index);
    return pos >= 0 && pos + 1 < runs.length ? runs[pos + 1] : null;
  }

  transitionLabel(current: RunRecord, next: RunRecord): string {
    return `Run #${current.index} re-opened into #${next.index} via ${this.triggerLabel(next)}`;
  }

  isReissueRun(r: RunRecord): boolean {
    return r.trigger === 'review-finding' || r.trigger === 'review-concern' || r.intent === 'reissue';
  }

  triggerLabel(run: RunRecord): string {
    const reason = run.triggerReason?.trim();
    const actor = run.triggeredBy?.replace(/^operator\s+/i, '').trim();
    switch (run.trigger) {
      case 'initial': return run.index === 1 ? 'Initial start' : 'Initial trigger recorded on a later run';
      case 'operator-continue': return `Operator continue${actor ? ` by ${actor}` : ''}${reason ? `: ${reason}` : ''}`;
      case 'review-finding': return `Review finding${this.reviewTriggerContext(run) ?? (reason ? `: ${reason}` : '')}`;
      case 'review-concern': return `Review concern${this.reviewTriggerContext(run) ?? (reason ? `: ${reason}` : '')}`;
      case 'integration-recovery': return `Integration recovery${reason ? `: ${reason}` : ''}`;
      case 'gate-failure': return `Gate failure${reason ? `: ${reason}` : ''}`;
      case 'timeout-continuation': return `Timeout continuation${reason ? `: ${reason}` : ''}`;
      case 'recovery-after-crash': return `Recovery after crash${reason ? `: ${reason}` : ''}`;
      case 'restart': return `Restart${reason ? `: ${reason}` : ''}`;
      case 'replan': return `Replan${reason ? `: ${reason}` : ''}`;
      case 'dependency-release': return `Dependency release${reason ? `: ${reason}` : ''}`;
      default: return 'Not recorded';
    }
  }

  triggerReportHref(run: RunRecord): string | null {
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

  private reviewTriggerContext(run: RunRecord): string | null {
    const review = this.triggerSourceField(run, 'review');
    const aspects = this.triggerSourceField(run, 'aspects')
      ?.split(',')
      .map((aspect) => aspect.replaceAll('-', ' ').replace(/\b\w/g, (letter) => letter.toUpperCase()))
      .join(', ');
    if (!review && !aspects) return null;
    return `: ${aspects || 'Review'}${review ? ` (${review})` : ''}`;
  }

  private triggerSourceField(run: RunRecord, key: string): string | null {
    const entry = run.triggerSource?.split(';').find((part) => part.startsWith(`${key}=`));
    return entry?.slice(key.length + 1).trim() || null;
  }

  promptTokenLabel(entry: RunPromptEntry | null): string {
    if (!entry?.promptTokenEstimate) return 'unknown';
    return `${formatTokens(entry.promptTokenEstimate)} tokens`;
  }

  contextTokenLabel(entry: RunPromptEntry | null): string {
    if (!entry?.contextTokenEstimate) return 'not captured';
    return `${formatTokens(entry.contextTokenEstimate)} tokens`;
  }

  promptSourceLabel(entry: RunPromptEntry | null): string {
    switch (entry?.promptTokenSource) {
      case 'task-prompt': return 'prompt.md';
      case 'prompt-history': return entry.fileName ?? 'prompt history';
      case 'user-followup': return 'user follow-up';
      case 'captured-context': return 'captured context';
      case 'missing': return 'missing';
      default: return entry?.promptTokenSource || 'unknown';
    }
  }

  snapshotSourceLabel(entry: RunPromptEntry | null): string {
    const source = entry?.contextSnapshot?.source;
    if (source === 'captured-context') return 'captured at run start';
    if (source === 'latest-context-usage') return 'latest /context usage';
    return 'not captured';
  }

  snapshotMetrics(entry: RunPromptEntry | null): string {
    const metrics = entry?.contextSnapshot?.metrics ?? [];
    if (metrics.length === 0) return '';
    return metrics.slice(0, 2).map(m => `${m.label}: ${m.value}`).join(' | ');
  }

  promptTextFor(entry: RunPromptEntry, run: RunRecord): string | null {
    switch (entry.promptTokenSource) {
      case 'task-prompt':
        return this.cleanPromptText(this.promptMarkdown());
      case 'prompt-history':
        return this.cleanPromptText(this.resolvePromptHistoryText(entry, run));
      case 'user-followup':
        return this.cleanPromptText(run.userFollowup ?? entry.promptPreview);
      case 'captured-context':
        return this.cleanPromptText(this.contextByRun().get(run.index) ?? null);
      default:
        return this.cleanPromptText(this.resolvePromptHistoryText(entry, run) ?? (run.index === 1 ? this.promptMarkdown() : null));
    }
  }

  cliIcon(cli: string | null): string {
    const type = this.cliType(cli);
    return type ? cliTypeIcon(type) : 'CLI';
  }

  cliLabel(cli: string | null): string {
    const type = this.cliType(cli);
    return type ? cliTypeLabel(type) : (cli?.trim() || 'CLI');
  }

  emitFilter(r: RunRecord): void {
    this.runFilter.emit(r);
  }

  /** Prefer the durable per-run owner; legacy latest runs use task-level facts. */
  runnerAttribution(r: RunRecord): { kind: 'remote' | 'local'; glyph: string; label: string; tooltip: string } | null {
    const job = this.job();
    if (!job) return null;
    const runs = this.visibleRuns();
    const isLatest = runs.length > 0 && runs[runs.length - 1].index === r.index;
    if (!isLatest) return null;

    const runner = job.runner ?? null;
    if (runner && runner.isRemote) {
      const name = (runner.runnerName || runner.runnerId || 'remote runner').trim();
      const host = (runner.hostname || '').trim();
      return {
        kind: 'remote',
        glyph: '⇥',
        label: name,
        tooltip: `Executed by remote runner ${name}${host ? ` on ${host}` : ''} (holds the run lease).`,
      };
    }

    const ext = job.externalCompletion ?? null;
    if (!runner && ext && (ext.source ?? '').trim()) {
      const source = ext.source.trim();
      return {
        kind: 'remote',
        glyph: '⇥',
        label: source,
        tooltip: `Handed back out-of-band by ${source} (remote runner / external source).`,
      };
    }

    if (r.status === 'running' || runner) {
      return {
        kind: 'local',
        glyph: '',
        label: 'lokal',
        tooltip: 'Executed in-process on the local backend (no remote run lease held).',
      };
    }
    return null;
  }

  intentLabel(intent: string): string {
    switch (intent) {
      case 'start': return 'start';
      case 'continue': return 'continue';
      case 'recovery': return 'recovery';
      case 'restart': return 'restart';
      case 'reissue': return 'reissue';
      default: return intent || 'run';
    }
  }

  statusLabel(r: RunRecord): string {
    if (r.status === 'running') return 'running';
    if (r.status === 'completed') return 'completed';
    if (r.status === 'failed') return 'failed';
    // 'stopped' is the deliberate-kill status (user pause, Pause-&-Send,
    // watchdog). Legacy run records may still carry 'cancelled'.
    if (r.status === 'stopped' || r.status === 'cancelled') return 'stopped';
    return r.status || 'unknown';
  }

  formatDuration(seconds: number): string {
    if (seconds < 1) return '<1s';
    if (seconds < 60) return `${seconds.toFixed(0)}s`;
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    return s === 0 ? `${m}m` : `${m}m${s}s`;
  }

  private fallbackPromptEntry(run: RunRecord, promptIndex: number): RunPromptEntry {
    const fallback = this.fallbackPromptText(run.index) ?? run.userFollowup ?? null;
    return {
      index: promptIndex,
      runIndex: run.index,
      intent: run.intent,
      at: run.startedAt,
      label: `Prompt #${promptIndex}`,
      fileName: run.index === 1 ? 'prompt.md' : null,
      promptTokenSource: fallback ? (run.index === 1 ? 'task-prompt' : 'user-followup') : 'missing',
      promptPreview: fallback ? this.preview(fallback) : run.userFollowup,
      promptTokenEstimate: fallback ? this.estimateTokens(fallback) : null,
      contextTokenEstimate: null,
      contextRef: run.contextRef,
      contextSnapshot: null,
    };
  }

  private fallbackPromptText(runIndex: number): string | null {
    if (runIndex === 1) return this.promptMarkdown();
    const history = this.promptHistory().find(h => h.index === runIndex - 1);
    return history?.markdown ?? null;
  }

  private resolvePromptHistoryText(entry: RunPromptEntry, run: RunRecord): string | null {
    const history = this.promptHistory();
    if (entry.fileName) {
      const byName = history.find(h => h.fileName === entry.fileName);
      if (byName) return byName.markdown;
    }
    const byRunIndex = history.find(h => h.index === run.index - 1);
    if (byRunIndex) return byRunIndex.markdown;
    const byPromptIndex = history.find(h => h.index === entry.index - 1);
    return byPromptIndex?.markdown ?? null;
  }

  private cleanPromptText(text: string | null | undefined): string | null {
    const trimmed = text?.trim();
    return trimmed ? trimmed : null;
  }

  private estimateTokens(text: string): number {
    return Math.max(1, Math.ceil(text.length / 4));
  }

  private preview(text: string): string {
    const compact = text.trim().replace(/\s+/g, ' ');
    return compact.length <= 180 ? compact : compact.slice(0, 177).trimEnd() + '...';
  }

  private cliType(cli: string | null): CliType | null {
    const normalized = (cli ?? '').trim().toLowerCase();
    return normalized === 'claude' ||
      normalized === 'codex' ||
      normalized === 'gemini'
        ? normalized
        : null;
  }

  formatTime(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    return formatTimeValue(iso);
  }

  short(id: string): string {
    if (!id) return '';
    return id.length > 12 ? id.slice(0, 8) + '…' : id;
  }

  private loadCommits(index: number): void {
    const job = this.job();
    if (!job) return;
    if (this.commitsByRun().has(index)) {
      // Already loaded once; don't re-fetch on re-expand. The user can
      // close + reopen the card if they need a refresh, and the parent
      // poll cycle will refresh the timeline data itself.
      this.commitsState.set('loaded');
      this.commitsError.set(null);
      return;
    }
    this.commitsState.set('loading');
    this.commitsError.set(null);
    this.jobService.getRunCommits(job.id, index, job.watchPath).subscribe({
      next: (res) => {
        const map = new Map(this.commitsByRun());
        map.set(index, res.commits);
        this.commitsByRun.set(map);
        this.commitsState.set('loaded');
      },
      error: (err) => {
        this.commitsError.set(err?.error?.error || err?.message || 'Could not load commits.');
        this.commitsState.set('error');
      }
    });
  }

  private loadContext(index: number): void {
    const job = this.job();
    if (!job) return;
    if (this.contextByRun().has(index)) {
      // Cached from a previous reveal; the context for a finished run never
      // changes, so don't re-fetch the multi-KB payload.
      this.contextState.set('loaded');
      this.contextError.set(null);
      return;
    }
    this.contextState.set('loading');
    this.contextError.set(null);
    this.jobService.getRunContext(job.id, index, job.watchPath).subscribe({
      next: (res) => {
        const map = new Map(this.contextByRun());
        map.set(index, res.context ?? null);
        this.contextByRun.set(map);
        this.contextState.set('loaded');
      },
      error: (err) => {
        this.contextError.set(err?.error?.error || err?.message || 'Could not load context.');
        this.contextState.set('error');
      }
    });
  }
}
