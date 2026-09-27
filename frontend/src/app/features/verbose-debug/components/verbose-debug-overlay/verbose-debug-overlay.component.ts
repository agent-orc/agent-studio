import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { ModalStackService } from '../../../../services/modal-stack.service';
import type { CliOutputLine, TaskInfo } from '../../../../models/task.model';
import type { RunRecord, RunTimeline } from '../../../../features/run-timeline';
import type { TaskScreenshot } from '../../../../features/screenshots';
import type { TaskTokenSummary } from '../../../../features/tokens';
import { projectConversation } from 'coding-agent-chat/core';
import type {
  ConversationEvent,
  MessageEvent,
  RawLineRange,
  ToolFamily,
  WorkbenchDebugEvent,
} from 'coding-agent-chat/core';
import {
  groupIntoPhases,
  groupIntoSuperPhases,
  type ChatPhase,
  type PhaseInputMessage,
  type SuperPhase,
} from 'coding-agent-chat/composer';
import { formatTokens as fmtTokens } from '../../../../services/format.util';

import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TooltipDirective } from 'coding-agent-chat/shared';
export type VerboseDebugTab =
  | 'overview'
  | 'actors'
  | 'phases'
  | 'orchestrator'
  | 'tools'
  | 'warnings'
  | 'tasks'
  | 'tokens'
  | 'artifacts';

interface ActorRow {
  key: string;
  label: string;
  count: number;
  glyph: string;
}

interface ToolRow {
  family: ToolFamily;
  label: string;
  count: number;
  failures: number;
  percent: number;
}

interface WarningRow {
  key: string;
  label: string;
  count: number;
  tone: 'info' | 'warn' | 'danger';
  description: string;
}

interface TokenRow {
  key: string;
  scope: string;
  inputTokens: number;
  outputTokens: number;
  total: number;
  percent: number;
}

interface ArtifactRow {
  caption: string;
  durablePath: string;
  sourcePath: string;
  url?: string;
  status?: string | null;
  timestamp?: string;
}

const TOOL_LABELS: Record<ToolFamily, string> = {
  read: 'Read',
  search: 'Search',
  command: 'Command',
  edit: 'Edit',
  task: 'Task',
  todo: 'Todo',
  other: 'Other',
};

/**
 * Read-only fullscreen "Verbose Debug" overlay. Surfaces actor activity,
 * orchestrator decisions, supervisor advisories, run timing, tool density,
 * warning density, task markers, token usage, artifacts, raw trace links,
 * and a human explanation derived from the same `ConversationEvent`
 * projection the chat lens uses. The composer is intentionally absent:
 * this is the diagnostic escape hatch, not the default chat surface.
 */
@Component({
  selector: 'app-verbose-debug-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, OverlayPortalDirective],
  templateUrl: './verbose-debug-overlay.component.html',
  styleUrl: './verbose-debug-overlay.component.scss',
})
export class VerboseDebugOverlayComponent {
  readonly lines = input<readonly CliOutputLine[]>([]);
  readonly runTimeline = input<RunTimeline | null>(null);
  readonly screenshots = input<readonly TaskScreenshot[]>([]);
  readonly tokenSummary = input<TaskTokenSummary | null>(null);
  readonly job = input<TaskInfo | null>(null);
  readonly source = input<string>('cli-output.log');
  readonly latestResult = input<string | null>(null);
  readonly initialTab = input<VerboseDebugTab>('overview');

  readonly closeRequest = output<void>();
  readonly openTrace = output<RawLineRange>();

  readonly activeTab = signal<VerboseDebugTab>('overview');

  readonly tabs: { id: VerboseDebugTab; label: string; icon: string }[] = [
    { id: 'overview', label: 'Overview', icon: '📊' },
    { id: 'actors', label: 'Actors', icon: '🎭' },
    { id: 'phases', label: 'Phases', icon: '🧭' },
    { id: 'orchestrator', label: 'Orchestrator', icon: '🛰' },
    { id: 'tools', label: 'Tools', icon: '🛠' },
    { id: 'warnings', label: 'Warnings', icon: '⚠' },
    { id: 'tasks', label: 'Tasks', icon: '🎯' },
    { id: 'tokens', label: 'Tokens', icon: '🪙' },
    { id: 'artifacts', label: 'Artifacts', icon: '🖼' },
  ];

  constructor() {
    queueMicrotask(() => {
      const requested = this.initialTab() as VerboseDebugTab | string;
      const known = this.tabs.some((t) => t.id === requested);
      this.activeTab.set(known ? (requested as VerboseDebugTab) : 'overview');
    });
    // Register with the modal stack so Escape closes this overlay first
    // when it sits above the task detail or another lower overlay.
    inject(ModalStackService).pushUntilDestroyed(
      'verbose-debug-overlay',
      () => this.closeRequest.emit(),
      inject(DestroyRef),
    );
  }

  emitTrace(range: RawLineRange): void {
    this.openTrace.emit(range);
  }

  // --------------------------------------------------------------------
  // Conversation projection (pure derivation from inputs)
  // --------------------------------------------------------------------

  readonly events = computed<ConversationEvent[]>(() => {
    const lines = this.lines();
    if (
      !lines.length &&
      !this.runTimeline() &&
      !this.tokenSummary() &&
      !this.screenshots().length
    ) {
      return [];
    }
    const screenshots = this.screenshots().map((s) => ({
      caption: s.caption || s.fileName,
      sourcePath: s.localPath || s.relativePath,
      durablePath: s.relativePath,
      sourceTool: 'screenshot',
      timestamp: s.timestampUtc,
    }));
    return projectConversation({
      source: this.source(),
      lines: lines as CliOutputLine[],
      task: this.job(),
      runTimeline: this.runTimeline(),
      tokenSummary: this.tokenSummary(),
      screenshots,
      emitRunMarkers: true,
      emitWorkbenchSummary: true,
      emitWorkbenchPreviews: false,
      emitTraceLink: true,
      emitDebugAggregate: true,
      latestResult: this.latestResult() ?? undefined,
    });
  });

  readonly debugEvent = computed<WorkbenchDebugEvent | null>(() => {
    const ev = this.events().find((e): e is WorkbenchDebugEvent => e.kind === 'workbench.debug');
    return ev ?? null;
  });

  // ------------------------------------------------------------------
  // Phases / Super-phases (Phases tab)
  // Derived from the same conversation event stream the chat uses, so
  // the debug surface stays consistent with what the operator sees in
  // the orchestrator side sheet. Filtering to message.* events keeps
  // the grouping aligned with the chat's per-message phase rule.
  // ------------------------------------------------------------------
  readonly phases = computed<ChatPhase[]>(() => {
    const msgs: PhaseInputMessage[] = this.events()
      .filter((e): e is MessageEvent => e.kind.startsWith('message.'))
      .map((e) => ({
        id: e.id,
        ts: e.timestamp,
        author: messageKindToAuthor(e.kind),
      }));
    return groupIntoPhases(msgs);
  });

  readonly superPhases = computed<SuperPhase[]>(() => groupIntoSuperPhases(this.phases()));

  readonly phaseIndexById = computed<ReadonlyMap<string, { supIndex: number; phaseIndexInSup: number }>>(() => {
    const out = new Map<string, { supIndex: number; phaseIndexInSup: number }>();
    this.superPhases().forEach((sup, sIdx) => {
      sup.phases.forEach((p, pIdx) => {
        out.set(p.id, { supIndex: sIdx + 1, phaseIndexInSup: pIdx + 1 });
      });
    });
    return out;
  });

  phaseParticipantsLabel(phase: ChatPhase): string {
    return phase.participants.map((r) => r.label).join(', ') || '—';
  }

  formatPhaseTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    } catch {
      return iso;
    }
  }

  readonly orchestratorDecisions = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'decision.orchestrator' }> =>
        e.kind === 'decision.orchestrator',
    );
  });

  readonly toolBursts = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'toolBurst' }> => e.kind === 'toolBurst',
    );
  });

  readonly supervisorWaits = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'supervisor.wait' }> =>
        e.kind === 'supervisor.wait',
    );
  });

  // Stable arrow function for use from the template (Angular templates can't
  // reference arrow type guards inside filter callbacks otherwise).
  readonly supervisorIsResumed = (w: { state: string }) => w.state === 'resumed';

  // --------------------------------------------------------------------
  // Aggregate getters
  // --------------------------------------------------------------------

  readonly toolDensity = computed(() => {
    const d = this.debugEvent();
    if (!d) return { total: 0, failures: 0, families: {} as Partial<Record<ToolFamily, number>> };
    return d.toolDensity;
  });

  readonly warningCounts = computed(() => {
    const d = this.debugEvent();
    if (!d) {
      return {
        supervisorAdvisories: 0,
        parserWarnings: 0,
        captureFails: 0,
        schemaDrifts: 0,
        needsInputLoops: 0,
        watchdogQuiet: 0,
        watchdogKills: 0,
      };
    }
    return d.warningCounts;
  });

  readonly runStats = computed(() => {
    const d = this.debugEvent();
    return d?.runStats ?? { runCount: 0, completedCount: 0, failedCount: 0, cancelledCount: 0 };
  });

  readonly runs = computed<RunRecord[]>(() => this.runTimeline()?.runs ?? []);

  readonly totalDurationSeconds = computed(() => {
    return this.runs().reduce((acc, r) => acc + (r.durationSeconds ?? 0), 0);
  });

  readonly activeRunBadge = computed(() => {
    return this.runTimeline()?.hasActiveRun ? 'yes' : 'idle';
  });

  readonly totalTokens = computed(() => {
    const t = this.debugEvent()?.tokenTotals;
    if (!t) return 0;
    return t.inputTokens + t.outputTokens + (t.reasoningTokens ?? 0);
  });

  readonly totalWarnings = computed(() => {
    const w = this.warningCounts();
    return (
      w.parserWarnings +
      w.captureFails +
      w.schemaDrifts +
      w.needsInputLoops +
      w.watchdogQuiet +
      w.watchdogKills
    );
  });

  readonly subtitle = computed(() => {
    const j = this.job();
    if (!j) return 'Read-only diagnostic view';
    return `${j.title} · ${j.state}`;
  });

  readonly taskScopeLabel = computed(() => {
    const j = this.job();
    return j ? j.state : '—';
  });

  readonly jobTokenSummary = computed(() => this.tokenSummary());

  // --------------------------------------------------------------------
  // Per-tab row builders
  // --------------------------------------------------------------------

  readonly actorRows = computed<ActorRow[]>(() => {
    const a = this.debugEvent()?.actorCounts;
    if (!a) return [];
    return [
      { key: 'user', label: 'You', count: a.user, glyph: '🧑' },
      { key: 'taskAgent', label: 'Task agent', count: a.taskAgent, glyph: '🤖' },
      { key: 'orchestrator', label: 'Orchestrator', count: a.orchestrator, glyph: '🛰' },
      { key: 'supervisor', label: 'Supervisor', count: a.supervisor, glyph: '🛡' },
      { key: 'supportingAgent', label: 'Supporting agents', count: a.supportingAgent, glyph: '🧰' },
    ].filter((r) => r.count > 0 || r.key === 'user' || r.key === 'taskAgent');
  });

  readonly maxActorCount = computed(() => {
    return Math.max(1, ...this.actorRows().map((r) => r.count));
  });

  readonly toolRows = computed<ToolRow[]>(() => {
    const families = this.toolDensity().families;
    const failureMap = new Map<ToolFamily, number>();
    for (const burst of this.toolBursts()) {
      // Failure counts are not split per-family in the projection; attribute
      // proportionally to a family by keeping the burst-level total visible
      // as the row's failure count when only one family is involved.
      const keys = Object.keys(burst.families) as ToolFamily[];
      if (keys.length === 1 && burst.failures > 0) {
        const fam = keys[0];
        failureMap.set(fam, (failureMap.get(fam) ?? 0) + burst.failures);
      }
    }
    const entries = Object.entries(families) as [ToolFamily, number][];
    const total = entries.reduce((acc, [, n]) => acc + (n ?? 0), 0);
    return entries
      .filter(([, n]) => (n ?? 0) > 0)
      .sort((a, b) => (b[1] ?? 0) - (a[1] ?? 0))
      .map(([family, count]) => ({
        family,
        label: TOOL_LABELS[family] ?? family,
        count: count ?? 0,
        failures: failureMap.get(family) ?? 0,
        percent: total > 0 ? Math.max(4, Math.round(((count ?? 0) / total) * 100)) : 0,
      }));
  });

  readonly warningRows = computed<WarningRow[]>(() => {
    const w = this.warningCounts();
    const tone = (n: number, danger: number, warn: number): 'info' | 'warn' | 'danger' =>
      n >= danger ? 'danger' : n >= warn ? 'warn' : 'info';
    return [
      {
        key: 'parserWarning',
        label: 'Parser warnings',
        count: w.parserWarnings,
        tone: tone(w.parserWarnings, 3, 1),
        description: 'Activity-log parser could not classify a sentinel',
      },
      {
        key: 'captureFail',
        label: 'Session capture-fail',
        count: w.captureFails,
        tone: tone(w.captureFails, 1, 1),
        description: 'CLI session id was not captured; recovery branch fired',
      },
      {
        key: 'schemaDrift',
        label: 'Schema drift',
        count: w.schemaDrifts,
        tone: tone(w.schemaDrifts, 2, 1),
        description: 'Structured Markdown / JSON did not match the expected shape',
      },
      {
        key: 'needsInput',
        label: 'NEEDS_INPUT loops',
        count: w.needsInputLoops,
        tone: tone(w.needsInputLoops, 5, 2),
        description: 'Agent paused for an answer; circuit breaker counts loops',
      },
      {
        key: 'watchdogQuiet',
        label: 'Watchdog quiet windows',
        count: w.watchdogQuiet,
        tone: tone(w.watchdogQuiet, 3, 1),
        description: 'Long quiet stretches noticed by Layer 2 supervisor',
      },
      {
        key: 'watchdogKill',
        label: 'Watchdog kills',
        count: w.watchdogKills,
        tone: tone(w.watchdogKills, 1, 1),
        description: 'Run aborted by the supervisor watchdog',
      },
    ];
  });

  readonly tokenRows = computed<TokenRow[]>(() => {
    const tokenEvents = this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'metric.token' }> => e.kind === 'metric.token',
    );
    const grouped = new Map<string, { input: number; output: number }>();
    for (const e of tokenEvents) {
      const key = e.scope ?? 'unknown';
      const prev = grouped.get(key) ?? { input: 0, output: 0 };
      grouped.set(key, {
        input: prev.input + (e.inputTokens ?? 0),
        output: prev.output + (e.outputTokens ?? 0),
      });
    }
    const t = this.tokenSummary();
    if (t && t.totalTokens > 0) {
      const prev = grouped.get('orchestrator') ?? { input: 0, output: 0 };
      grouped.set('orchestrator', {
        input: prev.input + (t.inputTokens ?? 0),
        output: prev.output + (t.outputTokens ?? 0),
      });
    }
    const rows = Array.from(grouped.entries()).map(([scope, v]) => ({
      key: scope,
      scope: this.scopeLabel(scope),
      inputTokens: v.input,
      outputTokens: v.output,
      total: v.input + v.output,
      percent: 0,
    }));
    const max = rows.reduce((acc, r) => Math.max(acc, r.total), 0);
    return rows
      .filter((r) => r.total > 0)
      .sort((a, b) => b.total - a.total)
      .map((r) => ({
        ...r,
        percent: max > 0 ? Math.max(6, Math.round((r.total / max) * 100)) : 0,
      }));
  });

  readonly artifactRows = computed<ArtifactRow[]>(() => {
    return this.screenshots().map<ArtifactRow>((s) => ({
      caption: s.caption || s.fileName,
      durablePath: s.relativePath,
      sourcePath: s.localPath,
      url: s.url,
      status: s.status,
      timestamp: s.timestampUtc,
    }));
  });

  readonly overviewBands = computed(() => {
    const tools = this.toolDensity().total;
    const tokens = this.totalTokens();
    const warnings = this.totalWarnings();
    const screenshots = this.artifactRows().length;
    const orchestrator = this.orchestratorDecisions().length;
    const max = Math.max(1, tools, tokens / 100, warnings, screenshots, orchestrator);
    const bands = [
      {
        name: 'Tool density',
        value: `${tools} call${tools === 1 ? '' : 's'}`,
        percent: bandPercent(tools, max),
      },
      { name: 'Tokens', value: fmtTokens(tokens), percent: bandPercent(tokens / 100, max) },
      { name: 'Warnings', value: `${warnings}`, percent: bandPercent(warnings, max) },
      { name: 'Artifacts', value: `${screenshots}`, percent: bandPercent(screenshots, max) },
      { name: 'Orchestrator', value: `${orchestrator}`, percent: bandPercent(orchestrator, max) },
    ];
    return bands.filter((b) => b.percent > 0 || b.name === 'Tokens' || b.name === 'Tool density');
  });

  // --------------------------------------------------------------------
  // Tab badges + small helpers
  // --------------------------------------------------------------------

  tabBadge(id: VerboseDebugTab): string | null {
    switch (id) {
      case 'orchestrator': {
        const n = this.orchestratorDecisions().length;
        return n > 0 ? `${n}` : null;
      }
      case 'tools': {
        const n = this.toolDensity().total;
        return n > 0 ? `${n}` : null;
      }
      case 'warnings': {
        const n = this.totalWarnings();
        return n > 0 ? `${n}` : null;
      }
      case 'tasks': {
        const n = this.runs().length;
        return n > 0 ? `${n}` : null;
      }
      case 'artifacts': {
        const n = this.artifactRows().length;
        return n > 0 ? `${n}` : null;
      }
      default:
        return null;
    }
  }

  rowPercent(value: number, max: number): number {
    if (max <= 0) return 0;
    return Math.max(4, Math.round((value / max) * 100));
  }

  formatTokens(n: number): string {
    return fmtTokens(n);
  }

  formatTime(ts: string): string {
    if (!ts) return '';
    try {
      return new Date(ts).toLocaleTimeString('en-US');
    } catch {
      return ts;
    }
  }

  formatDuration(seconds: number): string {
    if (!seconds || seconds <= 0) return '0s';
    if (seconds < 60) return `${Math.round(seconds)}s`;
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    if (m < 60) return s === 0 ? `${m}m` : `${m}m ${s}s`;
    const h = Math.floor(m / 60);
    const min = m % 60;
    return min === 0 ? `${h}h` : `${h}h ${min}m`;
  }

  traceSource(): string {
    return this.source();
  }

  private scopeLabel(scope: string): string {
    switch (scope) {
      case 'task':
        return 'Task agent';
      case 'orchestrator':
        return 'Orchestrator';
      case 'run':
        return 'Latest run';
      case 'project':
        return 'Project';
      case 'supporting-agent':
        return 'Supporting agents';
      default:
        return scope;
    }
  }
}

function bandPercent(value: number, max: number): number {
  if (max <= 0 || value <= 0) return 0;
  return Math.max(4, Math.min(100, Math.round((value / max) * 100)));
}

function messageKindToAuthor(kind: string): string {
  switch (kind) {
    case 'message.user':            return 'user';
    case 'message.taskAgent':       return 'agent';
    case 'message.orchestrator':    return 'orchestrator';
    case 'message.supervisor':      return 'supervisor';
    case 'message.supportingAgent': return 'agent';
    default:                        return 'agent';
  }
}
