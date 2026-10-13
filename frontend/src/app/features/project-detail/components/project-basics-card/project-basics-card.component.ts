import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { catchError, forkJoin, of } from 'rxjs';
import { ProjectBasicsFormComponent } from '../../../../components/project-basics-form';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import {
  PROJECT_COLOR_SWATCHES,
  effectiveProjectRootPath,
  projectBasicsAreValid,
  projectRepositoryUrl,
  validateProjectBasics,
  type ProjectBasicsValue,
} from '../../../../models/project-basics.model';
import { CLI_TYPES, type CliType, type RegistryProjectSummary, type RegistryWorkspaceListItem } from '../../../../models/task.model';
import { CliCatalogStore } from '../../../cli';
import { NotificationService } from '../../../../services/notification.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { TaskService } from '../../../../services/task.service';
// Service-only direct import avoids the Project Hub <-> shell component cycle.
import { WorkspaceManagerService } from '../../../shell/state/workspace-manager.service';
import { ProjectOverlaysService } from '../../state/project-overlays.service';

interface CheckoutStatus {
  status: string;
  branch: string;
  behind: number;
  dirtyFiles: string[];
  lockFile: string | null;
  reason: string | null;
}

@Component({
  selector: 'app-project-basics-card',
  standalone: true,
  imports: [ProjectBasicsFormComponent, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-basics-card.component.html',
  styleUrl: './project-basics-card.component.scss',
})
export class ProjectBasicsCardComponent {
  readonly projectName = input.required<string>();

  private readonly tasks = inject(TaskService);
  private readonly http = inject(HttpClient);
  private readonly catalog = inject(CliCatalogStore);
  private readonly notifications = inject(NotificationService);
  private readonly lookup = inject(ProjectLookupService);
  private readonly manager = inject(WorkspaceManagerService);
  private readonly overlays = inject(ProjectOverlaysService);
  private checkoutRequestVersion = 0;

  readonly workspaces = signal<readonly RegistryWorkspaceListItem[]>([]);
  readonly project = signal<RegistryProjectSummary | null>(null);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly saveError = signal<string | null>(null);
  readonly savedMessage = signal<string | null>(null);
  readonly checkout = signal<CheckoutStatus | null>(null);
  readonly syncingCheckout = signal(false);

  readonly workspaceId = signal('');
  readonly displayName = signal('');
  readonly shortCode = signal('');
  readonly color = signal<string>(PROJECT_COLOR_SWATCHES[0]);
  readonly repositoryPath = signal('');
  readonly rootPath = signal('');
  readonly repositoryUrl = signal('');
  readonly remoteExecutionLocation = signal<string | null>(null);
  readonly agentOverrideEnabled = signal(false);
  readonly cliDefault = signal<CliType>('claude');
  readonly modelDefault = signal('');

  readonly allProjects = computed(() => this.workspaces().flatMap((workspace) => workspace.projects));
  readonly models = computed(() => this.catalog.modelsFor(this.cliDefault()));
  readonly value = computed<ProjectBasicsValue>(() => ({
    workspaceId: this.workspaceId(),
    displayName: this.displayName(),
    shortCode: this.shortCode(),
    color: this.color(),
    repositoryPath: this.repositoryPath(),
    rootPath: this.rootPath(),
    repositoryUrl: this.repositoryUrl(),
    agentOverrideEnabled: this.agentOverrideEnabled(),
    cliDefault: this.cliDefault(),
    modelDefault: this.modelDefault(),
  }));
  readonly validation = computed(() => validateProjectBasics(this.value(), {
    workspaces: this.workspaces(),
    projects: this.allProjects(),
    currentProjectId: this.project()?.id,
  }));
  readonly dirty = computed(() => {
    const project = this.project();
    if (!project) return false;
    const value = this.value();
    return value.workspaceId.trim() !== project.workspaceId
      || value.displayName.trim() !== project.displayName
      || value.shortCode.trim().toUpperCase() !== project.shortCode
      || value.color !== (project.color ?? PROJECT_COLOR_SWATCHES[0])
      || value.repositoryPath.trim() !== (project.repositoryPath ?? '')
      || value.rootPath.trim() !== (project.rootPath ?? '')
      || value.repositoryUrl.trim() !== projectRepositoryUrl(project)
      || value.agentOverrideEnabled !== !!project.cliDefault
      || (value.agentOverrideEnabled && value.cliDefault !== project.cliDefault)
      || (value.agentOverrideEnabled && value.modelDefault.trim() !== (project.modelDefault ?? ''));
  });
  readonly canSave = computed(() =>
    !this.loading() && !this.saving() && this.dirty() && projectBasicsAreValid(this.validation()),
  );
  readonly remoteRepositoryWarning = computed(() =>
    !!this.remoteExecutionLocation()
    && this.remoteExecutionLocation()!.toLowerCase() !== 'local'
    && !this.repositoryUrl().trim(),
  );

  constructor() {
    effect(() => this.load(this.projectName()));
    effect(() => {
      const cli = this.cliDefault();
      this.catalog.ensure(cli).subscribe({ error: () => void 0 });
    });
  }

  syncCheckout(): void {
    const name = this.project()?.displayName;
    if (!name || this.syncingCheckout()) return;
    this.syncingCheckout.set(true);
    const version = ++this.checkoutRequestVersion;
    this.http.post<CheckoutStatus>(`/api/projects/${encodeURIComponent(name)}/checkout/sync`, {}).subscribe({
      next: (status) => {
        if (version === this.checkoutRequestVersion) this.checkout.set(status);
        this.syncingCheckout.set(false);
      },
      error: () => {
        if (version === this.checkoutRequestVersion)
          this.checkout.set({ status: 'error', branch: this.checkout()?.branch ?? '', behind: 0,
            dirtyFiles: [], lockFile: null, reason: 'Could not sync the checkout.' });
        this.syncingCheckout.set(false);
      },
    });
  }

  private loadCheckout(name: string): void {
    this.checkout.set(null);
    const version = ++this.checkoutRequestVersion;
    this.http.get<CheckoutStatus>(`/api/projects/${encodeURIComponent(name)}/checkout/status`).subscribe({
      next: (status) => {
        if (version === this.checkoutRequestVersion) this.checkout.set(status);
      },
      error: () => void 0,
    });
  }

  save(): void {
    const project = this.project();
    if (!project || !this.canSave()) return;
    const value = this.value();
    const effectiveRootPath = effectiveProjectRootPath(value.rootPath, value.repositoryPath);
    const renamed = value.displayName.trim() !== project.displayName;
    const pathsChanged = value.repositoryPath.trim() !== (project.repositoryPath ?? '')
      || effectiveRootPath !== (project.rootPath ?? '');
    this.saving.set(true);
    this.saveError.set(null);
    this.savedMessage.set(null);
    this.tasks.updateRegistryProject(project.id, {
      workspaceId: value.workspaceId.trim(),
      displayName: value.displayName.trim(),
      shortCode: value.shortCode.trim().toUpperCase(),
      color: value.color,
      repositoryPath: value.repositoryPath.trim() || undefined,
      clearRepositoryPath: !value.repositoryPath.trim(),
      rootPath: effectiveRootPath || undefined,
      clearRootPath: !effectiveRootPath,
      repositoryUrl: value.repositoryUrl.trim() || undefined,
      clearRepositoryUrl: !value.repositoryUrl.trim(),
      cliDefault: value.agentOverrideEnabled ? value.cliDefault : undefined,
      clearCliDefault: !value.agentOverrideEnabled,
      modelDefault: value.agentOverrideEnabled && value.modelDefault.trim() ? value.modelDefault.trim() : undefined,
      clearModelDefault: !value.agentOverrideEnabled || !value.modelDefault.trim(),
    }).subscribe({
      next: (updated) => {
        this.saving.set(false);
        this.seed(updated);
        this.savedMessage.set(renamed || pathsChanged
          ? 'Saved. Restart the backend before local auto-pickup uses identity or path changes.'
          : 'Project basics saved.');
        this.notifications.success('Project basics saved.');
        if (renamed) {
          this.manager.notifyProjectRenamed(project.displayName, updated.displayName);
          this.overlays.renameOpenProjectShell(updated.displayName);
        } else {
          this.manager.notifyRegistryChanged();
        }
        this.refreshRegistry();
      },
      error: (error) => {
        this.saving.set(false);
        this.saveError.set(formatUpdateError(error));
      },
    });
  }

  private load(projectName: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    forkJoin({
      workspaces: this.tasks.getRegistryWorkspaces({ includeArchived: true }),
      settings: this.tasks.getAllProjectSettings().pipe(catchError(() => of({}))),
    }).subscribe({
      next: ({ workspaces, settings }) => {
        this.workspaces.set(workspaces ?? []);
        this.lookup.setWorkspaces(workspaces ?? []);
        const display = this.lookup.getProjectDisplay(projectName);
        const project = (workspaces ?? []).flatMap((workspace) => workspace.projects).find((item) =>
          item.id === display.id || item.displayName.toLowerCase() === projectName.toLowerCase(),
        );
        if (!project) {
          this.loading.set(false);
          this.loadError.set('Project basics could not be resolved from the registry.');
          return;
        }
        const projectSettings = Object.entries(settings).find(([name]) =>
          name.toLowerCase() === project.displayName.toLowerCase(),
        )?.[1];
        this.remoteExecutionLocation.set(projectSettings?.executionLocation ?? null);
        this.seed(project);
        this.loadCheckout(project.displayName);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Project basics could not be loaded.');
      },
    });
  }

  private refreshRegistry(): void {
    this.tasks.getRegistryWorkspaces({ includeArchived: true }).subscribe({
      next: (workspaces) => {
        this.workspaces.set(workspaces ?? []);
        this.lookup.setWorkspaces(workspaces ?? []);
      },
      error: () => void 0,
    });
  }

  private seed(project: RegistryProjectSummary): void {
    this.project.set(project);
    this.workspaceId.set(project.workspaceId);
    this.displayName.set(project.displayName);
    this.shortCode.set(project.shortCode);
    this.color.set(project.color ?? PROJECT_COLOR_SWATCHES[0]);
    this.repositoryPath.set(project.repositoryPath ?? '');
    this.rootPath.set(project.rootPath ?? '');
    this.repositoryUrl.set(projectRepositoryUrl(project));
    const cli = project.cliDefault && (CLI_TYPES as readonly string[]).includes(project.cliDefault)
      ? project.cliDefault as CliType
      : 'claude';
    this.agentOverrideEnabled.set(!!project.cliDefault);
    this.cliDefault.set(cli);
    this.modelDefault.set(project.modelDefault ?? '');
  }
}

function formatUpdateError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { error?: string } | null;
    if (body?.error) return body.error;
    if (error.status === 0) return 'Backend unreachable. No changes were saved.';
    return `Save failed (HTTP ${error.status}).`;
  }
  return 'Project basics could not be saved.';
}
