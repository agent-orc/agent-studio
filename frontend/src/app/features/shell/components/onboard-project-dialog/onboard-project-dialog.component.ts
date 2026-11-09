import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { HttpClient } from '@angular/common/http';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { ProjectBasicsFormComponent } from '../../../../components/project-basics-form';
import type { CliType, RegistryWorkspaceListItem } from '../../../../models/task.model';
import {
  PROJECT_COLOR_SWATCHES,
  projectBasicsAreValid,
  validateProjectBasics,
  type ProjectBasicsValue,
} from '../../../../models/project-basics.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { CliCatalogStore } from '../../../cli';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';
import { RemoteHostsService, codingLocationUnavailableReason, type RemoteHost } from '../../../remote-hosts';

@Component({
  selector: 'app-onboard-project-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent, ProjectBasicsFormComponent, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './onboard-project-dialog.component.html',
  styleUrl: './onboard-project-dialog.component.scss',
})
export class OnboardProjectDialogComponent {
  readonly manager = inject(WorkspaceManagerService);
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly catalog = inject(CliCatalogStore);
  readonly hostRegistry = inject(RemoteHostsService);
  private readonly http = inject(HttpClient);
  readonly localCodingAdmitted = signal(false);

  readonly workspaceId = signal('');
  readonly displayName = signal('');
  readonly shortCode = signal('');
  readonly cliDefault = signal<CliType>('claude');
  readonly modelDefault = signal('');
  readonly color = signal<string>(PROJECT_COLOR_SWATCHES[0]);
  readonly repositoryPath = signal('');
  readonly rootPath = signal('');
  readonly repositoryUrl = signal('');
  readonly executionRunner = signal('local');
  readonly repositoryMode = signal<'existing' | 'create'>('existing');
  readonly repositoryOwner = signal('');
  readonly repositoryName = signal('');
  readonly repositoryVisibility = signal<'public' | 'private'>('private');
  readonly repositoryTemplate = signal<'static-web' | 'container-service'>('static-web');
  readonly submitting = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly registryWorkspaces = signal<readonly RegistryWorkspaceListItem[]>([]);

  private readonly form = viewChild(ProjectBasicsFormComponent);

  readonly currentWorkspaceName = computed(() => {
    const workspace = this.registryWorkspaces().find((item) => item.id === this.workspaceId());
    return (workspace?.displayName ?? this.workspaceId()) || 'Workspace';
  });

  readonly allProjects = computed(() => this.registryWorkspaces().flatMap((workspace) => workspace.projects));
  readonly models = computed(() => this.catalog.modelsFor(this.cliDefault()));
  readonly formValue = computed<ProjectBasicsValue>(() => ({
    workspaceId: this.workspaceId(),
    displayName: this.displayName(),
    shortCode: this.shortCode(),
    color: this.color(),
    repositoryPath: this.repositoryPath(),
    rootPath: this.rootPath(),
    repositoryUrl: this.repositoryUrl(),
    agentOverrideEnabled: true,
    cliDefault: this.cliDefault(),
    modelDefault: this.modelDefault(),
  }));
  readonly canSubmit = computed(() =>
    !this.submitting()
    && this.hostRegistry.hosts().some(host => host.id === this.executionRunner() && !this.runnerUnavailable(host))
    && (this.repositoryMode() === 'existing'
      || (/^[A-Za-z0-9][A-Za-z0-9-]*$/.test(this.repositoryOwner().trim())
        && /^[A-Za-z0-9_.-]{1,100}$/.test(this.repositoryName().trim())))
    && projectBasicsAreValid(validateProjectBasics(this.formValue(), {
      workspaces: this.registryWorkspaces(),
      projects: this.allProjects(),
    })),
  );

  constructor() {
    effect(() => {
      if (!this.manager.onboardProjectOpen()) return;
      this.reset(this.manager.onboardWorkspaceId() ?? '');
      this.localCodingAdmitted.set(false);
      this.tasks.getRegistryWorkspaces({ includeArchived: true }).subscribe({
        next: (list) => {
          this.registryWorkspaces.set(list ?? []);
          if (!this.workspaceId() && list?.length) this.workspaceId.set(list[0].id);
        },
        error: () => this.registryWorkspaces.set([]),
      });
      this.catalog.ensure('claude').subscribe({ error: () => void 0 });
      this.hostRegistry.ensureLoaded();
      this.http.get<{ localCodingAdmitted: boolean }>('/api/projects/execution-locations/policy')
        .subscribe({ next: policy => this.localCodingAdmitted.set(policy.localCodingAdmitted) });
      queueMicrotask(() => this.form()?.focusDisplayName());
    });
    effect(() => {
      const cli = this.cliDefault();
      this.catalog.ensure(cli).subscribe({ error: () => void 0 });
      const first = this.catalog.modelsFor(cli)[0]?.id ?? '';
      if (!this.modelDefault()) this.modelDefault.set(first);
    });
  }

  onCancel(): void {
    if (!this.submitting()) this.manager.closeProjectOnboard();
  }

  runnerUnavailable(host: RemoteHost): boolean {
    return !!this.runnerUnavailableReason(host);
  }

  runnerUnavailableReason(host: RemoteHost): string {
    return codingLocationUnavailableReason(host, this.localCodingAdmitted());
  }

  runnerLabel(host: RemoteHost): string {
    const reason = this.runnerUnavailableReason(host);
    return reason ? `${host.name} (${reason})` : host.name;
  }

  setRepositoryMode(mode: 'existing' | 'create'): void {
    this.repositoryMode.set(mode);
    if (mode === 'create') {
      this.repositoryPath.set('');
      this.rootPath.set('');
      this.repositoryUrl.set('');
    }
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
    const host = this.hostRegistry.hosts().find(candidate => candidate.id === this.executionRunner());
    if (!host || this.runnerUnavailable(host)) {
      this.errorMsg.set('Choose an available coding location.');
      return;
    }
    const value = this.formValue();
    this.submitting.set(true);
    this.errorMsg.set(null);
    this.tasks.createRegistryProject({
      workspaceId: value.workspaceId.trim(),
      displayName: value.displayName.trim(),
      shortCode: value.shortCode.trim().toUpperCase(),
      cliDefault: value.cliDefault,
      modelDefault: value.modelDefault.trim() || null,
      color: value.color,
      repositoryPath: value.repositoryPath.trim() || undefined,
      rootPath: value.rootPath.trim() || undefined,
      repositoryUrl: value.repositoryUrl.trim() || undefined,
      createRepository: this.repositoryMode() === 'create' ? {
        owner: this.repositoryOwner().trim(),
        name: this.repositoryName().trim(),
        visibility: this.repositoryVisibility(),
        template: this.repositoryTemplate(),
      } : undefined,
      executionRunner: this.executionRunner() === 'local' ? undefined : this.executionRunner(),
    }).subscribe({
      next: (project) => {
        this.submitting.set(false);
        this.notifications.success(`Project "${project.displayName}" created.`);
        this.manager.refreshAfterProjectCreate();
      },
      error: (error) => {
        this.submitting.set(false);
        const message = formatError(error);
        this.errorMsg.set(message);
        this.notifications.error(`Could not create project: ${message}`);
      },
    });
  }

  private reset(workspaceId: string): void {
    this.workspaceId.set(workspaceId);
    this.displayName.set('');
    this.shortCode.set('');
    this.cliDefault.set('claude');
    this.modelDefault.set('');
    this.color.set(PROJECT_COLOR_SWATCHES[0]);
    this.repositoryPath.set('');
    this.rootPath.set('');
    this.repositoryUrl.set('');
    this.executionRunner.set('local');
    this.repositoryMode.set('existing');
    this.repositoryOwner.set('');
    this.repositoryName.set('');
    this.repositoryVisibility.set('private');
    this.repositoryTemplate.set('static-web');
    this.errorMsg.set(null);
  }
}

function formatError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { error?: string } | null;
    if (body?.error) return body.error;
    if (error.status === 0) return 'Backend unreachable.';
    return `Create failed (HTTP ${error.status}).`;
  }
  return 'Create failed.';
}
