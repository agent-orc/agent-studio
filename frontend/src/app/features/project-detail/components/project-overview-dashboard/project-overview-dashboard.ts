import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { TaskInfo, PublishTarget } from '../../../../models/task.model';
import type { ProjectDeploymentSummary, ProjectThroughputSummary } from '../../../../models/project-overview.model';
import type { WikiPulse } from '../../../../models/project-docs.model';
import type { ProjectTokenUsageSummary } from '../../../project-token-usage';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { TaskService } from '../../../../services/task.service';
import { ProjectGitService } from '../../../../services/project-git.service';
import type { GitBranchEntry } from '../../../git';
import { ProjectPublishPanelComponent } from '../project-publish-panel/project-publish-panel';
import { ProjectOverviewUrlsComponent } from '../project-overview-urls/project-overview-urls';
import { ProjectVisualEvidenceQueueComponent } from '../project-visual-evidence-queue/project-visual-evidence-queue';
import type { ProjectRailKey } from '../project-shell/project-shell.config';
import { WikiLiveRefreshService } from '../../services/wiki-live-refresh.service';
import { mergeRecentWikiFeed } from './project-overview-wiki-feed';
import { ReviewStatisticsComponent } from '../../../tokens/components/review-statistics/review-statistics';

/** Operator-first Project Overview. Every block is a compact projection of an
 * existing detail truth; it owns no task, URL, deployment, or Wiki mutation. */
@Component({
  selector: 'app-project-overview-dashboard',
  standalone: true,
  imports: [
    StudioIconComponent,
    ProjectPublishPanelComponent,
    ProjectOverviewUrlsComponent,
    ProjectVisualEvidenceQueueComponent,
    ReviewStatisticsComponent,
  ],
  providers: [WikiLiveRefreshService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-overview-dashboard.html',
  styleUrl: './project-overview-dashboard.scss',
})
export class ProjectOverviewDashboardComponent {
  readonly projectName = input.required<string>();
  readonly openRail = output<ProjectRailKey>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();
  readonly openUrlPreview = output<{ id: string }>();

  private readonly tasks = inject(TaskService);
  private readonly docs = inject(ProjectDocsService);
  private readonly git = inject(ProjectGitService);
  private readonly wikiLiveRefresh = inject(WikiLiveRefreshService);
  private refreshGeneration = 0;

  readonly tokenUsage = signal<ProjectTokenUsageSummary | null>(null);
  readonly throughput = signal<ProjectThroughputSummary | null>(null);
  readonly deployment = signal<ProjectDeploymentSummary | null>(null);
  readonly wiki = signal<WikiPulse | null>(null);
  readonly publishTargets = signal<readonly PublishTarget[]>([]);
  readonly remoteBranches = signal<readonly GitBranchEntry[]>([]);
  readonly loading = signal(true);
  readonly unavailable = signal<ReadonlySet<string>>(new Set());
  readonly evidenceRefreshGeneration = signal(0);

  readonly planningTasks = computed(() => this.tasks.jobs()
    .filter(task => task.projectName === this.projectName())
    .filter(task => (task.mode ?? 'coding') === 'planning')
    .filter(task => task.state !== '6-completed' && task.state !== '7-archive')
    .sort((a, b) => this.activityTime(b) - this.activityTime(a)));

  readonly visiblePlanningTasks = computed(() => this.planningTasks().slice(0, 3));
  readonly planningRemainder = computed(() => Math.max(0, this.planningTasks().length - this.visiblePlanningTasks().length));
  readonly recentWikiItems = computed(() => this.wiki()?.feed.items.slice(0, 3) ?? []);
  readonly pendingCommits = computed(() => this.deployment()?.pendingCommits.slice(0, 4) ?? []);
  readonly pendingCommitRemainder = computed(() => {
    const total = this.deployment()?.pendingCount ?? 0;
    return Math.max(0, total - this.pendingCommits().length);
  });
  readonly deployedCommits = computed(() => this.deployment()?.lastDeployment?.commits.slice(0, 4) ?? []);
  readonly deployedCommitRemainder = computed(() => {
    const total = this.deployment()?.lastDeployment?.commits.length ?? 0;
    return Math.max(0, total - this.deployedCommits().length);
  });
  readonly deploymentState = computed<'pending' | 'current' | 'failed' | 'unknown'>(() => {
    const summary = this.deployment();
    if (!summary?.available || summary.pendingCount == null) return 'unknown';
    const status = summary.lastDeployment?.status.toLowerCase();
    if (status && status !== 'ok') return 'failed';
    return summary.pendingCount > 0 ? 'pending' : 'current';
  });
  readonly deploymentStateLabel = computed(() => {
    switch (this.deploymentState()) {
      case 'failed': return 'Check deployment';
      case 'pending': return 'Action due';
      case 'current': return 'Up to date';
      default: return this.deployment()?.lastDeployment ? 'Delta unavailable' : 'Not configured';
    }
  });
  readonly managedRemoteBranches = computed(() => this.remoteBranches()
    .filter(branch => branch.name === 'main' || branch.name === 'develop' || branch.name.startsWith('task/'))
    .sort((a, b) => a.name.localeCompare(b.name)));
  readonly hasLargeSyncDelta = computed(() => this.managedRemoteBranches()
    .some(branch => branch.ahead > 50 || branch.behind > 50));

  constructor() {
    effect(() => {
      const project = this.projectName();
      this.refresh(project);
      this.wikiLiveRefresh.watchRecentEdits(
        project,
        6,
        edits => {
          const current = this.wiki();
          if (!current) return;
          const next = mergeRecentWikiFeed(current, edits);
          if (next !== current) this.wiki.set(next);
        },
      );
    });
  }

  refresh(project = this.projectName(), forceEvidenceRefresh = false): void {
    if (!project) return;
    if (forceEvidenceRefresh) this.evidenceRefreshGeneration.update(value => value + 1);
    const generation = ++this.refreshGeneration;
    this.loading.set(true);
    this.unavailable.set(new Set());
    this.tokenUsage.set(null);
    this.throughput.set(null);
    this.deployment.set(null);
    this.wiki.set(null);
    this.publishTargets.set([]);
    this.remoteBranches.set([]);
    let pending = 6;
    const done = () => {
      pending--;
      if (pending === 0) this.loading.set(false);
    };
    const fail = (key: string) => {
      if (generation !== this.refreshGeneration) return;
      this.unavailable.update(current => new Set(current).add(key));
      done();
    };
    const accept = (apply: () => void) => {
      if (generation !== this.refreshGeneration) return;
      apply();
      done();
    };

    this.tasks.getProjectTokenUsageSummary(project).subscribe({
      next: value => accept(() => this.tokenUsage.set(value)),
      error: () => fail('tokens'),
    });
    this.tasks.getProjectThroughput(project).subscribe({
      next: value => accept(() => this.throughput.set(value)),
      error: () => fail('throughput'),
    });
    this.tasks.getProjectDeploymentSummary(project).subscribe({
      next: value => accept(() => this.deployment.set(value)),
      error: () => fail('deployment'),
    });
    this.docs.getWikiPulse(project, 6).subscribe({
      next: value => accept(() => this.wiki.set(value)),
      error: () => fail('wiki'),
    });
    this.tasks.getProjectSnapshot(project).subscribe({
      next: value => accept(() => this.publishTargets.set(value.publishTargets ?? [])),
      error: () => fail('publishing'),
    });
    this.git.getInventory(project).subscribe({
      next: value => accept(() => this.remoteBranches.set(value.branches ?? [])),
      error: () => fail('git-remote'),
    });
  }

  openPlanningTask(task: TaskInfo): void {
    this.openTask.emit({ jobId: task.id, watchPath: task.watchPath });
  }

  taskForBranch(branch: GitBranchEntry): TaskInfo | null {
    if (!branch.name.startsWith('task/')) return null;
    const branchTask = branch.name.slice('task/'.length).toLowerCase();
    return this.tasks.jobs().find(task => [task.key, task.taskKey, task.id]
      .filter((value): value is string => Boolean(value))
      .some(value => branchTask === value.toLowerCase() || branchTask.startsWith(`${value.toLowerCase()}-`))) ?? null;
  }

  branchKind(branch: GitBranchEntry): string {
    return branch.category === 'task' ? 'Task branch' : 'Integration branch';
  }

  pushLabel(branch: GitBranchEntry): string {
    if (!branch.upstream) return 'Unavailable';
    return branch.ahead === 0 ? 'Pushed' : `${branch.ahead} to push`;
  }

  pullLabel(branch: GitBranchEntry): string {
    if (!branch.upstream) return 'Unavailable';
    return branch.behind === 0 ? 'Pulled' : `${branch.behind} to pull`;
  }

  formatCompact(value: number | null | undefined): string {
    if (value == null) return 'Not available';
    return new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 }).format(value);
  }

  formatAgo(value: string | null | undefined): string {
    if (!value) return 'No recorded deployment';
    const recordedAt = new Date(value).getTime();
    if (Number.isNaN(recordedAt)) return 'Time unknown';
    const elapsed = Math.max(0, Date.now() - recordedAt);
    const minutes = Math.floor(elapsed / 60_000);
    if (minutes < 60) return `${Math.max(1, minutes)} min ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 48) return `${hours} h ago`;
    const days = Math.floor(hours / 24);
    return `${days} d ago`;
  }

  formatDateTime(value: string | null | undefined): string {
    if (!value) return 'Not recorded';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return value;
    return new Intl.DateTimeFormat('en', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(date);
  }

  trackTask(task: TaskInfo): string { return task.id; }

  private activityTime(task: TaskInfo): number {
    const value = task.lastActivity || task.createdAt;
    return new Date(value).getTime() || 0;
  }
}
