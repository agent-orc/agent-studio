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
import type { RegistryProjectUrl } from '../../../../models/task.model';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { ProjectUrlProbeService, ProjectUrlStatusLabelPipe, type ProjectUrlStatus } from '../../../../services/project-url-probe.service';
import { TaskService } from '../../../../services/task.service';

type OverviewUrlStatus = ProjectUrlStatus | 'building';

/** Compact Project Overview projection over the existing registry, URL probe,
 * and start-in-place endpoint used by the full Project URLs panel. */
@Component({
  selector: 'app-project-overview-urls',
  standalone: true,
  imports: [ProjectUrlStatusLabelPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-overview-urls.html',
  styleUrl: './project-overview-urls.scss',
})
export class ProjectOverviewUrlsComponent {
  readonly projectName = input.required<string>();
  readonly openDetails = output<void>();
  readonly openPreview = output<RegistryProjectUrl>();

  private readonly tasks = inject(TaskService);
  private readonly lookup = inject(ProjectLookupService);
  private readonly probe = inject(ProjectUrlProbeService);
  private loadGeneration = 0;

  readonly projectId = signal<string | null>(null);
  readonly urls = signal<readonly RegistryProjectUrl[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  private readonly buildingIds = signal<ReadonlySet<string>>(new Set());

  readonly visibleUrls = computed(() => this.urls().slice(0, 4));
  readonly runningCount = computed(() => this.visibleUrls().filter(url => this.statusFor(url) === 'running').length);
  readonly unknownCount = computed(() => this.visibleUrls().filter(url => this.statusFor(url) === 'unknown').length);
  readonly summaryLabel = computed(() => {
    const unknown = this.unknownCount();
    if (unknown === 0) return 'visible URLs reachable now';
    return `reachable; checking ${unknown} URL${unknown === 1 ? '' : 's'}`;
  });
  readonly hiddenCount = computed(() => Math.max(0, this.urls().length - this.visibleUrls().length));

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  statusFor(url: RegistryProjectUrl): OverviewUrlStatus {
    if (this.buildingIds().has(url.id)) return 'building';
    const projectId = this.projectId();
    return projectId ? this.probe.statusFor(projectId, url.id) : 'unknown';
  }

  start(url: RegistryProjectUrl): void {
    const projectId = this.projectId();
    if (!projectId || !url.startRule || this.buildingIds().has(url.id)) return;
    this.setBuilding(url.id, true);
    this.tasks.startProjectUrl(projectId, url.id).subscribe({
      next: () => {
        setTimeout(() => {
          this.probe.refresh(projectId, url.id);
          this.setBuilding(url.id, false);
        }, 1200);
      },
      error: () => this.setBuilding(url.id, false),
    });
  }

  host(rawUrl: string): string {
    try { return new URL(rawUrl).host; }
    catch { return rawUrl; }
  }

  private load(name: string): void {
    if (!name) return;
    const generation = ++this.loadGeneration;
    this.loading.set(true);
    this.loadError.set(null);
    this.projectId.set(null);
    this.urls.set([]);
    this.buildingIds.set(new Set());
    this.tasks.getRegistryWorkspaces().subscribe({
      next: workspaces => {
        if (generation !== this.loadGeneration) return;
        this.lookup.setWorkspaces(workspaces ?? []);
        const project = (workspaces ?? [])
          .flatMap(workspace => workspace.projects)
          .find(candidate => candidate.displayName === name);
        this.projectId.set(project?.id ?? null);
        this.urls.set([...(project?.urls ?? [])].sort((a, b) => a.sortOrder - b.sortOrder));
        this.loading.set(false);
      },
      error: () => {
        if (generation !== this.loadGeneration) return;
        this.loadError.set('Could not load project URLs.');
        this.loading.set(false);
      },
    });
  }

  private setBuilding(urlId: string, active: boolean): void {
    const next = new Set(this.buildingIds());
    if (active) next.add(urlId); else next.delete(urlId);
    this.buildingIds.set(next);
  }
}
