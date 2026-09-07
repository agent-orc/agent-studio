import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { WatchPathEntry } from '../../../../models/task.model';
import type {
  ChatExecutionContext,
  ComposerLocationContext,
  OrchestratorChatTurn,
  OrchestratorContextSession,
  OrchestratorContextSourceOption,
} from '../../../../features/orchestrator';
import {
  buildChatNavigationContext,
} from '../../../../features/orchestrator';
import { ChatComponent } from 'coding-agent-chat/composer';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import {
  ChatContextAttachment,
  ChatEvent,
  ChatModelSelection,
  ChatSubmitEvent,
} from 'coding-agent-chat/core';
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { OrchestratorContextHeaderComponent } from '../orchestrator-context-header/orchestrator-context-header.component';
import { ChatSwitcherRailComponent } from '../chat-switcher-rail/chat-switcher-rail.component';
import { OrchestratorPanelHeaderComponent } from '../orchestrator-panel-header/orchestrator-panel-header.component';
import { OrchestratorJumpLatestComponent } from '../orchestrator-jump-latest/orchestrator-jump-latest.component';
import { OrchestratorContextReceiptComponent } from '../orchestrator-context-receipt/orchestrator-context-receipt.component';
import { OrchestratorContextPickerComponent } from '../orchestrator-context-picker/orchestrator-context-picker.component';
import { OrchestratorPanelStateService } from '../../state/orchestrator-panel-state.service';
import { OrchestratorChatActivityService } from '../../state/orchestrator-chat-activity.service';
import { OrchestratorContextDigestService } from '../../state/orchestrator-context-digest.service';
import { OrchestratorComposerModelService } from '../../state/orchestrator-composer-model.service';
import {
  buildOrchestratorConversationEvents,
  sameOrchestratorChatTurns,
} from './orchestrator-side-sheet.util';
import {
  buildNavigationContextKey,
  orchestratorContextErrorMessage,
  parseOrchestratorContextKey,
  resolveEffectiveContextKey,
} from './orchestrator-context-key.util';
import { pageContextKey, type PageContext } from '../../../../models/page-context.model';
import {
  AUTOMATIC_CONTEXT_ATTACHMENT_ID,
  buildComposerContextAttachments,
  buildContextChipPresentation,
} from '../../composer-location-context';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';
import { PlanStripComponent } from '../../../plan-strip';
import { OrchestratorTaskPlanStore } from '../../state/orchestrator-task-plan.store';

/**
 * Push-layout side sheet hosting automatic context-keyed orchestrator chats.
 * The reusable composer owns chat interaction; this host owns app context,
 * transcript endpoints, and the ORCH-1 read digest.
 */
@Component({
  selector: 'app-orchestrator-side-sheet',
  standalone: true,
  imports: [
    ChatComponent,
    ConversationViewComponent,
    AppTooltipDirective,
    SidesheetComponent,
    OrchestratorContextHeaderComponent,
    OrchestratorContextReceiptComponent,
    OrchestratorContextPickerComponent,
    ChatSwitcherRailComponent,
    OrchestratorPanelHeaderComponent,
    OrchestratorJumpLatestComponent,
    PlanStripComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-side-sheet.component.html',
  styleUrl: './orchestrator-side-sheet.component.scss',
  providers: [OrchestratorContextDigestService, OrchestratorTaskPlanStore],
  host: {
    '[class.is-open]': 'open()',
    // When open, drive the host width from the persisted user choice so
    // the resize handle can grow / shrink the panel live. When closed the
    // binding evaluates to null and the static `:host { width: 0 }` rule
    // from the .scss wins, so the close transition still works.
    '[style.width.px]': 'open() ? panelWidth() : null'
  }
})
export class OrchestratorSideSheetComponent implements OnInit, OnDestroy {
  readonly jobService = inject(TaskService);
  readonly composerModel = inject(OrchestratorComposerModelService);
  readonly projects = input<string[]>([]);
  readonly preferredProject = input<string | null>(null);
  /** One-shot shell event for an explicit project entry from an empty editor. */
  readonly projectEntryRevision = input(0);
  readonly watchPaths = input<WatchPathEntry[]>([]);
  /**
   * Canonical active-tab context, derived by Studio and rendered through
   * CAC's `composerContext` input.
   */
  readonly composerContext = input<ComposerLocationContext | null>(null);
  /** Active repository page carried inside the existing project chat. */
  readonly pageContext = input<PageContext | null>(null);

  /**
   * Phase 6 inputs: when a task detail is open, the host passes the
   * job id, title, and watch path so the side sheet can offer an
   * "Active task" tab whose chat sends Continue (mode: Steer)
   * follow-ups to that specific task. All three are required together;
   * the tab only shows when both id and title are non-empty.
   */
  readonly activeJobId = input<string | null>(null);
  readonly activeJobTitle = input<string | null>(null);
  readonly activeWatchPath = input<string | null>(null);

  /**
   * "Where am I" context-header inputs. The host resolves the lane/state
   * and the live run for the scope in view (the open task, or — on the
   * board — the running task in the active project) and hands them here so
   * the context header can render the operator's current location without
   * re-fetching. `activeJobKey` is the short display key (e.g. `AGT-1916`);
   * `activeJobState` is the canonical lane key; `activeRun` is non-null only
   * while a CLI run is executing in scope.
   */
  readonly activeJobKey = input<string | null>(null);
  readonly activeJobState = input<string | null>(null);
  readonly activeRun = input<{ model: string | null; startedAt: string | null } | null>(null);

  /**
   * Phase: Verbose Debug. Emitted when the user clicks the bug icon in the
   * sheet header while a task tab is in scope. The host (app shell) opens
   * the read-only Verbose Debug overlay against the active task by fetching
   * its evidence (cli output, run timeline, screenshots) and feeding the
   * shared `<app-verbose-debug-overlay>` component.
   */
  readonly openVerboseDebug = output<{ jobId: string; watchPath: string; jobTitle: string | null }>();

  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  /**
   * Emitted when the user clicks the settings (⚙) button in the sidesheet
   * header. The host opens the Orchestrator Settings modal (which uses the
   * project-shell rail + panel layout). The modal replaces the former
   * inline "Logic" tab so the sidesheet stays Chat-centric.
   */
  readonly openSettings = output<void>();
  readonly navigateToContext = output<string>();

  /** Persisted open/closed and resize state for the panel. */
  private readonly panelState = inject(OrchestratorPanelStateService);
  readonly chatActivity = inject(OrchestratorChatActivityService);
  private readonly uiPreferences = inject(UiPreferencesService);
  readonly open = this.panelState.open;
  readonly panelWidth = this.panelState.width;
  readonly activeProject = signal<string | null>(null);
  readonly selectedContextKey = signal<string | null>(null);
  private readonly selectedContextNavigationKey = signal<string | null>(null);
  readonly contextSessions = signal<OrchestratorContextSession[]>([]);
  readonly activeChatCount = computed(() =>
    this.chatActivity.workingContextKeys(this.contextSessions()).size);
  readonly contextMenuOpen = signal(false);
  readonly contextDigestState = inject(OrchestratorContextDigestService);
  readonly taskPlanState = inject(OrchestratorTaskPlanStore);
  private readonly seenContexts = signal<Record<string, string>>(this.readSeenContexts());

  /** Total selectable scopes represented by the header context badge. */
  readonly contextCount = computed(() => {
    const keys = new Set<string>(['global']);
    for (const project of this.projects()) keys.add(`project:${project}`);
    for (const session of this.contextSessions()) keys.add(session.contextKey);
    return keys.size;
  });

  readonly unreadContextKeys = computed<ReadonlySet<string>>(() => {
    const seen = this.seenContexts();
    return new Set(this.contextSessions()
      .filter(session => session.updatedAt && session.contextKey !== this.contextKey()
        && session.updatedAt > (seen[session.contextKey] ?? ''))
      .map(session => session.contextKey));
  });

  private readonly selectedSession = computed(() => {
    const key = this.effectiveSelectionKey();
    return key ? this.contextSessions().find(session => session.contextKey === key) ?? null : null;
  });

  private readonly selectedTask = computed(() => {
    const session = this.selectedSession();
    if (!session || session.kind !== 'task') return null;
    return this.jobService.jobs().find(job => job.projectName === session.projectId
      && (job.taskKey === session.taskKey || job.displayKey === session.taskKey || job.key === session.taskKey)) ?? null;
  });

  /**
   * MC-2 (Concept §4): the side sheet's context follows the operator's
   * navigation — a task page yields a `task` context, the board yields a
   * `project` context. `pinned` freezes that so navigating away no longer
   * auto-switches the sheet; the frozen scope is captured in
   * {@link pinnedSnapshot} at pin time. There is deliberately no "create
   * context" UI: the context is derived from navigation, never authored.
   */
  readonly pinned = signal(false);
  private readonly pinnedSnapshot = signal<{
    project: string | null;
    jobId: string | null;
    jobTitle: string | null;
    jobKey: string | null;
    jobState: string | null;
    watchPath: string | null;
  } | null>(null);

  /**
   * Synchronous active-tab task projection shared with the visible composer
   * footer. It wins over the asynchronously hydrated detail inputs so the
   * request cannot briefly fall back to board scope during a tab switch.
   */
  private readonly composerTaskContext = computed(() => {
    if (this.pageContext()) return null;
    const context = this.composerContext();
    return context?.taskKey ? context : null;
  });

  private readonly navigationProject = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.project ?? null)
      : (this.pageContext()?.projectName ?? this.composerTaskContext()?.project ?? this.activeProject()));
  private readonly navigationJobId = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobId ?? null)
      : (this.composerTaskContext()?.taskId ?? this.activeJobId()));
  private readonly navigationJobTitle = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobTitle ?? null)
      : (this.composerTaskContext()?.taskTitle ?? this.activeJobTitle()));
  private readonly navigationJobKey = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobKey ?? null)
      : (this.composerTaskContext()?.taskKey ?? this.activeJobKey()));
  private readonly navigationJobState = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobState ?? null)
      : (this.composerTaskContext()?.taskState ?? this.activeJobState()));
  private readonly navigationWatchPath = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.watchPath ?? null)
      : (this.composerTaskContext()?.taskWatchPath ?? this.activeWatchPath()));
  private readonly navigationContextKey = computed(() => buildNavigationContextKey(
    this.navigationProject(),
    this.navigationJobKey(),
  ));
  private readonly contextResolution = computed(() => resolveEffectiveContextKey(
    this.navigationContextKey(),
    this.selectedContextKey(),
    this.selectedContextNavigationKey(),
    this.projects(),
    this.contextSessions(),
  ));
  private readonly effectiveSelectionKey = computed(() =>
    this.contextResolution().discardedSelection ? null : this.selectedContextKey());
  private readonly parsedContext = computed(() => parseOrchestratorContextKey(this.contextResolution().key));

  readonly effectiveProject = computed<string | null>(() =>
    this.parsedContext()?.projectId ?? null);
  readonly effectiveJobId = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.id ?? null)
        : this.navigationJobId());
  readonly effectiveJobTitle = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.title ?? this.parsedContext()?.taskKey ?? null)
        : (this.navigationJobTitle() ?? this.parsedContext()?.taskKey ?? null));
  readonly effectiveJobKey = computed<string | null>(() =>
    this.parsedContext()?.kind === 'task' ? (this.parsedContext()?.taskKey ?? null) : null);
  readonly effectiveJobState = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.state ?? null)
        : this.navigationJobState());
  readonly effectiveWatchPath = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.watchPath ?? null)
        : this.navigationWatchPath());

  /**
   * Navigation-derived context kind and canonical context key. A task
   * context needs its canonical task key in scope; anything else is the
   * project (board) context. The key mirrors the backend registry shape
   * (`project:<PROJ>` / `task:<PROJ>/<KEY>`, see OrchestratorContextKey) and
   * the chat body reads and writes through it (see {@link readChat} and the
   * context-aware send in {@link onSubmit}), so a task page and the board no
   * longer share one history.
   */
  readonly contextKind = computed<'task' | 'project'>(() =>
    this.parsedContext()?.kind === 'task' ? 'task' : 'project');
  readonly contextKey = computed<string | null>(() => this.contextResolution().key);

  readonly turns = signal<OrchestratorChatTurn[]>([], { equal: sameOrchestratorChatTurns });
  readonly latestContextReceipt = computed(() =>
    [...this.turns()].reverse().find(turn => turn.role === 'orchestrator' && turn.contextReceipt)?.contextReceipt ?? null);
  readonly loading = signal(false);
  readonly sending = computed(() => {
    const key = this.contextKey();
    return key ? this.chatActivity.pendingContextKeys().has(key) : false;
  });
  readonly errorMsg = signal<string | null>(null);
  readonly executionContext = signal<ChatExecutionContext | null>(null);
  /** Project scope may explicitly omit context once. Task scope is mandatory. */
  readonly contextDismissed = signal(false);
  readonly contextAttachments = signal<OrchestratorContextSourceOption[]>([]);
  private readonly contextPicker = viewChild<OrchestratorContextPickerComponent>('contextPicker');

  readonly automaticContextPresentation = computed(() => buildContextChipPresentation({
    project: this.effectiveProject(),
    page: this.pageContext(),
    contextKind: this.contextKind(),
    taskKey: this.effectiveJobKey(),
    taskTitle: this.effectiveJobTitle(),
    location: this.composerContext(),
  }));

  readonly currentTabSource = computed<OrchestratorContextSourceOption | null>(() => {
    if (this.contextKind() !== 'project') return null;
    const project = this.effectiveProject();
    const page = this.pageContext();
    if (!project || !page || page.projectName !== project) return null;
    const reference = { kind: 'page' as const, reference: pageContextKey(page), projectId: project };
    return {
      id: `${reference.kind}:${project}:${reference.reference}`,
      category: 'current',
      label: page.title,
      detail: `${page.pageType === 'workbench' ? 'Dossier' : 'Page'} · ${page.relPath}`,
      estimateTokens: 1_200,
      reference,
    };
  });
  /** Automatic context rides on every task turn; project scope may drop it once. */
  readonly automaticContextIncluded = computed(() =>
    this.contextKind() === 'task' || !this.contextDismissed());

  /**
   * The composer's chip row is the only context surface: the automatic
   * current-tab block plus every explicitly attached source, each with its own
   * estimate. The host used to own a picker row above `<cac-chat>` for this;
   * the library renders it inside the composer since 0.4.1 (CAC-20).
   */
  readonly cacContextAttachments = computed<readonly ChatContextAttachment[]>(() =>
    buildComposerContextAttachments({
      automatic: this.automaticContextPresentation(),
      automaticIncluded: this.automaticContextIncluded(),
      automaticMandatory: this.contextKind() === 'task',
      attachments: this.contextAttachments(),
    }));

  readonly selectedContextAttachmentIds = computed(() =>
    new Set(this.contextAttachments().map(source => source.id)));

  /** Scope-aware prompt. The composer names no agent - the model selector does. */
  readonly composerPlaceholder = computed(() =>
    `Ask about this ${this.contextKind() === 'task' ? 'task' : 'project'}... `
    + 'try /bug <description> to file a bug');

  /** Locally-buffered user turns shown immediately (server is source of truth on next refresh). */
  private readonly localTurns = signal<OrchestratorChatTurn[]>([]);

  /**
   * Inline event cards interleaved with the chat (Slice B mechanism).
   * Default empty; the data source for the six event kinds (tool-call,
   * watchdog, rate-limit, decision, update, task) lands as a separate
   * task that wires SignalR / endpoint streams into this signal.
   * The `?demoEvents=1` URL flag seeds three sample events for visual
   * review and Playwright regression coverage of the rendering contract.
   */
  readonly events = signal<ChatEvent[]>([]);

  onModelCommit(selection: ChatModelSelection): void {
    this.composerModel.commit(selection);
  }

  private pollTimer: VisibleIntervalHandle | null = null;
  private contextPollTimer: VisibleIntervalHandle | null = null;
  private previousProjectEntryRevision = 0;
  private pendingStandardProjectEntry = false;
  private contextSessionsLoading = false;

  /** Canonical next-gen transcript consumed by `<cac-conversation-view>`. */
  readonly conversationEvents = computed(() => buildOrchestratorConversationEvents(
    this.turns(),
    this.localTurns(),
    this.events(),
    this.effectiveProject(),
    this.contextKey() ?? this.effectiveProject() ?? 'orchestrator-chat',
  ));

  readonly contextChipText = computed<string | null>(() => {
    const proj = this.effectiveProject();
    if (!proj) return null;
    const page = this.pageContext();
    const tail = page
      ? `${page.pageType} '${page.title}'`
      : this.contextKind() === 'task'
      ? `Task '${this.effectiveJobTitle()}'`
      : 'Board';
    return `Context: ${proj} · ${tail}`;
  });

  constructor() {
    // Session rows can outlive the navigation scope that selected them. The
    // resolver falls back synchronously; this effect also clears stale picker
    // state so subsequent interactions remain on the current navigation key.
    effect(() => {
      const discarded = this.contextResolution().discardedSelection;
      untracked(() => {
        if (!discarded) return;
        this.selectedContextKey.set(null);
        this.selectedContextNavigationKey.set(null);
      });
    });

    // MC-2: reload when the *effective context* changes — pinning freezes
    // the scope, so following the effective value (not the raw picker)
    // keeps a pinned sheet on its frozen thread while nav moves on. We track
    // contextKey() (not just the project) so navigating between the board and
    // a task in the same project — a project↔task context switch that leaves
    // effectiveProject() unchanged — still swaps the visible transcript.
    effect(() => {
      const proj = this.effectiveProject();
      const key = this.contextKey();
      untracked(() => this.contextDigestState.selectContext(key));
      this.open();
      if (this.open() && key) {
        this.localTurns.set([]);
        untracked(() => this.contextDigestState.load(key, false));
        if (proj) this.refresh(false);
      }
    });

    // Context exclusion is a one-message project override. Navigating to a
    // different scope clears it, while task scope remains mandatory.
    effect(() => {
      this.contextKey();
      this.pageContext();
      untracked(() => {
        if (this.contextDismissed()) this.contextDismissed.set(false);
        if (this.contextAttachments().length > 0) this.contextAttachments.set([]);
        this.contextPicker()?.close();
      });
    });

    effect(() => {
      const jobId = this.effectiveJobId();
      const watchPath = this.effectiveWatchPath();
      untracked(() => this.taskPlanState.select(jobId, watchPath));
    });

    /**
     * Sync activeProject from the host's preferred project. We `untracked`
     * the read of activeProject so this effect only fires when preferred
     * or the project list changes — otherwise picking a project in the
     * combobox would re-trigger the effect and snap the selection back
     * to the host's preferred project on every user pick.
     */
    effect(() => {
      const preferred = this.preferredProject();
      const projects = this.projects();
      untracked(() => {
        // MC-2: a pinned sheet ignores navigation — do not let the host's
        // preferred project snap the frozen scope back.
        if (this.pinned()) return;
        if (!preferred) {
          if (this.activeProject() == null && projects.length > 0) {
            this.activeProject.set(projects[0]);
          }
          return;
        }
        if (projects.includes(preferred) && preferred !== this.activeProject()) {
          this.activeProject.set(preferred);
        }
      });
    });

    // S5's standard entry is a one-shot shell event, not a derivation from the
    // current route. This distinction preserves the operator's panel posture
    // through all ordinary project, task, Dossier, and Wiki navigation.
    effect(() => {
      const entryRevision = this.projectEntryRevision();
      const context = this.composerContext();
      const openOnEntry = this.uiPreferences.openProjectChatOnEntry();
      untracked(() => {
        if (entryRevision !== this.previousProjectEntryRevision) {
          this.previousProjectEntryRevision = entryRevision;
          this.pendingStandardProjectEntry = true;
        }
        if (!this.pendingStandardProjectEntry || !context) return;
        this.pendingStandardProjectEntry = false;

        const eligibleSurface = context.surface === 'Board'
          || context.surface === 'Overview'
          || context.surface === 'Project Overview';
        if (!eligibleSurface || !context.project || !openOnEntry || this.pinned()) return;
        if (!this.projects().includes(context.project)) return;
        this.setActiveProject(context.project);
        this.show();
      });
    });

  }

  /**
   * MC-2: pin freezes the current navigation context; unpin resumes
   * following navigation. Pinning snapshots the live scope so the badge,
   * context menu, and chat body all stay on it. Unpinning snaps the project back to
   * the host's current preferred project because the preferred-sync effect
   * only fires on an input change — without this nudge an unpinned sheet
   * would keep the frozen project until the operator next navigated.
   */
  togglePin(): void {
    if (this.pinned()) {
      this.pinned.set(false);
      this.pinnedSnapshot.set(null);
      const preferred = this.preferredProject();
      if (preferred && this.projects().includes(preferred)) {
        this.activeProject.set(preferred);
      }
      return;
    }
    this.pinnedSnapshot.set({
      project: this.activeProject(),
      jobId: this.activeJobId(),
      jobTitle: this.activeJobTitle(),
      jobKey: this.activeJobKey(),
      jobState: this.activeJobState(),
      watchPath: this.activeWatchPath(),
    });
    this.pinned.set(true);
  }

  ngOnInit(): void {
    // Slow poll: the chat history only changes when the user sends a
    // message (which we already refresh after) or when something else
    // appends a turn. 30s keeps the UI honest without burning quota.
    this.pollTimer = setVisibleInterval(() => {
      if (this.open() && this.effectiveProject() && !this.loading() && !this.sending()) {
        this.refresh(true);
      }
    }, 30_000);
    // Active and queued managed turns remain visible even while the panel is
    // closed. SignalR refreshes the central history, while this bounded poll
    // is the convergence fallback for the always-present shell indicator.
    this.contextPollTimer = setVisibleInterval(() => {
      this.refreshContextSessions();
    }, 2_000);

    this.maybeSeedDemoEvents();
    this.refreshContextSessions();
  }

  /**
   * `?demoEvents=1` on the URL seeds three sample event cards so the
   * Slice B rendering contract can be reviewed visually and pinned by
   * Playwright before the live data source lands. No-op without the
   * flag, so production callers see an empty events list as designed.
   */
  private maybeSeedDemoEvents(): void {
    if (typeof window === 'undefined') return;
    const params = new URLSearchParams(window.location.search);
    if (params.get('demoEvents') !== '1') return;
    void import('./orchestrator-side-sheet.lazy').then(({ buildDemoEvents }) => {
      this.events.set(buildDemoEvents(Date.now()));
    });
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
    if (this.contextPollTimer != null) clearVisibleInterval(this.contextPollTimer);
    this.contextPollTimer = null;
  }

  show(): void { this.panelState.setOpen(true); }
  hide(): void {
    this.contextMenuOpen.set(false);
    this.panelState.setOpen(false);
  }
  toggleContextMenu(): void {
    this.contextMenuOpen.update(open => !open);
  }
  toggle(): void {
    if (this.open()) {
      this.hide();
    } else {
      this.show();
    }
  }

  setActiveProject(proj: string): void {
    // MC-2: the picker is inert while pinned — the frozen scope wins until
    // the operator unpins.
    if (this.pinned()) return;
    this.selectedContextKey.set(null);
    this.selectedContextNavigationKey.set(null);
    if (proj === this.activeProject()) return;
    this.activeProject.set(proj);
  }

  selectChatContext(contextKey: string): void {
    this.selectedContextNavigationKey.set(this.navigationContextKey());
    this.selectedContextKey.set(contextKey);
    const session = this.contextSessions().find(item => item.contextKey === contextKey);
    const updatedAt = session?.updatedAt ?? new Date().toISOString();
    this.seenContexts.update(seen => ({ ...seen, [contextKey]: updatedAt }));
    this.persistSeenContexts();
    this.contextMenuOpen.set(false);
  }

  /** Open one Task Server-managed context from the workspace Chat History. */
  openManagedContext(contextKey: string): void {
    this.show();
    if (this.contextSessions().some(context => context.contextKey === contextKey)) {
      this.selectChatContext(contextKey);
      return;
    }
    this.jobService.getOrchestratorContextSessions().subscribe({
      next: response => {
        const sessions = response.sessions ?? [];
        this.contextSessions.set(sessions);
        if (sessions.some(context => context.contextKey === contextKey)) {
          this.selectChatContext(contextKey);
        }
      },
    });
  }

  onNavigateToContext(contextKey: string): void {
    this.selectedContextKey.set(null);
    this.selectedContextNavigationKey.set(null);
    this.contextMenuOpen.set(false);
    this.navigateToContext.emit(contextKey);
  }
  private refreshContextSessions(): void {
    if (this.contextSessionsLoading) return;
    this.contextSessionsLoading = true;
    this.jobService.getOrchestratorContextSessions().subscribe({
      next: response => {
        this.contextSessions.set(response.sessions ?? []);
        this.contextSessionsLoading = false;
      },
      error: () => {
        this.contextSessions.set([]);
        this.contextSessionsLoading = false;
      },
    });
  }

  refreshCurrentContext(): void {
    const key = this.contextKey();
    if (!key || this.contextDigestState.refreshing()) return;
    this.contextDigestState.load(key, true);
    if (this.effectiveProject()) this.refreshChat(false, false);
    this.refreshContextSessions();
  }

  private readSeenContexts(): Record<string, string> {
    if (typeof window === 'undefined') return {};
    try { return JSON.parse(window.localStorage?.getItem('atp.chatSwitcher.seen.v1') ?? '{}'); }
    catch { return {}; }
  }
  private persistSeenContexts(): void {
    if (typeof window === 'undefined') return;
    try { window.localStorage?.setItem('atp.chatSwitcher.seen.v1', JSON.stringify(this.seenContexts())); }
    catch { /* optional local read state */ }
  }

  selectProjectTab(proj: string): void {
    this.setActiveProject(proj);
  }

  /**
   * Drag the left-edge splitter to resize the panel. The orchestrator
   * sits on the right of the viewport (flex-direction: row-reverse on
   * .app-shell), so dragging the handle LEFT widens the panel: dx =
   * startX - clientX, newWidth = startW + dx. Width is committed to
   * OrchestratorPanelStateService which persists to localStorage and
   * clamps within [360, min(1100, 96vw)].
   */
  startResize(event: MouseEvent): void {
    event.preventDefault();
    const startX = event.clientX;
    const startW = this.panelWidth();
    const onMove = (e: MouseEvent) => {
      this.panelState.setWidth(startW + (startX - e.clientX));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }


  onOpenSettings(): void {
    this.openSettings.emit();
  }

  toggleNextMessageContext(): void {
    if (this.contextKind() === 'task') return;
    this.contextDismissed.update(dismissed => !dismissed);
  }

  setNextMessageContextIncluded(included: boolean): void {
    if (this.contextKind() === 'task') return;
    this.contextDismissed.set(!included);
  }

  addContextAttachment(source: OrchestratorContextSourceOption): void {
    if (source.reference.projectId !== this.effectiveProject()) return;
    this.contextAttachments.update(current => current.some(item => item.id === source.id)
      ? current
      : [...current, source]);
  }

  removeContextAttachment(id: string): void {
    this.contextAttachments.update(current => current.filter(item => item.id !== id));
  }

  /** The composer's `+` affordance opens the host-owned source picker popover. */
  requestContextAttachment(): void {
    this.contextPicker()?.show();
  }

  /**
   * The library gives every chip the same remove button. Removing the
   * automatic chip means "leave the current tab out of the next message",
   * which project scope honours and task scope deliberately ignores.
   */
  onContextAttachmentRemoved(id: string): void {
    if (id === AUTOMATIC_CONTEXT_ATTACHMENT_ID) {
      this.setNextMessageContextIncluded(false);
      return;
    }
    this.removeContextAttachment(id);
  }

  onOpenVerboseDebug(): void {
    const jobId = this.effectiveJobId();
    const watchPath = this.effectiveWatchPath();
    if (!jobId || !watchPath) return;
    this.openVerboseDebug.emit({ jobId, watchPath, jobTitle: this.effectiveJobTitle() });
  }

  /**
   * MC-2 (Concept §4): read the transcript for the *current navigation
   * context*, not just the project. On a task page the sheet reads the
   * `task:<PROJ>/<KEY>` thread; on the board it reads `project:<PROJ>`, which
   * the backend resolves to the same canonical per-project log the plain
   * project route serves. Callers only reach this method after the shared
   * resolver has produced a valid canonical key.
   */
  private readChat(key: string) {
    return this.jobService.getOrchestratorChatByContext(key);
  }

  refresh(silent = false): void {
    this.refreshChat(silent, true);
  }

  private refreshChat(silent: boolean, reconcileMissingSession: boolean): void {
    const proj = this.effectiveProject();
    if (!proj) return;
    const key = this.contextKey();
    if (!key) {
      this.errorMsg.set('This chat context is unavailable. Return to a project or task, then try again.');
      return;
    }
    if (!silent) this.loading.set(true);
    this.readChat(key).subscribe({
      next: (resp) => {
        if (this.contextKey() !== key) return;
        this.turns.set(resp.turns ?? []);
        this.executionContext.set(resp.executionContext ?? null);
        this.errorMsg.set(null);
        if (reconcileMissingSession
          && !this.contextSessions().some((session) => session.contextKey === key)) {
          this.refreshContextSessions();
        }
        if (!silent) this.loading.set(false);
      },
      error: (err) => {
        if (this.contextKey() !== key) return;
        const message = orchestratorContextErrorMessage(err, 'Failed to load orchestrator chat');
        this.errorMsg.set(message);
        if (!silent) this.loading.set(false);
      }
    });
  }

  /**
   * Slice E: lookup table from inline event card id to the job it was
   * filed for. Populated when a `/bug ...` directive succeeds; consumed
   * by {@link onChatEventAction} to open the detail panel of that job
   * without parsing the rendered markdown back out.
   */
  private readonly bugEventTargets = new Map<string, { jobId: string; watchPath: string }>();

  async onSubmit(event: ChatSubmitEvent): Promise<void> {
    const proj = this.effectiveProject();
    if (!proj) return;
    // Snapshot once so a navigation change during the request cannot split
    // the send and its reconciliation read across two histories.
    const contextKey = this.contextKey();
    if (!contextKey) {
      this.errorMsg.set('This chat context is unavailable. Return to a project or task, then try again.');
      return;
    }
    const capturedAt = new Date();
    const explicitReferenceSnapshot = this.contextAttachments().map(source => ({ ...source.reference }));
    const navigationSnapshot = {
      kind: this.contextKind(),
      jobId: this.effectiveJobId(),
      taskKey: this.effectiveJobKey(),
      jobTitle: this.effectiveJobTitle(),
      jobState: this.effectiveJobState(),
      page: this.pageContext(),
    };
    const contextIncludedSnapshot = this.automaticContextIncluded();
    const text = event.text.trim();
    if (!text) return;

    // Slice E: chat-level slash directive. The parser lives here in the
    // chat host (not a global registry) because the directive borrows
    // the chat's X-Client-Id (via the HttpClient interceptor) and the
    // active-project watch-path lookup, and routes its outcome into the
    // existing `events` stream so the confirmation card appears in the
    // chat at the user's turn position.
    if (text === '/bug' || text.startsWith('/bug ') || text.startsWith('/bug\n')) {
      this.handleBugDirective(text, proj);
      return;
    }

    // Render the user's turn immediately so the chat doesn't sit silent
    // while the orchestrator thinks (Opus replies often take 30-60s).
    const localId = `local:${Date.now()}`;
    const localTurn: OrchestratorChatTurn & { pending?: boolean } = {
      id: localId,
      ts: new Date().toISOString(),
      role: 'user',
      text,
      pending: true,
    };
    this.localTurns.update((curr) => [...curr, localTurn]);
    this.chatActivity.start(contextKey);
    const lazy = await import('./orchestrator-side-sheet.lazy');

    // Task scope is attached to every turn. Project scope is also attached by
    // default, with one explicit one-message exclusion available in the menu.
    const taskScope = navigationSnapshot.kind === 'task';
    const contextPayload = contextIncludedSnapshot
      ? buildChatNavigationContext({
          activeJobId: navigationSnapshot.jobId,
          activeTaskKey: navigationSnapshot.taskKey,
          activeJobTitle: navigationSnapshot.jobTitle,
          activeJobState: navigationSnapshot.jobState,
          pageContext: navigationSnapshot.page,
          now: () => capturedAt,
        })
      : null;

    // MC-2: route the send to the current context thread so a task page's
    // turns accumulate in — and read back from — their own history. A
    // project/board context falls through to the per-project route.
    const sendBody = {
      text,
      navigationContext: contextPayload,
      contextEnvelope: lazy.buildOrchestratorContextEnvelope(
        contextKey,
        contextPayload,
        explicitReferenceSnapshot,
        () => capturedAt,
      ),
      model: this.composerModel.effectiveSelection().model || null,
      thinkingLevel: this.composerModel.effectiveSelection().thinkingLevel,
      selectionSource: this.composerModel.selectionSource(),
    };
    const send$ = this.jobService.sendOrchestratorChatByContext(contextKey, sendBody);
    send$.subscribe({
      next: (response) => {
        const contextStillVisible = this.contextKey() === contextKey;
        if (contextStillVisible && response.executionContext) {
          this.executionContext.set(response.executionContext);
        }
        if (contextStillVisible && !taskScope && !contextIncludedSnapshot) {
          this.contextDismissed.set(false);
        }
        if (contextStillVisible) this.contextAttachments.set([]);
        this.chatActivity.finish(contextKey);
        this.refreshContextSessions();
        // Fetch the server's view of the conversation. While the local turn
        // is still in the list, `suppressLocalDuplicates` hides the matching
        // server user turn so the bubble does not duplicate momentarily.
        this.readChat(contextKey).subscribe({
          next: (resp) => {
            if (this.contextKey() === contextKey) {
              this.turns.set(resp.turns ?? []);
              this.errorMsg.set(null);
              this.localTurns.set([]);
            }
          },
          error: () => {
            // Fallback: clear local turn anyway so the user is not stuck
            // looking at a pending bubble forever.
            if (this.contextKey() === contextKey) this.localTurns.set([]);
          }
        });
      },
      error: (err) => {
        this.chatActivity.finish(contextKey);
        this.refreshContextSessions();
        const message = orchestratorContextErrorMessage(err, 'Failed to send');
        if (this.contextKey() === contextKey) {
          this.localTurns.update((curr) =>
            curr.map((t) => (t.id === localId ? { ...t, pending: false, errorMessage: message } : t))
          );
        }
      }
    });
  }

  /**
   * Slice E: parse `/bug <description>` and create a backlog task via
   * the existing `POST /api/tasks` endpoint. The directive must land in
   * `0-backlog` with `taskType=bug` so it goes through triage instead of
   * skipping straight into `2-ready`. Hashtag patterns at the start of
   * any line in the description are parsed into workspace tag ids.
   */
  private handleBugDirective(text: string, project: string): void {
    void import('./orchestrator-side-sheet.lazy').then(({ handleBugDirective }) => {
      handleBugDirective({
        text,
        project,
        watchPaths: this.watchPaths(),
        jobService: this.jobService,
        appendUser: (id, ts, body) => this.localTurns.update(current => [
          ...current, { id, ts, role: 'user', text: body }
        ]),
        appendEvent: item => this.appendBugEvent(item),
        addTarget: (eventId, jobId, watchPath) =>
          this.bugEventTargets.set(eventId, { jobId, watchPath }),
      });
    });
  }

  private appendBugEvent(ev: ChatEvent): void {
    this.events.update((curr) => [...curr, ev]);
  }

  /**
   * Slice E: route an inline event-card action click. Currently the
   * only consumer is the bug-confirmation card's "Open task" button,
   * which opens the kanban detail panel for the newly-filed job in the
   * same tab via the host's existing `openDetail` flow.
   */
  onChatEventAction(action: { eventId: string }): void {
    const target = this.bugEventTargets.get(action.eventId);
    if (!target) return;
    this.openJobDetail.emit(target);
  }

  hasChatEventAction(eventId: string): boolean {
    return this.bugEventTargets.has(eventId);
  }

}
