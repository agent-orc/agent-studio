import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
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
import { RemoteHostsService } from '../../../remote-hosts/core-api';

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
    && projectBasicsAreValid(validateProjectBasics(this.formValue(), {
      workspaces: this.registryWorkspaces(),
      projects: this.allProjects(),
    })),
  );

  constructor() {
    effect(() => {
      if (!this.manager.onboardProjectOpen()) return;
      this.reset(this.manager.onboardWorkspaceId() ?? '');
      this.tasks.getRegistryWorkspaces({ includeArchived: true }).subscribe({
        next: (list) => {
          this.registryWorkspaces.set(list ?? []);
          if (!this.workspaceId() && list?.length) this.workspaceId.set(list[0].id);
        },
        error: () => this.registryWorkspaces.set([]),
      });
      this.catalog.ensure('claude').subscribe({ error: () => void 0 });
      this.hostRegistry.ensureLoaded();
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

  runnerUnavailable(host: { status: string }): boolean {
    return host.status === 'offline' || host.status === 'draining';
  }

  runnerLabel(host: { name: string; status: string }): string {
    return this.runnerUnavailable(host) ? `${host.name} (${host.status})` : host.name;
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
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
