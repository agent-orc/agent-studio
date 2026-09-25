import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, effect, input, output, signal, viewChild, inject } from '@angular/core';
import { DomSanitizer } from '@angular/platform-browser';
import { CliOutputLine } from '../../../../models/task.model';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import {
  ActivityLogGroup,
  ActivityLogKind,
  ConversationTurn,
  LiveStatus,
  activityKindLabel,
  binToolBurstByKind,
  buildConversationTurns,
  deriveLiveStatus,
  formatBurstDuration,
  formatLiveSince,
  parseActivityLog,
  parseOrchestratorSteer
} from '../activity-log.parser';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import {
  RenderedTurn,
  buildToolChips,
  escapeForPlain,
  isDebugNoise,
  roleHeading,
} from './activity-log-view-model';
import { ConversationHistoryWindow } from './activity-log-windowing';

import { StickToBottomDirective, TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import { parseRuntimeSentinels } from '../runtime-sentinel.parser';
import { RuntimeSentinelViewComponent } from '../runtime-sentinel-view/runtime-sentinel-view.component';
type ViewMode = 'conversation' | 'trace';
/**
 * Activity Log view. The component runs in one of two modes:
 *
 * - **Conversation** (default): a chat-like read of the run. Adjacent agent
 *   text turns are joined and rendered as Markdown so the model's reply is
 *   one large readable block instead of N tiny lines. Tool calls between
 *   turns collapse to a single inline pill ("4 actions: 2 reads, 1 edit")
 *   that expands on click. User messages stand out. This is what the user
 *   reads day-to-day.
 *
 * - **Trace**: a flat chronological dump of every parsed group, useful for
 *   debugging - errors, tool detail, system frames. No per-kind filter
 *   checkboxes; a single "Show debug noise" toggle hides the truly spammy
 *   stuff (session/init frames, blank-only groups). The 9-checkbox filter
 *   row from the previous design was replaced because the user reported it
 *   created more friction than value.
 */
@Component({
  selector: 'app-activity-log-view',
  standalone: true,
  imports: [MarkdownViewComponent, StickToBottomDirective, TooltipDirective, MenuComponent, RuntimeSentinelViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './activity-log-view.html',
  styleUrl: './activity-log-view.scss'
})
export class ActivityLogViewComponent implements OnDestroy {
  readonly lines = input<CliOutputLine[]>([]);
  readonly bodyMaxHeight = input('400px');
  readonly variant = input<'framed' | 'embedded'>('framed');
  readonly showToolbar = input(true);
  readonly defaultMode = input<ViewMode>('conversation');
  readonly toolsVisible = input<boolean | null>(null);
  readonly debugVisible = input<boolean | null>(null);
  /**
   * When true the live-status row renders at the bottom of the body
   * (in both Conversation and Trace mode). The row pulses, names what
   * the agent is doing right now, and counts seconds since the last
   * line so the user always sees that the run is alive.
   */
  readonly isRunning = input(false);

  /**
   * Emitted when the user picks a suggested reply from a steer card. The
   * parent (typically the protocol pane) is expected to pre-fill its
   * compose box with the option text so the user can edit and send.
   */
  readonly applyComposeSuggestion = output<string>();
  /**
   * Emitted when the user clicks "Send screenshot" on a steer card whose
   * Need line mentions a screenshot. The parent owns the attachment
   * uploader (it knows the job id) and opens it in response.
   */
  readonly requestUploadScreenshot = output<void>();

  readonly mode = signal<ViewMode>('conversation');
  readonly showTools = signal(false);
  readonly showDebug = signal(false);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  readonly toolbarMenuOpen = signal(false);
  readonly toolbarMenuAnchor = signal<HTMLElement | null>(null);
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  /** Open/closed state for tool bursts (Conversation) and groups (Trace), keyed by id. */
  readonly expandedTurns = signal<Record<string, boolean>>({});
  readonly expandedGroups = signal<Record<string, boolean>>({});
  private readonly historyWindow = new ConversationHistoryWindow();

  readonly parsedGroups = computed(() => parseActivityLog(this.lines()));

  private readonly defaultModeEffect = effect(() => {
    this.mode.set(this.defaultMode());
  });

  readonly conversationTurns = computed<ConversationTurn[]>(
    () => buildConversationTurns(this.parsedGroups())
  );

  /** Memoized presentation for stable turn ids; the DOM only receives the history window. */
  private renderTurnCache = new Map<string, RenderedTurn>();
  readonly conversationWindowSize = this.historyWindow.size;

  readonly filteredConversationTurns = computed<ConversationTurn[]>(() => {
    const showTools = this.toolsEnabled();
    return this.conversationTurns().filter((turn) => showTools || turn.kind !== 'tools');
  });

  readonly visibleConversation = computed<RenderedTurn[]>(() => {
    const all = this.filteredConversationTurns();
    const turns = this.historyWindow.slice(all);
    if (this.renderTurnCache.size > Math.max(turns.length * 4, 100)) {
      this.renderTurnCache.clear();
    }
    return turns.map((turn) => {
      const cached = this.renderTurnCache.get(turn.id);
      if (cached) return cached;
      const rendered = this.renderTurn(turn);
      this.renderTurnCache.set(turn.id, rendered);
      return rendered;
    });
  });

  readonly olderConversationCount = computed(() =>
    this.historyWindow.olderCount(
      this.filteredConversationTurns().length,
      this.visibleConversation().length
    )
  );

  /**
   * Trace feed: every parsed group, optionally filtered for "debug noise" -
   * which currently means session-init markers and groups whose only line is
   * a blank or whitespace-only string. The bar to add a per-kind filter is
   * the same friction the redesign was meant to remove, so we don't.
   */
  readonly visibleTraceGroups = computed<ActivityLogGroup[]>(() => {
    const groups = this.parsedGroups();
    if (this.debugEnabled()) return groups;
    return groups.filter((group) => !isDebugNoise(group));
  });

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private readonly stick = viewChild(StickToBottomDirective);
  readonly stickToBottom = computed(() => this.stick()?.stuck() ?? true);
  private prependFrame: number | null = null;
  private readonly sanitizer = inject(DomSanitizer);

  /**
   * 1 s wall-clock ticker that drives the "since last line" counter on
   * the live-status row. Only ticks while {@link isRunning} is true so
   * idle detail panels do not pay for a setInterval. NowTickService
   * exists but it ticks at 15 s, which is far too coarse for the
   * "agent is alive" feel the user asked for.
   */
  private readonly nowMs = signal(Date.now());
  private liveTicker: ReturnType<typeof setInterval> | null = null;
  private readonly liveTickerEffect = effect(() => {
    if (this.isRunning()) {
      if (!this.liveTicker) {
        this.nowMs.set(Date.now());
        this.liveTicker = setInterval(() => this.nowMs.set(Date.now()), 1000);
      }
    } else if (this.liveTicker) {
      clearInterval(this.liveTicker);
      this.liveTicker = null;
    }
  });

  readonly liveStatus = computed<LiveStatus | null>(() =>
    deriveLiveStatus(this.lines(), this.isRunning(), this.nowMs())
  );

  readonly toolbarMenuItems = computed<readonly MenuItem[]>(() => [
    {
      kind: 'row',
      id: 'trace',
      label: 'Trace',
      active: this.mode() === 'trace',
      disabled: this.lines().length === 0,
    },
    {
      kind: 'row',
      id: 'debug',
      label: 'Debug',
      active: this.mode() === 'conversation' ? this.toolsEnabled() : this.debugEnabled(),
      disabled: this.lines().length === 0,
    },
    { kind: 'separator' },
    {
      kind: 'row',
      id: 'copy',
      label: this.copyMenuLabel(),
      disabled: this.copyDisabled(),
    },
  ]);

  readonly toolsEnabled = computed(() => this.toolsVisible() ?? this.showTools());
  readonly debugEnabled = computed(() => this.debugVisible() ?? this.showDebug());

  /**
   * Keep the oldest rendered turn anchored while detached from Follow. New
   * tail turns expand the window instead of evicting the row the user reads.
   */
  private readonly conversationGrowthEffect = effect(() => {
    const turns = this.filteredConversationTurns();
    const firstKey = turns[0] ? `${turns[0].kind}:${turns[0].timestamp}` : '';
    this.historyWindow.sync(firstKey, turns.length, this.stickToBottom());
  });

  formatSince(ms: number): string {
    return formatLiveSince(ms);
  }

  ngOnDestroy(): void {
    if (this.prependFrame !== null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(this.prependFrame);
      this.prependFrame = null;
    }
    if (this.copyResetTimer !== null) {
      clearTimeout(this.copyResetTimer);
      this.copyResetTimer = null;
    }
    if (this.liveTicker !== null) {
      clearInterval(this.liveTicker);
      this.liveTicker = null;
    }
    this.conversationGrowthEffect.destroy();
    this.liveTickerEffect.destroy();
    this.defaultModeEffect.destroy();
  }

  testIdFor(turn: ConversationTurn): string | null {
    if (turn.kind === 'user') return 'convo-turn-user';
    if (turn.kind === 'agent') return 'convo-turn-agent';
    if (turn.kind === 'tools') return 'convo-turn-tools';
    if (turn.kind === 'system') return 'convo-turn-system';
    if (turn.kind === 'orchestrator') return 'convo-turn-orchestrator';
    return null;
  }

  copyDisabled(): boolean {
    if (this.mode() === 'conversation') return this.visibleConversation().length === 0;
    return this.visibleTraceGroups().length === 0;
  }

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy';
  }

  copyMenuLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return 'Copied';
    if (s === 'failed') return 'Copy Failed';
    return 'Copy';
  }

  copyTooltip(): string {
    return this.mode() === 'conversation'
      ? 'Copy the visible conversation transcript'
      : 'Copy the visible trace';
  }

  async copyVisible(): Promise<void> {
    const text = this.buildCopyText();
    if (!text) return;
    const ok = await copyTextToClipboard(text);
    this.copyState.set(ok ? 'copied' : 'failed');
    if (this.copyResetTimer !== null) clearTimeout(this.copyResetTimer);
    this.copyResetTimer = setTimeout(() => {
      this.copyState.set('idle');
      this.copyResetTimer = null;
    }, 2000);
  }

  openToolbarMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.toolbarMenuAnchor.set(event.currentTarget as HTMLElement);
    this.toolbarMenuOpen.set(true);
  }

  closeToolbarMenu(): void {
    this.toolbarMenuOpen.set(false);
  }

  onToolbarMenuItemClick(ev: MenuItemClickEvent): void {
    switch (ev.id) {
      case 'trace':
        this.mode.set('trace');
        break;
      case 'debug':
        if (this.mode() === 'conversation') {
          if (this.toolsVisible() === null) {
            this.showTools.update(v => !v);
          }
        } else {
          if (this.debugVisible() === null) {
            this.showDebug.update(v => !v);
          }
        }
        break;
      case 'copy':
        void this.copyVisible();
        break;
    }
    this.closeToolbarMenu();
  }

  private buildCopyText(): string {
    if (this.mode() === 'conversation') {
      const parts: string[] = [];
      for (const item of this.visibleConversation()) {
        const head = `[${this.formatTime(item.turn.timestamp)}] ${roleHeading(item.turn.kind)}`;
        if (item.turn.kind === 'tools') {
          const chipText = item.toolChips.map((c) => `${c.label} ×${c.count}`).join(', ');
          const dur = item.toolDuration ? ` (${item.toolDuration})` : '';
          parts.push(`${head} ${chipText}${dur}`);
        } else {
          parts.push(`${head}\n${item.turn.text}`);
        }
      }
      return parts.join('\n\n');
    }
    const parts: string[] = [];
    for (const group of this.visibleTraceGroups()) {
      parts.push(`=== ${activityKindLabel(group.kind)} — ${group.title} ===`);
      if (group.subtitle) parts.push(group.subtitle);
      for (const line of group.lines) {
        parts.push(`[${this.formatTime(line.timestamp)}] ${this.streamLabel(line.stream)} ${line.text}`);
      }
      parts.push('');
    }
    return parts.join('\n').trimEnd();
  }

  loadOlderConversation(): void {
    const el = this.bodyRef()?.nativeElement;
    const remaining = this.olderConversationCount();
    if (!el || remaining === 0) return;
    const beforeHeight = el.scrollHeight;
    const beforeTop = el.scrollTop;
    this.historyWindow.loadOlder(remaining);
    if (typeof requestAnimationFrame === 'undefined') return;
    if (this.prependFrame !== null) cancelAnimationFrame(this.prependFrame);
    this.prependFrame = requestAnimationFrame(() => {
      this.prependFrame = null;
      el.scrollTop = beforeTop + Math.max(0, el.scrollHeight - beforeHeight);
    });
  }

  jumpToBottom(): void {
    this.stick()?.scrollToBottom();
  }

  kindLabel(kind: ActivityLogKind): string {
    return activityKindLabel(kind);
  }

  streamLabel(stream: string): string {
    if (stream === 'stderr') return 'ERR';
    if (stream === 'user') return 'YOU';
    if (stream === 'system') return 'SYS';
    return 'OUT';
  }

  /**
   * The raw stream-json a redacted `[internal event]` line stands in for, or
   * undefined for an ordinary line. Read through the app-side {@link
   * CliOutputLine} shape because the library's `ActivityLogGroup.lines` type
   * (structurally identical) does not declare the host-only `internalDetail`
   * field the projection guard attaches.
   */
  internalDetailOf(line: CliOutputLine): string | undefined {
    return line.internalDetail;
  }

  isExpanded(group: ActivityLogGroup): boolean {
    return this.expandedGroups()[group.id] ?? !group.collapsedByDefault;
  }

  toggleGroup(group: ActivityLogGroup): void {
    const next = !this.isExpanded(group);
    this.expandedGroups.update((m) => ({ ...m, [group.id]: next }));
  }

  isTurnExpanded(turn: ConversationTurn): boolean {
    return this.expandedTurns()[turn.id] ?? false;
  }

  toggleTurn(id: string): void {
    this.expandedTurns.update((m) => ({ ...m, [id]: !m[id] }));
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    });
  }

  private renderTurn(turn: ConversationTurn): RenderedTurn {
    if (turn.kind === 'tools') {
      return {
        turn,
        sentinels: parseRuntimeSentinels(''),
        bodyHtml: null,
        toolChips: buildToolChips(turn),
        toolDuration: formatBurstDuration(turn.toolSummary?.durationMs ?? 0),
        toolBins: binToolBurstByKind(turn.groups)
      };
    }
    if (turn.kind === 'orchestrator') {
      const firstLine = turn.groups[0]?.lines[0];
      const steer = firstLine ? parseOrchestratorSteer(firstLine.text) : null;
      if (steer) {
        return {
          turn,
          sentinels: parseRuntimeSentinels(''),
          bodyHtml: null,
          toolChips: [],
          toolDuration: '',
          toolBins: [],
          steer
        };
      }
    }
    // Agent turns delegate markdown rendering to <cac-markdown> in the
    // template, so bodyHtml stays null for that branch. User/system/
    // orchestrator-non-steer turns remain plain escaped text via the
    // inline [innerHTML] binding.
    if (turn.kind === 'agent') {
      return {
        turn: { ...turn, text: parseRuntimeSentinels(turn.text).text },
        sentinels: parseRuntimeSentinels(turn.text),
        bodyHtml: null,
        toolChips: [],
        toolDuration: '',
        toolBins: [],
      };
    }
    const html = this.sanitizer.bypassSecurityTrustHtml(escapeForPlain(turn.text));
    return {
      turn,
      sentinels: parseRuntimeSentinels(''),
      bodyHtml: html,
      toolChips: [],
      toolDuration: '',
      toolBins: [],
    };
  }

  /**
   * One-letter label for a steer option ("A", "B", "C", ...). Mirrors the
   * grammar the orchestrator emits, so the chat row reads naturally
   * regardless of which marker style the model chose (`A)`, `1)`, `-`).
   */
  steerOptionLabel(index: number): string {
    if (index < 0) return '';
    if (index < 26) return String.fromCharCode('A'.charCodeAt(0) + index);
    return `${index + 1}`;
  }

  onSteerOptionClick(option: string, index: number): void {
    void index;
    if (!option) return;
    this.applyComposeSuggestion.emit(option);
  }

  onSteerUploadClick(): void {
    this.requestUploadScreenshot.emit();
  }
}
