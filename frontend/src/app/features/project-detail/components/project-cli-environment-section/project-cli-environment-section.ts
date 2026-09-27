import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { CLI_TYPES, type CliType } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { CliSessionInfo, CliUsageReport, CliUsageSection } from '../../../../features/cli';
import { TooltipDirective } from 'coding-agent-chat/shared';

interface ProjectCliEnvironmentPaths {
  path: string;
  rootPath: string | null;
  repositoryPath: string | null;
}

interface ProjectCliModeRow {
  cliType: string;
  mode: string;
  source: string;
}

interface ProjectCliContextModeRow {
  cliType: string;
  mode: string;
  source: string;
  supported: boolean;
}

interface ProjectCliEnvironmentRow {
  cliType: string;
  label: string;
  icon: string;
  available: boolean;
  version: string | null;
  path: string | null;
  error: string | null;
  sessionCount: number;
  latestSession: CliSessionInfo | null;
  mode: string;
  modeSource: string;
  contextMode: string;
  contextSource: string;
  contextSupported: boolean;
}

type ProjectCliEnvironmentPresentation = 'detail' | 'onboarding';
type ProjectCliOnboardingTone = 'ok' | 'warn' | 'alert' | 'muted' | 'info';

interface ProjectCliOnboardingTile {
  id: string;
  label: string;
  value: string;
  detail: string;
  tone: ProjectCliOnboardingTone;
}

@Component({
  selector: 'app-project-cli-environment-section',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './project-cli-environment-section.html',
  styleUrl: './project-cli-environment-section.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectCliEnvironmentSectionComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly paths = input.required<ProjectCliEnvironmentPaths>();
  readonly modeRows = input<readonly ProjectCliModeRow[]>([]);
  readonly contextModeRows = input<readonly ProjectCliContextModeRow[]>([]);
  readonly presentation = input<ProjectCliEnvironmentPresentation>('detail');

  private readonly jobService = inject(TaskService);

  readonly cliUsageReport = signal<CliUsageReport | null>(null);
  readonly cliUsageLoading = signal(false);
  readonly cliUsageLoaded = signal(false);
  readonly cliUsageError = signal<string | null>(null);

  readonly rows = computed<readonly ProjectCliEnvironmentRow[]>(() => {
    const sections = new Map<string, CliUsageSection>(
      (this.cliUsageReport()?.sections ?? []).map(section => [section.cliType, section]),
    );
    const modes = new Map(this.modeRows().map(row => [row.cliType, row]));
    const contexts = new Map(this.contextModeRows().map(row => [row.cliType, row]));

    return CLI_TYPES.map((cli) => {
      const section = sections.get(cli);
      const sessions = section ? this.projectSessionsFor(section) : [];
      const latestSession = sessions
        .slice()
        .sort((a, b) => this.sessionTime(b) - this.sessionTime(a))[0] ?? null;
      const mode = modes.get(cli);
      const context = contexts.get(cli);

      return {
        cliType: cli,
        label: cliTypeLabel(cli as CliType),
        icon: cliTypeIcon(cli as CliType),
        available: section?.available ?? false,
        version: section?.version ?? null,
        path: section?.path ?? null,
        error: section?.error ?? null,
        sessionCount: sessions.length,
        latestSession,
        mode: mode?.mode ?? 'yolo',
        modeSource: mode?.source ?? 'default',
        contextMode: context?.mode ?? 'clean',
        contextSource: context?.source ?? 'default',
        contextSupported: context?.supported ?? false,
      };
    });
  });

  readonly summary = computed(() => {
    const rows = this.rows();
    return {
      available: rows.filter(row => row.available).length,
      total: rows.length,
      sessions: rows.reduce((sum, row) => sum + row.sessionCount, 0),
    };
  });

  readonly onboardingTiles = computed<readonly ProjectCliOnboardingTile[]>(() => {
    const rows = this.rows();
    const summary = this.summary();
    const unsupported = rows.filter(row => !row.available).length;
    const contextRows = this.contextModeRows();
    const supportedContexts = contextRows.filter(row => row.supported);
    const cleanContexts = supportedContexts.filter(row => row.mode === 'clean').length;
    const overrideCount = this.modeRows().filter(row => this.isProjectSource(row.source)).length
      + contextRows.filter(row => this.isProjectSource(row.source)).length;
    const latest = rows
      .map(row => row.latestSession)
      .filter((session): session is CliSessionInfo => !!session)
      .sort((a, b) => this.sessionTime(b) - this.sessionTime(a))[0] ?? null;

    return [
      {
        id: 'cli-ready',
        label: 'CLI ready',
        value: `${summary.available} / ${summary.total}`,
        detail: unsupported === 0 ? 'all detected' : `${unsupported} need attention`,
        tone: summary.available === summary.total ? 'ok' : summary.available > 0 ? 'warn' : 'alert',
      },
      {
        id: 'clean-context',
        label: 'Clean context',
        value: supportedContexts.length === 0 ? 'n/a' : `${cleanContexts} / ${supportedContexts.length}`,
        detail: supportedContexts.length === 0
          ? 'support unknown'
          : cleanContexts === supportedContexts.length
            ? 'isolated where supported'
            : `${supportedContexts.length - cleanContexts} shared`,
        tone: supportedContexts.length === 0 ? 'muted' : cleanContexts === supportedContexts.length ? 'ok' : 'warn',
      },
      {
        id: 'project-sessions',
        label: 'Project sessions',
        value: `${summary.sessions}`,
        detail: latest?.label || (summary.sessions === 1 ? 'one session found' : summary.sessions > 1 ? 'sessions found' : 'none linked yet'),
        tone: summary.sessions > 0 ? 'info' : 'muted',
      },
      {
        id: 'overrides',
        label: 'Project overrides',
        value: overrideCount === 0 ? 'none' : `${overrideCount}`,
        detail: overrideCount === 0 ? 'inherits defaults' : 'project-specific',
        tone: overrideCount === 0 ? 'muted' : 'info',
      },
    ];
  });

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    if (this.cliUsageLoading()) return;
    this.cliUsageLoading.set(true);
    this.cliUsageError.set(null);
    this.jobService.getCliUsageReport().subscribe({
      next: (report) => {
        this.cliUsageReport.set(report);
        this.cliUsageLoaded.set(true);
        this.cliUsageLoading.set(false);
      },
      error: (err) => {
        this.cliUsageError.set(err?.error?.error || err?.message || 'Failed to load CLI environment.');
        this.cliUsageLoaded.set(true);
        this.cliUsageLoading.set(false);
      },
    });
  }

  statusLabel(row: ProjectCliEnvironmentRow): string {
    if (row.available) return 'Ready';
    return row.error ? 'Issue' : 'Not detected';
  }

  modeLabel(mode: string | null | undefined): string {
    const labels: Record<string, string> = {
      yolo: 'YOLO',
      'workspace-write': 'Workspace-Write',
      'read-only': 'Read-only',
      custom: 'Custom',
    };
    return labels[mode ?? ''] ?? mode ?? '';
  }

  sourceLabel(source: string | null | undefined): string {
    const labels: Record<string, string> = {
      project: 'project override',
      global: 'global config',
      default: 'platform default',
    };
    return labels[source ?? ''] ?? source ?? '';
  }

  contextModeLabel(mode: string | null | undefined): string {
    const labels: Record<string, string> = { clean: 'Clean', shared: 'Shared' };
    return labels[mode ?? ''] ?? mode ?? '';
  }

  latestSessionLabel(row: ProjectCliEnvironmentRow): string {
    const session = row.latestSession;
    if (!session) return 'No project session';
    return session.label || this.shortSessionId(session.id);
  }

  latestSessionTooltip(row: ProjectCliEnvironmentRow): string {
    const session = row.latestSession;
    if (!session) return 'No session found for this project in the native CLI session stores.';
    const parts = [`Session ${session.id}`];
    if (session.cwd) parts.push(`cwd: ${session.cwd}`);
    if (session.linkedJob) parts.push(`task: ${session.linkedJob.title} (${session.linkedJob.lane})`);
    return parts.join(' - ');
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const date = new Date(iso);
      if (Number.isNaN(date.getTime())) return iso;
      return date.toLocaleString('en-US');
    } catch {
      return iso;
    }
  }

  private projectSessionsFor(section: CliUsageSection): CliSessionInfo[] {
    const projectName = this.projectName();
    const pathKeys = this.projectPathKeys();
    const result: CliSessionInfo[] = [];
    for (const group of section.projects ?? []) {
      const groupMatches = this.projectNameMatches(group.projectName, projectName)
        || this.pathMatches(group.rootPath, pathKeys)
        || this.pathMatches(group.projectName, pathKeys);
      for (const session of group.sessions ?? []) {
        if (groupMatches || this.sessionMatchesProject(session, projectName, pathKeys)) {
          result.push(session);
        }
      }
    }
    return result;
  }

  private sessionMatchesProject(session: CliSessionInfo, projectName: string, pathKeys: readonly string[]): boolean {
    const link = session.linkedJob;
    if (link && this.projectNameMatches(link.projectName, projectName)) return true;
    if (link && this.pathMatches(link.watchPath, pathKeys)) return true;
    return this.pathMatches(session.cwd, pathKeys);
  }

  private projectPathKeys(): readonly string[] {
    const paths = this.paths();
    return [paths.path, paths.rootPath, paths.repositoryPath, this.projectName()]
      .filter((value): value is string => typeof value === 'string' && value.trim().length > 0)
      .map(value => this.normalizedPath(value));
  }

  private pathMatches(value: string | null | undefined, normalizedKeys: readonly string[]): boolean {
    const candidate = this.normalizedPath(value);
    if (!candidate) return false;
    return normalizedKeys.some(key => key === candidate || key.endsWith(`/${candidate}`) || candidate.endsWith(`/${key}`));
  }

  private projectNameMatches(value: string | null | undefined, projectName: string): boolean {
    return (value ?? '').trim().toLowerCase() === projectName.trim().toLowerCase();
  }

  private isProjectSource(source: string | null | undefined): boolean {
    return (source ?? '').trim().toLowerCase() === 'project'
      || (source ?? '').trim().toLowerCase() === 'project override';
  }

  private normalizedPath(value: string | null | undefined): string {
    return (value ?? '').trim().replace(/\\/g, '/').replace(/\/+$/g, '').toLowerCase();
  }

  private sessionTime(session: CliSessionInfo): number {
    const raw = session.updatedAt;
    if (!raw) return 0;
    const time = new Date(raw).getTime();
    return Number.isFinite(time) ? time : 0;
  }

  private shortSessionId(id: string): string {
    return id.length <= 12 ? id : `${id.slice(0, 8)}...`;
  }
}
