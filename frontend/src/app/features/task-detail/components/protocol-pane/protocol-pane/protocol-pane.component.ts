import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  OnDestroy,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import type {
  CliOutputLine,
  TaskDetail,
  TaskSummaryStatus,
  ReviewEvidenceEntry,
  ResultHistoryDocument,
} from '../../../../../models/task.model';
import { TaskState } from '../../../../../models/task.model';
import type { RunRecord } from '../../../../../features/run-timeline';
import { deriveWatchdogPill } from '../watchdog-state';
import { ActivityLogViewComponent } from '../../activity-log-view/activity-log-view';
import { buildConversationTurns, parseActivityLog, sanitizeProjectionLines } from '../../activity-log.parser';
import { classifyLatestActivityOutcome, OutcomeAssessment, QuickReply } from '../../agent-outcome.util';
import { copyTextToClipboard } from '../../../../../services/clipboard.util';
import { sessionFetch } from '../../../../../services/session-fetch';
import { ClaudeSessionPollService } from '../../../../polling/services/claude-session-poll.service';
import { CliOutputPollService } from '../../../../polling/services/cli-output-poll.service';
import { SessionEventsPollService } from '../../../../polling/services/session-events-poll.service';
import { RunTimelinePollService } from '../../../../polling/services/run-timeline-poll.service';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { ScreenshotsPollService } from '../../../../polling/services/screenshots-poll.service';
import { PlanPollService } from '../../../../polling/services/plan-poll.service';
import { PlanStripComponent } from '../../../../plan-strip';
import { NowTickService } from '../../../../../services/now-tick.service';
import { RunTimelineComponent } from '../run-timeline/run-timeline.component';
import { RunGitViewerComponent } from '../run-git-viewer/run-git-viewer.component';

import { FeatureFlagsService } from '../../../../../services/feature-flags.service';
import { VerboseDebugOverlayComponent } from '../../../../../features/verbose-debug';
import { TaskService } from '../../../../../services/task.service';
import type { ConversationEvent, RawLineRange } from 'coding-agent-chat/core';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import { mergeByTimestamp, projectConversation } from 'coding-agent-chat/core';
import { BeautifulResultsComponent } from '../../beautiful-results/beautiful-results.component';
import { ResultViewComponent } from '../result-view/result-view.component';
import { PreviousResultsControlComponent } from '../previous-results-control/previous-results-control.component';
import { FileSourceHistoryComponent } from '../../../../../components/file-source-history/file-source-history.component';
import { SourceViewerComponent, type SourceViewerRequest } from '../../source-viewer/source-viewer.component';
import { MenuComponent } from '../../../../../components/menu';
import type { MenuItem, MenuItemClickEvent } from '../../../../../components/menu';
import { deriveProtocolVerdict, stripStatusHeader, type ProtocolVerdict } from '../protocol-verdict';
import { ProtocolVerdictBannerComponent } from '../protocol-verdict-banner/protocol-verdict-banner.component';
import {
  buildInspectorTabs,
  claudeSessionTooltip,
  formatRateWindow,
  formatResetIn,
  formatTokens,
  rateLimitTooltip,
} from './protocol-pane-view-model';
import { generatedFileProvenance } from '../../generated-file-provenance.util';
import { presentActivityEvents, stripLegacyCompletionLines } from '../activity-event-presentation';
import { ActivityEventPresentationDirective } from '../activity-event-presentation.directive';
import { mergeReplayEvents, projectRunnerReplay } from '../runner-event-replay';
import { RunnerReplayMetadataComponent } from '../runner-replay-metadata/runner-replay-metadata';
import { projectStructuredActivityContent } from '../structured-activity-projection';
import { TaskInspectorTabComponent } from '../task-inspector-tab/task-inspector-tab.component';
import { DecisionSurfaceComponent } from '../../decision-surface/decision-surface.component';
import { TaskArtifactLinksDirective } from '../../task-artifact-links/task-artifact-links.directive';
import { DetailKeyboardSurfaceDirective } from '../../detail-keyboard-surface/detail-keyboard-surface.directive';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { PaneHeaderComponent } from '../../../../../components/pane-header/pane-header.component';
import { PaneTabsComponent } from '../../../../../components/pane-tabs/pane-tabs.component';
import { OverlayPortalRef, OverlayPortalService } from '../../../../../services/overlay-portal.service';
import { taskNavigationHref, taskUrl } from '../../../state/task-url';
import { LayoutPanesService } from '../../../services/layout-panes.service';
import { TaskReferenceNavigationService } from '../../../../../services/task-reference-navigation.service';
import { StudioTabStateService } from '../../../../studio-shell/services/studio-tab-state.service';
export type InspectorTab = 'task' | 'activity' | 'protocol';

/**
 * Sub-view of the Activity tab: the readable agent stream or the raw Trace.
 * A native agent plan is projected as one living checklist inside the readable
 * stream, never as a competing tab or one row per transport update.
 */
export type ActivityView = 'conversation' | 'trace';

/**
 * Transient interim-summary result shown while a job is running. Generated
 * on demand from a single Haiku call against the in-flight cli-output.log;
 * never written to status.md (the final summary owns that file).
 */
interface InterimSummaryState {
  status: 'idle' | 'pending' | 'ready' | 'failed';
  markdown: string | null;
  error: string | null;
  startedAt: number | null;
  finishedAt: number | null;
}

/**
 * Protocol pane: shows the Haiku-generated status.md (read-only), the
 * activity log, the chat-compose strip, and Claude telemetry chips in
 * the header. status.md is owned by the SummaryGenerationService on the
 * backend — there is no edit mode here.
 */
@Component({
  selector: 'app-protocol-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ActivityLogViewComponent,
    PlanStripComponent,
    ConversationViewComponent,
    RunTimelineComponent,
    RunGitViewerComponent,
    VerboseDebugOverlayComponent,
    BeautifulResultsComponent,
    ResultViewComponent,
    PreviousResultsControlComponent,
    FileSourceHistoryComponent,
    SourceViewerComponent,
    MenuComponent,
    TooltipDirective,
    ProtocolVerdictBannerComponent,
    PaneHeaderComponent,
    PaneTabsComponent,
    RunnerReplayMetadataComponent,
    TaskInspectorTabComponent,
    DecisionSurfaceComponent,
    ActivityEventPresentationDirective,
    TaskArtifactLinksDirective,
    DetailKeyboardSurfaceDirective,
  ],
  templateUrl: './protocol-pane.component.html',
  styleUrls: ['./protocol-pane.component.scss'],
})
export class ProtocolPaneComponent implements OnDestroy {
  @ViewChild(DetailKeyboardSurfaceDirective)
  private keyboardSurface?: DetailKeyboardSurfaceDirective;

  readonly detail = input.required<TaskDetail>();
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly isActiveJob = input<boolean>(false);

  readonly activeInspectorTab = model<InspectorTab>('protocol');
  readonly followupPrompt = input<string>('');
  readonly canSendChat = input(false);
  readonly chatSendLabel = input<string>('Send');
  readonly chatError = input<string | null>(null);
  readonly queuedFollowUp = input<boolean>(false);
  /**
   * True while the card carries a saved, not yet consumed follow-up
   * (`pending-intent.json`). Quieter and more precise than
   * {@link queuedFollowUp}: the prompt is on disk, so it survives restarts and
   * the next run consumes it.
   */
  readonly pendingFollowUp = input<boolean>(false);
  readonly mutationsBlocked = input(false);

  readonly regenerating = input(false);
  readonly runOutcome = input<ProtocolVerdict | null>(null);

  readonly artifactLinkContext = computed(() => ({
    jobId: this.detail().info.id,
    watchPath: this.detail().info.watchPath,
  }));

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  /** Emitted after a follow-up task was created from a review-evidence finding so the parent can refetch the detail and (optionally) navigate to the new job. */
  readonly followupCreatedFromEvidence = output<{ jobId: string; taskKey?: string; targetState: string }>();
  /** Emitted after a finding was acknowledged so the parent can refetch the detail. */
  readonly evidenceMutated = output<void>();

  readonly followupPromptChange = output<string>();

  readonly openLogOverlay = output<void>();
  readonly sendChat = output<void>();
  readonly regenerateSummary = output<void>();
  readonly decisionApplied = output<void>();

  // Live data — injected from the parent's local providers.
  private readonly claudePoll = inject(ClaudeSessionPollService);
  private readonly cliPoll = inject(CliOutputPollService);
  private readonly sessionEventsPoll = inject(SessionEventsPollService);
  private readonly runTimelinePoll = inject(RunTimelinePollService);
  private readonly pipelinePoll = inject(TaskPipelinePollService);
  private readonly screenshotsPoll = inject(ScreenshotsPollService);
  private readonly planPoll = inject(PlanPollService);
  private readonly nowTick = inject(NowTickService).now;
  private readonly jobs = inject(TaskService);
  private readonly overlayPortal = inject(OverlayPortalService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly layout = inject(LayoutPanesService);
  private readonly taskReferenceNavigation = inject(TaskReferenceNavigationService);
  private readonly tabs = inject(StudioTabStateService);

  @ViewChild('runsPortalRoot')
  private runsPortalRoot?: ElementRef<HTMLDivElement>;

  private runsPortalRef: OverlayPortalRef | null = null;

  /** Tracks the job whose Activity sub-view override is currently held. */
  private activityViewJobId: string | null = null;
  readonly nextGenChatEvents = signal<ConversationEvent[]>([]);

  constructor() {
    // A fresh task opens at its readable Activity stream. Clearing the
    // explicit pick when the job changes keeps a Trace
    // choice made on one task from leaking into the next.
    effect(() => {
      const id = this.detail().info.id;
      if (id !== this.activityViewJobId) {
        this.activityViewJobId = id;
        this.activityViewOverride.set(null);
      }
    });
    effect(() => {
      if (this.runsModalOpen()) {
        queueMicrotask(() => this.acquireRunsPortal());
      } else {
        this.releaseRunsPortal();
      }
    });
    effect((onCleanup) => {
      const baseEvents = this.baseNextGenChatEvents();
      const { id, watchPath } = this.detail().info;
      this.nextGenChatEvents.set(baseEvents);
      if (baseEvents.length === 0) return;

      let cancelled = false;
      void import('../artifact-gallery/artifact-gallery.lazy')
        .then(({ presentArtifactEvents }) => {
          if (!cancelled) {
            this.nextGenChatEvents.set(presentArtifactEvents(baseEvents, id, watchPath));
          }
        });
      onCleanup(() => {
        cancelled = true;
      });
    });
    this.destroyRef.onDestroy(() => {
      this.releaseRunsPortal();
    });
  }

  /** Set after "Create follow-up" returns; used to render the success banner. */
  readonly followupCreated = signal<{ jobId: string; taskKey?: string; targetState: string } | null>(null);

  readonly claudeSession = this.claudePoll.session;
  readonly claudeRateLimit = this.claudePoll.rateLimit;
  readonly cliOutput = this.cliPoll.output;
  readonly runTimeline = this.runTimelinePoll.timeline;
  readonly runnerEvents = computed(() => this.runTimeline()?.runnerEvents ?? []);
  readonly runnerReplay = computed(() => projectRunnerReplay(this.runnerEvents(), this.detail().info.id));
  readonly runnerTraceLines = computed(() => [
    ...this.filteredCliOutput(),
    ...this.runnerReplay().diagnosticLines,
  ].sort((a, b) => a.timestamp.localeCompare(b.timestamp)));
  readonly screenshots = this.screenshotsPoll.screenshots;
  readonly plan = this.planPoll.plan;

  /** Feature-flagged VS Code-style chrome. The "i" header button is only
   *  rendered when the flag is on; otherwise legacy chrome stays visible. */
  readonly featureFlags = inject(FeatureFlagsService);

  toggleVsCodeMeta(): void {
    this.featureFlags.setVsCodeMetaOpen(!this.featureFlags.vsCodeMetaOpen());
  }

  /**
   * Active run filter for the activity log. Set when the user clicks
   * "Filter activity log to this run" on a run-card; cleared via the
   * banner. The filter is line-span-based so we can re-apply it
   * deterministically as cliOutput grows during a live run.
   */
  readonly runFilterRange = signal<{ index: number; lineStart: number; lineEnd: number } | null>(
    null,
  );

  /**
   * The activity-log lines visible in the embedded view. When a run
   * filter is active, slice cliOutput to the run's [lineStart..lineEnd]
   * (inclusive, 1-based). Otherwise return the full buffer. Open-ended
   * runs (still streaming) keep the upper bound at the buffer length so
   * new lines stream into the filter.
   */
  readonly filteredCliOutput = computed<CliOutputLine[]>(() => {
    const all = this.cliOutput();
    const range = this.runFilterRange();
    if (!range) return all;
    const start = Math.max(1, range.lineStart) - 1;
    const end = Math.min(all.length, range.lineEnd);
    if (end <= start) return [];
    return all.slice(start, end);
  });

  onRunFilter(r: RunRecord): void {
    if (r.lineStart == null) return;
    const end = r.lineEnd ?? this.cliOutput().length;
    this.runFilterRange.set({ index: r.index, lineStart: r.lineStart, lineEnd: end });
    // Filtering targets the activity log behind the modal — close it so the
    // user sees the filtered output instead of the run list.
    this.runsModalOpen.set(false);
  }

  /**
   * The compact "N Runs" header element opens the full run list/picker in a
   * modal instead of stacking the icon-bar above the activity log. The count
   * mirrors the polled run timeline so the header stays in sync.
   */
  readonly runsModalOpen = signal(false);
  readonly runCount = computed<number>(() => this.runTimeline()?.runs?.length ?? 0);

  /**
   * Definition surfaced by the "i" icon-button next to the run count. A run is
   * one execution attempt of the task; the intents come from the ADR-0049 run
   * model (start / continue / recovery / restart). HTML body is sanitized by
   * the tooltip controller.
   */
  readonly runInfoTooltip = {
    title: 'What is a run?',
    body:
      'A <strong>run</strong> is one execution attempt of this task — a single agent/CLI invocation between your inputs.<br><br>' +
      'Runs are tagged by intent: <strong>start</strong> (first attempt), <strong>continue</strong> (a follow-up turn), and <strong>recovery</strong> / <strong>restart</strong> (a re-run after a problem).<br><br>' +
      'See ADR-0049 for the task run model.',
  };

  clearRunFilter(): void {
    this.runFilterRange.set(null);
  }

  // Big Git Viewer modal state. Opened from a run card via the
  // "Open git viewer" button; closed via the modal's own close
  // affordance or backdrop click. The selected RunRecord is held as
  // a signal so the modal re-binds its load when the user opens a
  // different run without closing first.
  readonly gitViewerRun = signal<RunRecord | null>(null);

  openGitViewer(r: RunRecord): void {
    if (!r.headShaBefore || !r.headShaAfter) return;
    this.gitViewerRun.set(r);
    // The diff overlay renders above the activity log, so close the run-list
    // modal to avoid stacking two overlays.
    this.runsModalOpen.set(false);
  }

  closeGitViewer(): void {
    this.gitViewerRun.set(null);
  }

  // Source viewer overlay, opened from a clickable source reference in the
  // protocol / interim-summary markdown. Held in a signal so re-clicking a
  // different file/line re-binds the viewer without closing it first.
  readonly sourceViewerRequest = signal<SourceViewerRequest | null>(null);

  openSource(ref: { path: string; line: number | null }): void {
    if (!ref?.path) return;
    this.sourceViewerRequest.set({ path: ref.path, line: ref.line });
  }

  openWiki(path: string): void {
    const projectName = this.detail().info.projectName;
    if (!projectName || !path) return;
    try {
      const key = `atp.projectWiki.v1.${projectName}`;
      const current = JSON.parse(localStorage.getItem(key) ?? '{}') as Record<string, unknown>;
      localStorage.setItem(key, JSON.stringify({ ...current, openedRel: path, viewerTab: 'doc' }));
    } catch {
      // The Wiki itself remains navigable when browser storage is unavailable.
    }
    this.tabs.open({
      kind: 'hub',
      projectName,
      section: 'wiki',
      wikiTarget: { kind: 'page', relPath: path },
    });
  }

  openTask(taskKey: string): void {
    this.taskReferenceNavigation.openTaskKey(taskKey);
  }

  closeSourceViewer(): void {
    this.sourceViewerRequest.set(null);
  }

  /**
   * Drives the session-status chip in the header. Shape:
   *   - kind: "continued" | "lost" | "fresh" | null
   *   - chainLength: number of real session ids ever recorded
   *   - segmentCount: bumps every time the chain breaks (recovery)
   *   - tooltip: human-readable summary of the latest event
   * Returns null when there are no events yet (never been started).
   */
  readonly sessionChip = computed(() => {
    const r = this.sessionEventsPoll.response();
    if (!r || r.events.length === 0) return null;
    const last = r.events[r.events.length - 1];
    const chainLength = r.sessionChain.filter((s) => s && s !== '(recovery)').length;
    const segmentCount =
      r.sessionChain.filter((s) => s === '(recovery)').length + (chainLength > 0 ? 1 : 0);

    let kind: 'continued' | 'lost' | 'fresh';
    let label: string;
    let emoji: string;
    if (last.kind === 'recovery') {
      kind = 'lost';
      emoji = '⚠';
      label = 'session lost — recovered';
    } else if (last.kind === 'continue') {
      kind = 'continued';
      emoji = '✓';
      label = `session continued${chainLength > 1 ? ` (chain: ${chainLength})` : ''}`;
    } else {
      kind = 'fresh';
      emoji = '●';
      label = 'session started';
    }

    const reasonLine = last.reason ? `Reason: ${last.reason}\n` : '';
    const inputLine = last.inputSessionId ? `Resumed from: ${last.inputSessionId}\n` : '';
    const capturedLine = last.capturedSessionId ? `Captured: ${last.capturedSessionId}\n` : '';
    const tooltip = [
      `Last event: ${last.kind} (${last.cli ?? '?'})`,
      `When: ${last.ts}`,
      reasonLine.trim(),
      inputLine.trim(),
      capturedLine.trim(),
      `Chain length: ${chainLength}`,
      segmentCount > 1 ? `Chain breaks: ${segmentCount - 1}` : '',
    ]
      .filter(Boolean)
      .join('\n');

    return { kind, label, emoji, tooltip, chainLength, segmentCount };
  });

  readonly summaryStatus = computed<TaskSummaryStatus>(
    () => this.detail().summaryState?.status ?? 'none',
  );

  readonly statusIsSuperseded = computed<boolean>(() => {
    const generation = this.detail().statusGeneration;
    const execution = this.pipelinePoll.pipeline()?.execution;
    const currentAttempt = execution?.attempt;
    if (currentAttempt == null) return false;
    if (generation?.runIndex != null) return generation.runIndex < currentAttempt;

    // Legacy status files have no runIndex. While a later attempt is active,
    // any still-visible status belongs to the already closed attempt because
    // the current attempt has not produced its terminal summary yet.
    return currentAttempt > 1
      && execution?.completedAt == null
      && !!this.detail().statusMarkdown?.trim();
  });

  /** Shared authoritative outcome, with a local fallback for isolated tests. */
  readonly protocolVerdict = computed<ProtocolVerdict>(() =>
    this.runOutcome() ?? deriveProtocolVerdict({
      isRunning: this.isRunning(),
      summaryStatus: this.summaryStatus(),
      statusMarkdown: this.detail().statusMarkdown,
      outcomeIssue: this.detail().info.outcomeIssue,
      hasActivity: this.hasActivity(),
      laneState: this.detail().info.state,
      orchestratorVerdict: this.detail().info.orchestratorVerdict,
      statusSuperseded: this.statusIsSuperseded(),
      execution: this.detail().info.execution,
      pipelineExecution: this.pipelinePoll.pipeline()?.execution ?? null,
      activityOutcome: this.outcome(),
    }),
  );

  readonly selectedPreviousResult = signal<ResultHistoryDocument | null>(null);
  readonly displayedResultDetail = computed<TaskDetail>(() => {
    const previous = this.selectedPreviousResult();
    return previous
      ? { ...this.detail(), statusMarkdown: previous.markdown, statusGeneration: null }
      : this.detail();
  });
  readonly displayedResultVerdict = computed<ProtocolVerdict>(() => {
    const previous = this.selectedPreviousResult();
    if (!previous) return this.protocolVerdict();
    return deriveProtocolVerdict({
      isRunning: false,
      summaryStatus: 'ready',
      statusMarkdown: previous.markdown,
      outcomeIssue: null,
      hasActivity: true,
    });
  });

  onResultMetricNavigate(metricId: string): void {
    if (metricId === 'grade') this.layout.openPromptTab('description', 'codeReview');
    if (metricId === 'artifacts') this.layout.openPromptTab('description');
  }

  /**
   * Status.md body with the `# Status` header (Result + Duration) lifted out
   * so it does not duplicate what the verdict pill already shows. Used for
   * both rendered and raw views; the copy-markdown button still copies the
   * untouched source.
   */
  readonly statusMarkdownBody = computed<string>(() =>
    stripStatusHeader(this.detail().statusMarkdown),
  );
  readonly statusProvenance = computed(() =>
    generatedFileProvenance(this.detail().statusGeneration),
  );
  /**
   * Same header strip the rendered/raw views apply, handed to the file-history
   * pane so the live body and every historical `status.md` version render with
   * the `# Status` header lifted out (the verdict pill already shows it).
   */
  readonly statusHistoryTransform = (raw: string): string => stripStatusHeader(raw);

  /**
   * Progressive spinner label so a slow Haiku call doesn't look frozen.
   * The backend caps the call at HaikuTimeoutSeconds = 90 s; we
   * intentionally mirror that constant here. Tiers:
   *   < 30 s         "Generating the result..."
   *   30 s ... 60 s  "Generating the result... (>=30 s)"
   *   >= 60 s        "Generating the result... (>=60 s, will time out)"
   * Re-evaluates on every NowTickService tick while summaryStatus is
   * 'generating'; falls back to the base label as soon as the state
   * flips to ready or failed.
   */
  readonly summarySpinnerLabel = computed<string>(() => {
    if (this.summaryStatus() !== 'generating') return 'Generating the result...';
    const startedAtIso = this.detail().summaryState?.startedAt;
    if (!startedAtIso) return 'Generating the result...';
    const elapsed = (this.nowTick() - new Date(startedAtIso).getTime()) / 1000;
    if (elapsed >= 60) return 'Generating the result... (>=60 s, will time out)';
    if (elapsed >= 30) return 'Generating the result... (>=30 s)';
    return 'Generating the result...';
  });

  /**
   * Watchdog pill state derived purely from polled output frames + the
   * NowTickService clock. Re-evaluates whenever cliOutput or the clock
   * tick changes; the chip in the header reads .visible to decide
   * whether to render at all and uses .label / .state / .tooltip for
   * the visual.
   */
  readonly watchdogPill = computed(() =>
    deriveWatchdogPill({
      lines: this.cliOutput(),
      isRunning: this.isRunning(),
      now: new Date(this.nowTick()),
    }),
  );
  /** The open task is implicit; a running task is paused by the Send flow. */
  composePlaceholder(): string {
    return this.isRunning()
      ? 'Message this task. Sending pauses the current run first. Ctrl+Enter to send.'
      : 'Message this task. Ctrl+Enter to send.';
  }

  /** Task / Activity / Result tab strip for the shared pane-tabs component. */
  readonly protocolTabs = computed(() =>
    buildInspectorTabs({
      summaryStatus: this.summaryStatus(),
      hasStatusMarkdown: !!this.detail().statusMarkdown,
      hasCliActivity: this.cliOutput().length > 0,
      isHumanReview:
        this.detail().info.state === TaskState.HumanReview ||
        this.detail().info.state === TaskState.Escalated,
      isRunning: this.isRunning(),
    }),
  );

  /** Bridge from the generic pane-tabs change event to the parent. */
  onInspectorTabChange(id: string): void {
    if (id === 'task' || id === 'activity' || id === 'protocol') {
      this.activeInspectorTab.set(id);
      this.focusTabSurface();
    }
  }

  focusTabSurface(): void {
    this.keyboardSurface?.focus();
  }

  readonly canRegenerate = computed(() => {
    const status = this.summaryStatus();
    if (status === 'generating') return false;
    if (this.regenerating()) return false;
    return status !== 'none' || !!this.detail().statusMarkdown || this.cliOutput().length > 0;
  });

  /**
   * Heuristic classification of the agent's last reply (see
   * {@link classifyOutcome}). Drives the auto-eval banner above the chat
   * input: a one-line summary of where the agent landed plus up to four
   * quick-reply chips that pre-fill the follow-up prompt. While the CLI is
   * actively running we suppress the banner — the "outcome" is still in
   * flight and chips would race the streaming text.
   */
  readonly outcome = computed<OutcomeAssessment | null>(() => {
    if (this.isRunning()) return null;
    return classifyLatestActivityOutcome(this.cliOutput());
  });

  /** True when the auto-eval banner should be visible. */
  readonly outcomeVisible = computed(() => {
    const o = this.outcome();
    if (!o) return false;
    if (o.kind === 'unknown' && !o.question) return false;
    return o.suggestions.length > 0;
  });

  // "There is or was activity for this job" — drives the live-dot indicator.
  // True when CLI is running OR we have any output buffered OR the job has a
  // log/usage record from a previous run.
  readonly hasActivity = computed(() => {
    if (this.isRunning()) return true;
    if (this.cliOutput().length > 0) return true;
    const d = this.detail();
    return d.log.length > 0 || d.info.lastUsage != null;
  });

  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  /**
   * Live "interim status" banner state. Populated when the user clicks the
   * `📊 Interim status` button while a run is in flight. The button calls
   * `POST /api/tasks/{id}/summary/interim`, which fires a one-shot Haiku
   * against the current cli-output.log but does NOT touch status.md. The
   * banner is transient: dismissing it clears the markdown back to null.
   */
  readonly interimSummary = signal<InterimSummaryState>({
    status: 'idle',
    markdown: null,
    error: null,
    startedAt: null,
    finishedAt: null,
  });

  /** Elapsed seconds since the interim call started; for the pending label. */
  readonly interimElapsedSeconds = computed<number>(() => {
    const s = this.interimSummary();
    if (s.status !== 'pending' || s.startedAt === null) return 0;
    return Math.max(0, Math.floor((this.nowTick() - s.startedAt) / 1000));
  });

  /** True while a `📊 Interim status` request is in flight. Drives the button disabled state. */
  readonly interimInFlight = computed<boolean>(() => this.interimSummary().status === 'pending');

  /**
   * Whether to show the "📊 Interim status" button in the protocol-pane
   * header. We surface it whenever the agent is running so the user can
   * peek at progress without stopping the run. Hidden otherwise to keep
   * the header from looking like a control panel when the task is idle.
   */
  readonly canRequestInterim = computed<boolean>(() => this.isRunning() && !this.interimInFlight());

  requestInterimSummary(): void {
    if (this.interimInFlight()) return;
    const job = this.detail().info;
    this.interimSummary.set({
      status: 'pending',
      markdown: null,
      error: null,
      startedAt: Date.now(),
      finishedAt: null,
    });
    this.jobs.requestInterimSummary(job.id, job.watchPath).subscribe({
      next: (resp) => {
        this.interimSummary.set({
          status: 'ready',
          markdown: resp.markdown ?? '',
          error: null,
          startedAt: this.interimSummary().startedAt,
          finishedAt: Date.now(),
        });
      },
      error: (err) => {
        const message = err?.error?.error ?? err?.message ?? 'Interim summary failed';
        this.interimSummary.set({
          status: 'failed',
          markdown: null,
          error: message,
          startedAt: this.interimSummary().startedAt,
          finishedAt: Date.now(),
        });
      },
    });
  }

  dismissInterimSummary(): void {
    this.interimSummary.set({
      status: 'idle',
      markdown: null,
      error: null,
      startedAt: null,
      finishedAt: null,
    });
  }

  /**
   * Drives the read-only Verbose Debug overlay. Opened from the activity-log
   * header; closed via the overlay's own close button. Trace links route to
   * the existing log overlay so the raw activity log remains one click away.
   */
  readonly verboseDebugOpen = signal(false);

  /**
   * The Activity tab is one readable stream with secondary actions in the
   * overflow menu. `activityViewOverride` holds the user's explicit Trace pick;
   * when null the panel renders readable CLI output plus the living plan row.
   *
   * CLI uses the next-gen `cac-conversation-view` over the `ConversationEvent[]`
   * projection when the flag is enabled, and otherwise falls back to the
   * legacy activity-log conversation view. Trace remains an overflow action.
   */
  private readonly activityViewOverride = signal<ActivityView | null>(null);
  readonly activityMenuOpen = signal(false);
  readonly activityMenuAnchor = signal<HTMLElement | null>(null);
  readonly activityDebugEnabled = signal(false);

  /** True once a usable task plan exists - mirrors `PlanStripComponent.visible`. */
  readonly planAvailable = computed<boolean>(() => {
    const p = this.plan();
    return !!p && p.hasPlan && p.items.length > 0;
  });

  /** The compact CLI sub-view is always available; next-gen chat is only its preferred renderer. */
  readonly conversationAvailable = computed<boolean>(() => true);
  readonly nextGenConversationEnabled = computed<boolean>(() => this.featureFlags.nextGenChat());

  /** Sub-view shown when the user has not picked one explicitly. */
  readonly defaultActivityView = computed<ActivityView>(() => {
    return 'conversation';
  });

  /** Effective Activity sub-view: the user's pick if still valid, else the default. */
  readonly activityView = computed<ActivityView>(() => {
    return this.activityViewOverride() ?? this.defaultActivityView();
  });

  readonly activityMenuItems = computed<readonly MenuItem[]>(() => [
    { kind: 'row', id: 'conversation', label: 'Agent events', active: this.activityView() === 'conversation', disabled: this.filteredCliOutput().length === 0 },
    {
      kind: 'row',
      id: 'trace',
      label: 'Trace',
      active: this.activityView() === 'trace',
      disabled: this.filteredCliOutput().length === 0,
    },
    {
      kind: 'row',
      id: 'debug',
      label: 'Debug',
      active: this.activityDebugEnabled(),
      disabled: this.filteredCliOutput().length === 0 && !this.runTimeline(),
    },
    { kind: 'separator' },
    {
      kind: 'row',
      id: 'copy',
      label: this.activityCopyLabel(),
      disabled: !this.activityCopyText().trim(),
    },
  ]);

  setActivityView(view: ActivityView): void {
    this.activityViewOverride.set(view);
  }

  openActivityMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.activityMenuAnchor.set(event.currentTarget as HTMLElement);
    this.activityMenuOpen.set(true);
  }

  closeActivityMenu(): void {
    this.activityMenuOpen.set(false);
  }

  onActivityMenuItemClick(ev: MenuItemClickEvent): void {
    switch (ev.id) {
      case 'conversation':
        this.setActivityView('conversation');
        break;
      case 'trace':
        this.setActivityView('trace');
        break;
      case 'debug':
        if (this.nextGenConversationEnabled() && this.activityView() === 'conversation') {
          this.verboseDebugOpen.set(true);
        } else {
          this.activityDebugEnabled.update((v) => !v);
        }
        break;
      case 'copy':
        void this.copyActivityView();
        break;
    }
    this.closeActivityMenu();
  }

  /**
   * Projected conversation events for the next-gen chat renderer. Pure
   * derivation over the existing polled signals (cliOutput, runTimeline,
   * screenshots, tokenSummary). The projection itself stays pure TypeScript;
   * the host only feeds it the evidence it already has in scope. Dossier
   * preview events are off until slice 6 wires the split presets.
   */
  readonly baseNextGenChatEvents = computed<ConversationEvent[]>(() => {
    if (!this.featureFlags.nextGenChat()) return [];
    const filtered = this.filteredCliOutput();
    if (filtered.length === 0 && !this.runTimeline() && !this.screenshots().length) {
      return [];
    }
    const info = this.detail().info;
    const screenshots = this.screenshots().map((s) => ({
      caption: s.caption || s.fileName,
      sourcePath: s.localPath || s.relativePath,
      durablePath: s.relativePath,
      sourceTool: 'screenshot',
      timestamp: s.timestampUtc,
    }));
    const replay = this.runnerReplay();
    const typedLifecycle = this.runnerEvents().some(event => event.kind === 'turn.completed');
    const structured = projectStructuredActivityContent(filtered, info.id);
    const projected = projectConversation({
      source: info.id,
      // Strip transport frames before the library classifies them. See sanitizeProjectionLines.
      lines: sanitizeProjectionLines(stripLegacyCompletionLines(structured.projectionLines, typedLifecycle)),
      task: info,
      runTimeline: this.runTimeline(),
      tokenSummary: info.tokenSummary ?? null,
      screenshots,
      emitRunMarkers: replay.timelineEvents.length === 0,
      emitWorkbenchSummary: false,
      emitWorkbenchPreviews: false,
      emitTraceLink: false,
      emitDebugAggregate: false,
    });
    const worktreeRootsByRun = new Map<number, string>();
    for (const run of this.runTimeline()?.runs ?? []) {
      const root = run.executionLocation?.worktreePath;
      if (root) worktreeRootsByRun.set(run.index, root);
    }
    // Present after Studio-native structured events join the library
    // projection so both CLI formats receive the same path and row cleanup.
    const presented = presentActivityEvents(
      mergeByTimestamp(projected, structured.events),
      info.id,
      info.watchPath,
      {
        typedTurnCompletions: typedLifecycle,
        worktreeRootsByRun,
        fallbackWorktreeRoot: info.executionLocation?.worktreePath,
      },
    );
    return mergeReplayEvents(presented, replay.timelineEvents);
  });

  onConversationOpenTrace(range: RawLineRange | null): void {
    void range;
    this.setActivityView('trace');
  }

  onConversationOpenVerboseDebug(): void {
    this.verboseDebugOpen.set(true);
  }

  exitConversationTraceFallback(): void {
    this.setActivityView('conversation');
  }

  onVerboseDebugOpenTrace(range: RawLineRange): void {
    void range;
    // Route trace links to the existing activity-log maximized view so the
    // raw activity log stays the single source of truth for line-level
    // inspection. Closing Verbose Debug first keeps focus deterministic.
    this.verboseDebugOpen.set(false);
    this.openLogOverlay.emit();
  }

  ngOnDestroy(): void {
    if (this.copyResetTimer !== null) {
      clearTimeout(this.copyResetTimer);
      this.copyResetTimer = null;
    }
    this.releaseRunsPortal();
  }


  private acquireRunsPortal(): void {
    if (!this.runsModalOpen() || this.runsPortalRef) return;
    const root = this.runsPortalRoot?.nativeElement;
    if (!root) return;
    this.runsPortalRef = this.overlayPortal.attachModal(root);
  }

  private releaseRunsPortal(): void {
    this.runsPortalRef?.dispose();
    this.runsPortalRef = null;
  }

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy';
  }

  copyIconLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓';
    if (s === 'failed') return '⚠';
    return '📋';
  }

  activityCopyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return 'Copied';
    if (s === 'failed') return 'Copy Failed';
    return 'Copy';
  }

  async copyActivityView(): Promise<void> {
    const text = this.activityCopyText();
    if (!text.trim()) return;
    const ok = await copyTextToClipboard(text);
    this.copyState.set(ok ? 'copied' : 'failed');
    if (this.copyResetTimer !== null) clearTimeout(this.copyResetTimer);
    this.copyResetTimer = setTimeout(() => {
      this.copyState.set('idle');
      this.copyResetTimer = null;
    }, 2000);
  }

  private activityCopyText(): string {
    switch (this.activityView()) {
      case 'trace':
        return this.traceCopyText();
      default:
        return this.cliCopyText();
    }
  }

  private cliCopyText(): string {
    const turns = buildConversationTurns(parseActivityLog(this.filteredCliOutput()));
    const parts: string[] = [];
    for (const turn of turns) {
      if (turn.kind === 'tools') {
        parts.push(`[${this.formatActivityTime(turn.timestamp)}] Tools (${turn.groups.length} group${turn.groups.length === 1 ? '' : 's'})`);
      } else {
        parts.push(`[${this.formatActivityTime(turn.timestamp)}] ${turn.kind}\n${turn.text}`);
      }
    }
    return parts.join('\n\n');
  }

  private traceCopyText(): string {
    return this.filteredCliOutput()
      .map((line) => `[${this.formatActivityTime(line.timestamp)}] ${line.stream.toUpperCase()} ${line.text}`)
      .join('\n');
  }

  private formatActivityTime(dateStr: string): string {
    const d = new Date(dateStr);
    if (Number.isNaN(d.getTime())) return dateStr;
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  // --- Protocol context menu (F54) ---
  readonly protocolViewMode = signal<'rendered' | 'raw' | 'history'>('rendered');
  readonly protocolMenuOpen = signal(false);
  readonly protocolMenuPosition = signal<{ x: number; y: number } | null>(null);

  readonly protocolMenuItems = computed<readonly MenuItem[]>(() => {
    const items: MenuItem[] = [
      {
        kind: 'row',
        id: 'regenerate',
        label: 'Regenerate from CLI output',
        disabled: !this.canRegenerate(),
      },
      { kind: 'separator' },
      {
        kind: 'row',
        id: 'view-rendered',
        label: 'View rendered',
        active: this.protocolViewMode() === 'rendered',
      },
      {
        kind: 'row',
        id: 'view-raw',
        label: 'View raw markdown',
        active: this.protocolViewMode() === 'raw',
      },
      {
        kind: 'row',
        id: 'view-history',
        label: 'View version history',
        active: this.protocolViewMode() === 'history',
      },
    ];
    return items;
  });

  openProtocolMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.protocolMenuPosition.set({ x: event.clientX, y: event.clientY });
    this.protocolMenuOpen.set(true);
  }

  closeProtocolMenu(): void {
    this.protocolMenuOpen.set(false);
  }

  onProtocolMenuItemClick(ev: MenuItemClickEvent): void {
    switch (ev.id) {
      case 'regenerate':
        this.regenerateSummary.emit();
        break;
      case 'view-rendered':
        this.protocolViewMode.set('rendered');
        break;
      case 'view-raw':
        this.selectedPreviousResult.set(null);
        this.protocolViewMode.set('raw');
        break;
      case 'view-history':
        this.selectedPreviousResult.set(null);
        this.protocolViewMode.set('history');
        break;
    }
    this.closeProtocolMenu();
  }

  async copyProtocolMarkdown(): Promise<void> {
    const md = this.displayedResultDetail().statusMarkdown ?? '';
    if (!md) return;
    const ok = await copyTextToClipboard(md);
    this.copyState.set(ok ? 'copied' : 'failed');
    if (this.copyResetTimer !== null) clearTimeout(this.copyResetTimer);
    this.copyResetTimer = setTimeout(() => {
      this.copyState.set('idle');
      this.copyResetTimer = null;
    }, 2000);
  }

  readonly formatTokens = formatTokens;
  readonly formatRateWindow = formatRateWindow;

  formatResetIn(epoch: number): string {
    return formatResetIn(epoch, this.nowTick());
  }

  claudeSessionTooltip(): string {
    return claudeSessionTooltip(this.claudeSession());
  }

  /**
   * Quick-reply chip click handler. Always pre-fills the input rather than
   * sending immediately — the user reviews and confirms before the follow-up
   * goes out. The default-false `autoSend` flag on individual chips is the
   * future hook for one-click sends, kept here for symmetry but not wired up
   * until we have telemetry that says it would not surprise users.
   */
  applyQuickReply(reply: QuickReply): void {
    this.followupPromptChange.emit(reply.prompt);
  }

  /**
   * The activity-log view raises this when the user clicks an option button
   * on a steer card. We mirror the quick-reply behavior: pre-fill the
   * compose box so the user can edit and confirm, never auto-send.
   */
  onSteerOptionApply(option: string): void {
    if (!option) return;
    this.followupPromptChange.emit(option);
  }

  /**
   * The activity-log view raises this when the user clicks "Send screenshot"
   * on a steer card whose Need line mentions a screenshot. We open the job's
   * attachment uploader (a hidden &lt;input type=file&gt; in the template).
   */
  onSteerUploadRequest(): void {
    const input = document.querySelector<HTMLInputElement>(
      '[data-testid="orchestrator-steer-upload-input"]',
    );
    input?.click();
  }

  /**
   * Posts the chosen file to the job's attachments endpoint. Mirrors the
   * upload path used by the prompt editor (`/api/tasks/{id}/attachments`)
   * so the screenshot lands next to other task attachments where the
   * orchestrator can reference it on the next decision call.
   */
  async onSteerFileSelected(file: File | undefined | null): Promise<void> {
    if (!file) return;
    const job = this.detail()?.info;
    if (!job?.id) return;
    const watchPath = job.watchPath ?? '';
    const url =
      `/api/tasks/${encodeURIComponent(job.id)}/attachments` +
      (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
    const form = new FormData();
    form.append('file', file, file.name || 'steer-screenshot.png');
    try {
      await sessionFetch(url, { method: 'POST', body: form });
    } catch {
      /* upload failure is best-effort; the user retains the steer card
         so they can try again or send a follow-up message instead */
    }
  }

  onEvidenceAcknowledge(
    payload: { entry: ReviewEvidenceEntry; acknowledged: boolean },
    panel: { clearBusy(): void },
  ): void {
    const job = this.detail().info;
    this.jobs
      .acknowledgeReviewEvidence(job.id, payload.entry.id, payload.acknowledged, job.watchPath)
      .subscribe({
        next: () => {
          panel.clearBusy();
          this.evidenceMutated.emit();
        },
        error: () => panel.clearBusy(),
      });
  }

  onEvidenceCreateFollowup(entry: ReviewEvidenceEntry, panel: { clearBusy(): void }): void {
    const job = this.detail().info;
    this.jobs.createReviewEvidenceFollowup(job.id, entry.id, {}, job.watchPath).subscribe({
      next: (resp) => {
        panel.clearBusy();
        this.followupCreated.set(resp);
        this.followupCreatedFromEvidence.emit(resp);
        this.evidenceMutated.emit();
      },
      error: () => panel.clearBusy(),
    });
  }

  dismissFollowupBanner(): void {
    this.followupCreated.set(null);
  }

  onOpenFollowup(followup: string | { jobId: string; taskKey?: string }): void {
    if (typeof window === 'undefined') return;
    const reference = typeof followup === 'string' ? { jobId: followup } : followup;
    const navigate = (href: string | null): void => { if (href) window.location.href = href; };
    if (reference.taskKey) return navigate(taskUrl(reference.taskKey, new URL(window.location.href)));
    this.jobs.getDetail(reference.jobId, this.detail().info.watchPath).subscribe(
      (detail) => navigate(taskNavigationHref(detail.info))
    );
  }

  rateLimitTooltip(): string {
    return rateLimitTooltip(this.claudeRateLimit(), this.nowTick());
  }
}
