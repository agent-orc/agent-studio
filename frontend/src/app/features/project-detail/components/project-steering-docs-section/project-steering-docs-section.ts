import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { SteeringDocsService } from '../../../../services/steering-docs.service';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import {
  AgentDocsReadAnalytics,
  AgentDocsReadFile,
  SteeringDocsOverview,
  SteeringDocsSource,
  SteeringDocsWarning,
} from '../../../../models/steering-docs.model';
import { TaskState } from '../../../../models/task.model';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
/**
 * Project-level Steering Docs surface. Shows the agent-facing
 * instruction sources for a watched project (README, AGENTS, ROADMAP,
 * task contract, skills lookup, ADR archive, runtime prompts, project
 * settings), a small heuristic warning set (missing or stale entries,
 * shim files that have grown past their contract), and explicit action
 * buttons that queue normal 1-preparation tasks: summarize, check
 * drift, analyze recurring failures, propose README / AGENTS update,
 * create generic follow-up.
 *
 * V1 keeps this read-only on disk. The service does not summarize or
 * rewrite docs. Drilling into a source opens its raw Markdown inline so
 * the source-of-truth view stays available alongside the human summary
 * actions.
 */

interface StatusBucket {
  label: string;
  count: number;
  cls: 'present' | 'missing';
}

interface SteeringAction {
  slug: string;
  label: string;
  description: string;
}

interface AgentDocsTreeNode {
  name: string;
  title: string;
  relPath: string | null;
  source: SteeringDocsSource | null;
  children: AgentDocsTreeNode[];
}

interface AgentDocsTreeRow {
  node: AgentDocsTreeNode;
  depth: number;
  expanded: boolean;
  hasChildren: boolean;
}

interface ToolUseRow {
  relPath: string;
  reads: number;
  recentReads: number;
  lastRead: string;
  byCli: string;
}

const ACTIONS: SteeringAction[] = [
  { slug: 'summarize', label: 'Summarize Steering Docs', description: 'Spawn a task that reads the inventory below and produces a human summary of what agents are currently told.' },
  { slug: 'check-drift', label: 'Check Docs Drift', description: 'Spawn a task that compares the steering files against current code and flags stale rules or contradictions.' },
  { slug: 'analyze-failures', label: 'Analyze Recurring Job Failures', description: 'Spawn a task that scans recent blocked / needs-input outcomes and proposes steering-doc changes.' },
  { slug: 'propose-readme', label: 'Propose README Update', description: 'Spawn a task that drafts a README change for review, evidence-first.' },
  { slug: 'propose-agents', label: 'Propose AGENTS Update', description: 'Spawn a task that drafts an AGENTS.md change for review, evidence-first.' },
  { slug: 'create-followup', label: 'Create Follow-up Task', description: 'Queue a generic follow-up tied to the steering surface for later scoping.' },
];

@Component({
  selector: 'app-project-steering-docs-section',
  standalone: true,
  imports: [TooltipDirective, AppTooltipDirective, MarkdownViewComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-steering-docs-section.html',
  styleUrl: './project-steering-docs-section.scss'
})
export class ProjectSteeringDocsSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly svc = inject(SteeringDocsService);
  private readonly jobs = inject(TaskService);

  readonly actions = ACTIONS;

  readonly overview = signal<SteeringDocsOverview | null>(null);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);

  readonly readAnalytics = signal<AgentDocsReadAnalytics | null>(null);
  readonly readAnalyticsError = signal<string | null>(null);

  readonly openedRel = signal<string | null>(null);
  readonly fileContent = signal<{ relPath: string; content: string } | null>(null);
  readonly fileLoading = signal<boolean>(false);
  readonly fileError = signal<string | null>(null);
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly navCollapsed = signal<boolean>(false);

  readonly busyAction = signal<string | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  readonly presentCount = computed(() => this.overview()?.sources.length ?? 0);
  readonly warningCount = computed(() => this.overview()?.warnings.length ?? 0);
  readonly sourceTree = computed(() => this.buildTree(this.overview()?.sources ?? []));
  readonly rows = computed(() => this.flattenTree(this.sourceTree(), this.expanded()));
  readonly rootFolderLabel = computed(() => {
    const ov = this.overview();
    if (!ov?.baseDir) return 'project root';
    return ov.baseDir.replace(/\\/g, '/').split('/').filter(Boolean).pop() ?? ov.baseDir;
  });
  readonly selectedSource = computed(() => {
    const rel = this.openedRel();
    if (!rel) return null;
    return this.overview()?.sources.find(s => s.relPath === rel) ?? null;
  });

  readonly statusBuckets = computed<StatusBucket[]>(() => {
    const ov = this.overview();
    if (!ov) return [];
    const buckets: StatusBucket[] = [];
    const high = ov.warnings.filter(w => w.severity === 'high').length;
    const warn = ov.warnings.filter(w => w.severity === 'warn').length;
    const info = ov.warnings.filter(w => w.severity === 'info').length;
    const missing = ov.sources.filter(s => !s.exists).length;
    if (high > 0) buckets.push({ label: 'High', count: high, cls: 'missing' });
    if (warn > 0) buckets.push({ label: 'Warn', count: warn, cls: 'missing' });
    if (info > 0) buckets.push({ label: 'Info', count: info, cls: 'present' });
    if (missing > 0) buckets.push({ label: 'Missing files', count: missing, cls: 'missing' });
    return buckets;
  });

  readonly toolUseRows = computed<ToolUseRow[]>(() => {
    const files = this.readAnalytics()?.files ?? [];
    return files
      .filter(f => f.reads > 0)
      .map((f: AgentDocsReadFile) => ({
        relPath: f.relPath,
        reads: f.reads,
        recentReads: f.recentReads,
        lastRead: this.formatRelative(f.lastReadAt),
        byCli: (f.byCli ?? [])
          .map(c => `${this.cliLabel(c.cli)} ${c.reads}`)
          .join(', ') || '-',
      }));
  });

  private timer?: VisibleIntervalHandle;

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) {
        // Reset cached drilldown state when the project changes.
        this.openedRel.set(null);
        this.fileContent.set(null);
        this.expanded.set(new Set());
        this.readAnalytics.set(null);
        this.readAnalyticsError.set(null);
      }
    });
  }

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(true), 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  refresh(silent = false): void {
    const project = this.projectName();
    if (!project) return;
    if (!silent) this.loading.set(true);
    this.svc.getOverview(project).subscribe({
      next: (ov) => {
        this.overview.set(ov);
        this.loading.set(false);
        this.error.set(null);
        this.expanded.set(new Set(this.collectFolderIds(this.sourceTree())));
        const selected = this.openedRel();
        const selectedStillExists = selected && ov.sources.some(source => source.relPath === selected);
        if (!selectedStillExists && ov.sources.length > 0) {
          this.openSource(ov.sources[0]);
        }
      },
      error: (err) => {
        this.error.set(this.describe(err, 'Steering docs API call failed.'));
        this.loading.set(false);
      },
    });
    this.loadReadAnalytics();
  }

  private loadReadAnalytics(): void {
    const project = this.projectName();
    if (!project) return;
    this.svc.getReadAnalytics(project).subscribe({
      next: (analytics) => {
        this.readAnalytics.set(analytics);
        this.readAnalyticsError.set(null);
      },
      error: (err) => {
        this.readAnalytics.set(null);
        this.readAnalyticsError.set(this.describe(err, 'Could not load read analytics.'));
      },
    });
  }

  openSource(s: SteeringDocsSource): void {
    this.openedRel.set(s.relPath);
    this.fileContent.set(null);
    this.fileError.set(null);
    this.loadFile(s.relPath);
  }

  toggleFolder(id: string): void {
    const current = new Set(this.expanded());
    if (current.has(id)) current.delete(id);
    else current.add(id);
    this.expanded.set(current);
  }

  toggleNav(): void {
    this.navCollapsed.update(v => !v);
  }

  private loadFile(relPath: string): void {
    const project = this.projectName();
    if (!project) return;
    this.fileLoading.set(true);
    this.svc.getFile(project, relPath).subscribe({
      next: (f) => {
        this.fileContent.set(f);
        this.fileLoading.set(false);
      },
      error: (err) => {
        this.fileError.set(this.describe(err, 'Could not load file.'));
        this.fileLoading.set(false);
      },
    });
  }

  onWarningClick(w: SteeringDocsWarning): void {
    if (!w.sourceId) return;
    const ov = this.overview();
    if (!ov) return;
    const src = ov.sources.find(s => s.id === w.sourceId);
    if (!src) return;
    this.openSource(src);
  }

  runAction(action: SteeringAction): void {
    const project = this.projectName();
    if (!project) return;
    this.busyAction.set(action.slug);
    this.actionMessage.set(null);
    this.actionError.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyAction.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `steering-${action.slug}-${Date.now().toString(36)}`;
        const promptMarkdown = this.buildActionPrompt(action);
        this.jobs.createJob({
          id: slug,
          title: action.label,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: TaskState.Preparation,
        }).subscribe({
          next: (resp) => {
            this.busyAction.set(null);
            this.actionMessage.set(`Queued ${resp?.id ?? slug} in 1-preparation. Promote to 2-ready when scoped.`);
          },
          error: (err) => {
            this.busyAction.set(null);
            this.actionError.set(this.describe(err, 'Could not queue steering task.'));
          },
        });
      },
      error: () => {
        this.busyAction.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  private resolveWatchPath(project: string): Observable<string | null> {
    return this.jobs.getWatchPaths().pipe(map(entries => {
      const match = entries.find(e => e.name === project)
        ?? entries.find(e => e.path === project);
      return match?.path ?? null;
    }));
  }

  private buildActionPrompt(action: SteeringAction): string {
    const ov = this.overview();
    const sources = ov?.sources.map(s => `- \`${s.relPath}\` - applies to ${(s.appliesToClis ?? []).join(', ') || 'unknown'} - ${s.label}`).join('\n') ?? '';
    const warnings = (ov?.warnings ?? [])
      .map(w => `- [${w.severity}] ${w.message}`)
      .join('\n');
    return `# ${action.label}

Spawned from the project Steering Docs surface.

## What to do

${action.description}

Produce a Markdown report; attach it to this task's \`status.md\` (and to the
Analysis Reports archive when an entry is appropriate). Do **not** silently
edit the steering documents; propose changes for review instead.

## Steering inventory at queue time

${sources || '_(no sources known)_'}

## Heuristic warnings at queue time

${warnings || '_(no warnings at queue time)_'}
`;
  }

  // ----------------------------------------------------------------------
  // Display helpers
  // ----------------------------------------------------------------------

  formatTime(iso: string | null | undefined): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString('en-US');
    } catch {
      return iso;
    }
  }

  formatSize(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  humanWarningKind(k: string): string {
    switch (k) {
      case 'missingSource': return 'missing source';
      case 'stale': return 'stale';
      case 'possibleConflict': return 'possible conflict';
      case 'recurringFailure': return 'recurring job failure';
      case 'gatewayTooHeavy': return 'gateway warning';
      default: return k;
    }
  }

  cliLabel(cli: string): string {
    switch ((cli ?? '').toLowerCase()) {
      case 'claude': return 'Claude Code';
      case 'codex': return 'Codex';
      case 'gemini': return 'Gemini';
      case 'copilot': return 'Copilot';
      default: return cli || 'Unknown';
    }
  }

  formatRelative(iso: string | null | undefined): string {
    if (!iso) return 'never';
    const then = new Date(iso).getTime();
    if (Number.isNaN(then)) return iso;
    const diffMs = Date.now() - then;
    if (diffMs < 0) return 'just now';
    const mins = Math.floor(diffMs / 60_000);
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    const weeks = Math.floor(days / 7);
    if (weeks < 5) return `${weeks}w ago`;
    return new Date(iso).toLocaleDateString('en-US');
  }

  cliList(clis: readonly string[] | null | undefined): string {
    const list = clis ?? [];
    return list.length ? list.map(cli => this.cliLabel(cli)).join(', ') : 'Unknown';
  }

  rowId(node: AgentDocsTreeNode): string {
    return node.relPath ?? node.title;
  }

  rowPad(depth: number): number {
    return 8 + depth * 14;
  }

  sourceWarnings(source: SteeringDocsSource | null): SteeringDocsWarning[] {
    if (!source) return [];
    return this.overview()?.warnings.filter(w => w.sourceId === source.id) ?? [];
  }

  private buildTree(sources: readonly SteeringDocsSource[]): AgentDocsTreeNode[] {
    const roots: AgentDocsTreeNode[] = [];
    const ensureFolder = (children: AgentDocsTreeNode[], name: string, relPath: string): AgentDocsTreeNode => {
      let existing = children.find(node => node.source === null && node.name === name);
      if (!existing) {
        existing = { name, title: name, relPath, source: null, children: [] };
        children.push(existing);
      }
      return existing;
    };

    for (const source of [...sources].sort((a, b) => a.relPath.localeCompare(b.relPath))) {
      const parts = source.relPath.split('/').filter(Boolean);
      let level = roots;
      let folderRel = '';
      for (let i = 0; i < parts.length - 1; i++) {
        folderRel = folderRel ? `${folderRel}/${parts[i]}` : parts[i];
        level = ensureFolder(level, parts[i], folderRel).children;
      }
      const fileName = parts[parts.length - 1] ?? source.relPath;
      level.push({
        name: fileName,
        title: fileName,
        relPath: source.relPath,
        source,
        children: [],
      });
    }

    const sortNodes = (nodes: AgentDocsTreeNode[]): AgentDocsTreeNode[] => nodes
      .map(node => ({ ...node, children: sortNodes(node.children) }))
      .sort((a, b) => {
        if (!!a.source !== !!b.source) return a.source ? 1 : -1;
        return a.title.localeCompare(b.title);
      });
    return sortNodes(roots);
  }

  private flattenTree(roots: readonly AgentDocsTreeNode[], expanded: ReadonlySet<string>): AgentDocsTreeRow[] {
    const out: AgentDocsTreeRow[] = [];
    const walk = (nodes: readonly AgentDocsTreeNode[], depth: number): void => {
      for (const node of nodes) {
        const id = this.rowId(node);
        const hasChildren = node.children.length > 0;
        const isOpen = !node.source && expanded.has(id);
        out.push({ node, depth, expanded: isOpen, hasChildren });
        if (isOpen) walk(node.children, depth + 1);
      }
    };
    walk(roots, 0);
    return out;
  }

  private collectFolderIds(roots: readonly AgentDocsTreeNode[]): string[] {
    const out: string[] = [];
    const walk = (nodes: readonly AgentDocsTreeNode[]): void => {
      for (const node of nodes) {
        if (!node.source) out.push(this.rowId(node));
        if (node.children.length > 0) walk(node.children);
      }
    };
    walk(roots);
    return out;
  }

  private describe(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }
}
