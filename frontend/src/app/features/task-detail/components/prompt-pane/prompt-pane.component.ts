import { ChangeDetectionStrategy, Component, ViewChild, computed, effect, inject, input, output, signal } from '@angular/core';
import { LayoutPanesService } from '../../services/layout-panes.service';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { TaskArtifact, TaskArtifactKind, TaskInfo, TaskPromptHistoryEntry, TaskTitleHistoryEntry, ReviewEvidenceEntry, ReviewEvidenceSource, TaskState } from '../../../../models/task.model';
import type { CliType } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../cli';
import type { TaskScreenshot } from '../../../screenshots/models/screenshots.model';
import { ScreenshotStripComponent } from '../../../screenshots/components/screenshot-strip/screenshot-strip.component';
import { ReviewEvidencePanelComponent } from '../protocol-pane/review-evidence-panel/review-evidence-panel.component';
import { CodeReviewPanelComponent } from '../protocol-pane/code-review-panel/code-review-panel.component';
import { resolveProtocolImageSrc } from '../protocol-pane/protocol-image-resolver';
import { PaneHeaderComponent } from '../../../../components/pane-header/pane-header.component';
import { PaneTabsComponent, PaneTabDef } from '../../../../components/pane-tabs/pane-tabs.component';
import { FilesPaneComponent } from './files-pane/files-pane.component';
import { OverviewPaneComponent } from './overview-pane/overview-pane.component';
import { TaskTimelinePaneComponent } from '../../../task-timeline/components/task-timeline-pane/task-timeline-pane.component';
import type { ProtocolVerdict } from '../protocol-pane/protocol-verdict';
import { TaskArtifactLinksDirective } from '../task-artifact-links/task-artifact-links.directive';
import { TestEvidenceStatusComponent, visibleTestEvidence } from '../../../test-evidence';
import { DetailKeyboardSurfaceDirective } from '../detail-keyboard-surface/detail-keyboard-surface.directive';

/** Display-grouping for the Evidence tab, modeled after the reference layout. */
interface EvidenceSection {
  key: ReviewEvidenceSource;
  label: string;
  /** Optional brand-accent token; falls back to `--studio-accent`. */
  accent: 'var(--accent-2)' | 'var(--accent-3)' | 'var(--accent-4)' | 'var(--studio-accent)' | 'var(--accent-warn)';
  entries: ReviewEvidenceEntry[];
}

export type PromptPaneTabId = 'overview' | 'description' | 'timeline' | 'evidence' | 'code-review';

export function nextPromptPaneTabForJobSwitch(
  activeTab: PromptPaneTabId,
  previousJobKey: string | null,
  nextJobKey: string | null,
): PromptPaneTabId {
  return previousJobKey !== null && nextJobKey !== null && nextJobKey !== previousJobKey
    ? 'overview'
    : activeTab;
}

export function buildPromptTabs(filesCount: number, visualEvidenceCount: number): readonly PaneTabDef[] {
  return [
    {
      id: 'overview',
      label: 'Overview',
      icon: 'layout',
      testid: 'prompt-tab-overview',
    },
    {
      id: 'timeline',
      label: 'Timeline',
      icon: 'activity',
      testid: 'prompt-tab-timeline',
    },
    {
      id: 'evidence',
      label: 'Evidence',
      icon: 'check',
      testid: 'prompt-tab-evidence',
      badge: visualEvidenceCount > 0 ? visualEvidenceCount : null,
    },
    {
      id: 'code-review',
      label: 'Code Review',
      icon: 'diff',
      testid: 'prompt-tab-code-review',
    },
    {
      id: 'description',
      label: 'Docs',
      icon: 'folder',
      testid: 'prompt-tab-description',
      badge: filesCount > 1 ? filesCount : null,
    },
  ];
}

/**
 * Prompt pane of the job-detail view. Per the reference layout
 * (.reference-layout/detail.jsx) the left detail pane carries a small
 * Description / Evidence tab strip — Description renders prompt.md (via
 * the shared markdown rich editor), Evidence renders the same
 * review-evidence entries the protocol pane consumes, grouped by
 * source (Code Review / Security / Task Checks / Human Notes / Other)
 * with status pills per section. This matches the user's direction
 * that Code Review belongs in the Evidence tab rather than in the
 * Protocol pane.
 */
@Component({
  selector: 'app-prompt-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FilesPaneComponent, MarkdownViewComponent, OverviewPaneComponent, TaskTimelinePaneComponent, PaneHeaderComponent, PaneTabsComponent, ScreenshotStripComponent, ReviewEvidencePanelComponent, CodeReviewPanelComponent, TestEvidenceStatusComponent, TaskArtifactLinksDirective, DetailKeyboardSurfaceDirective],
  templateUrl: './prompt-pane.component.html',
  styleUrls: ['./prompt-pane.component.scss']
})
export class PromptPaneComponent {
  @ViewChild(DetailKeyboardSurfaceDirective)
  private keyboardSurface?: DetailKeyboardSurfaceDirective;

  readonly markdown = input<string>('');
  readonly history = input<TaskPromptHistoryEntry[]>([]);
  readonly titleHistory = input<TaskTitleHistoryEntry[]>([]);
  readonly reviewEvidence = input<ReviewEvidenceEntry[]>([]);
  readonly screenshots = input<TaskScreenshot[]>([]);
  readonly artifacts = input<TaskArtifact[]>([]);
  readonly job = input<TaskInfo | null>(null);
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly availableModels = input<readonly CliModelInfo[]>([]);
  /** Optimistic CLI + model values forwarded to the Overview tab's badge
   *  so it reflects a freshly-committed change without waiting on the
   *  parent's detail re-fetch. See [[OverviewPaneComponent]] and ADR-0046. */
  readonly cliTypeOverride = input<CliType | null | undefined>(undefined);
  readonly modelOverride = input<string | null | undefined>(undefined);
  readonly thinkingLevelOverride = input<string | null | undefined>(undefined);
  /** Canonical route state supplied by the task-detail host. */
  readonly routeTab = input<PromptPaneTabId | null>(null);
  readonly runOutcome = input<ProtocolVerdict | null>(null);

  readonly artifactLinkContext = computed(() => ({
    jobId: this.jobId(),
    watchPath: this.watchPath(),
  }));

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();
  readonly evidenceAcknowledge = output<{ entry: ReviewEvidenceEntry; acknowledged: boolean }>();
  readonly evidenceCreateFollowup = output<ReviewEvidenceEntry>();
  /** Atomic CLI + model commit from the Overview tab's unified selector.
   *  Forwarded to the parent task-detail which issues both PUTs in sequence. */
  readonly agentConfigCommit = output<{ cliType: CliType; model: string; thinkingLevel: string | null }>();
  /** Forwarded from the Overview tab's compact References section after a
   *  successful reference write so the parent can re-fetch the detail. */
  readonly referencesChanged = output<void>();
  /** Forwarded from the Overview tab's inline title-edit so the parent
   *  task-detail can re-fetch the job and let the optimistic override
   *  drop. */
  readonly titleSaved = output<void>();
  readonly activeTabChange = output<PromptPaneTabId>();

  /**
   * overview | description | timeline | evidence | code-review.
   *
   * The active tab is reset to Overview on an ordinary task switch. The Studio
   * shell may then apply canonical route state, which makes a copied URL or
   * reload restore the requested tab after the task has mounted.
   */
  readonly activeTab = signal<PromptPaneTabId>('overview');
  readonly docFocusRequest = signal<{ kind: TaskArtifactKind; requestId: number } | null>(null);

  /** Resets the active tab to Overview whenever the underlying task changes,
   *  so navigating between tasks always lands on Overview. Within the same
   *  task (refreshes, status updates) the previously-selected tab persists. */
  private lastJobKey: string | null = null;
  private resetTabOnJobSwitch = effect(() => {
    const key = this.job()?.taskKey ?? null;
    const nextTab = nextPromptPaneTabForJobSwitch(this.activeTab(), this.lastJobKey, key);
    if (nextTab !== this.activeTab()) {
      this.activeTab.set(nextTab);
    }
    this.lastJobKey = key;
  });

  private applyRouteTab = effect(() => {
    const routeTab = this.routeTab();
    if (routeTab && this.activeTab() !== routeTab) {
      this.activeTab.set(routeTab);
    }
  });

  setTab(tab: PromptPaneTabId): void {
    this.activeTab.set(tab);
    this.activeTabChange.emit(tab);
  }

  /**
   * Consume a cross-pane tab request (e.g. the git pane's commit-row
   * code-review badge, AGT-1995). Reading the shared layout signal here
   * rather than taking an imperative call means a request raised while this
   * pane was hidden is still honoured: the pane re-renders on reveal and
   * this effect picks the pending request up on creation, then clears it.
   */
  private readonly layout = inject(LayoutPanesService);
  private readonly requestedTabEffect = effect(() => {
    const requested = this.layout.requestedPromptTab();
    if (!requested) return;
    this.onPromptTabChange(requested);
    const anchor = this.layout.requestedPromptAnchor();
    if (anchor) this.docFocusRequest.set(anchor as { kind: TaskArtifactKind; requestId: number });
    this.layout.requestedPromptTab.set(null);
    this.layout.requestedPromptAnchor.set(null);
  });

  /** Type-safe bridge from the generic pane-tabs `tabChange` event. */
  onPromptTabChange(id: string): void {
    if (id === 'overview' || id === 'description' || id === 'timeline' || id === 'evidence' || id === 'code-review') {
      this.setTab(id);
      this.focusTabSurface();
    }
  }

  focusTabSurface(): void {
    this.keyboardSurface?.focus();
  }

  /** Total visual-evidence count for the Evidence-tab badge. */
  readonly visualEvidenceCount = computed(() => this.screenshots().length);
  readonly testEvidence = computed(() => {
    const job = this.job();
    return job ? visibleTestEvidence(job) : null;
  });

  /** Total document count for the Docs-tab badge (only shown when > 1). */
  readonly filesCount = computed(() => this.artifacts().length);

  /**
   * Tab definitions for the shared {@link PaneTabsComponent}.
   * NB: the `description` id is preserved (with its `prompt-tab-description`
   * testid) for backward-compat with pre-F48 specs; the user-facing label
   * is now "Docs" since the tab leads with readable outcomes while preserving
   * access to every supported source file in the job folder.
   */
  readonly promptTabs = computed<readonly PaneTabDef[]>(() =>
    buildPromptTabs(this.filesCount(), this.visualEvidenceCount()));

  /** Evidence entries grouped into the reference's Evidence-tab sections. */
  readonly evidenceSections = computed<EvidenceSection[]>(() => {
    const entries = this.reviewEvidence();
    const sections: EvidenceSection[] = [
      { key: 'code-review',    label: 'Code Review',  accent: 'var(--accent-3)', entries: [] },
      { key: 'security-audit', label: 'Security',     accent: 'var(--accent-warn)', entries: [] },
      { key: 'task-check',     label: 'Task Checks',  accent: 'var(--accent-2)', entries: [] },
      { key: 'human-note',     label: 'Human Notes',  accent: 'var(--accent-4)', entries: [] },
      { key: 'other',          label: 'Other',        accent: 'var(--studio-accent)', entries: [] },
    ];
    const byKey = new Map(sections.map(s => [s.key, s]));
    for (const e of entries) byKey.get(e.source)?.entries.push(e);
    return sections.filter(s => s.entries.length > 0);
  });

  /** Maps severity to its semantic treatment class. */
  severityClass(sev: ReviewEvidenceEntry['severity']): string {
    return sev === 'high' ? 'pass-fail' : sev === 'warn' ? 'pass-defer' : 'pass-info';
  }

  /** Resolver factory for prompt-history image refs (`attachments/foo.png` ->
   *  job-folder API URL). Stable identity per render so `<cac-markdown>`'s
   *  signal doesn't churn unnecessarily. */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return (src: string) => resolveProtocolImageSrc(src, jobId, watchPath);
  });

  formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }

  /** "created Xh ago" / "Xm ago" / "Xd ago" relative-time rendering for
   *  the description meta strip. */
  formatRelativeTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const diffMs = Date.now() - d.getTime();
    const minutes = Math.round(diffMs / 60_000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    const months = Math.round(days / 30);
    if (months < 12) return `${months}mo ago`;
    return `${Math.round(months / 12)}y ago`;
  }

  /** Human label for a kanban-state slug. Slugs match
   *  STATE_TO_LANE in job.service.ts. */
  laneLabel(state: string): string {
    switch (state) {
      case TaskState.Backlog:          return 'Backlog';
      case TaskState.Preparation:      return 'In Preparation';
      case TaskState.OrchestratorPrep: return 'Orchestrator Prep';
      case TaskState.Ready:            return 'Ready';
      case TaskState.Progress:         return 'In Progress';
      case TaskState.AutoReview:       return 'Post Processing';
      case TaskState.HumanReview:      return 'Review';
      case TaskState.Escalated:        return 'Escalated';
      case TaskState.Completed:        return 'Delivered';
      case TaskState.Archive:          return 'Archive';
      default:                     return state ?? '';
    }
  }
}
