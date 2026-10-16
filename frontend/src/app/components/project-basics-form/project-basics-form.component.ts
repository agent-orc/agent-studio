import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, model, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CliModelSelectorComponent } from '../cli-model-selector';
import type { CliModelInfo } from '../../features/cli';
import type { CliType, RegistryProjectSummary, RegistryWorkspaceListItem } from '../../models/task.model';
import { AuthService } from '../../services/auth.service';
import {
  PROJECT_COLOR_SWATCHES,
  deriveProjectShortCode,
  normalizeProjectShortCode,
  validateProjectBasics,
  type ProjectBasicsField,
  type ProjectBasicsValue,
} from '../../models/project-basics.model';

@Component({
  selector: 'app-project-basics-form',
  standalone: true,
  imports: [FormsModule, CliModelSelectorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-basics-form.component.html',
  styleUrl: './project-basics-form.component.scss',
})
export class ProjectBasicsFormComponent {
  private readonly auth = inject(AuthService);
  readonly networked = computed(() => this.auth.status()?.profile === 'networked');
  readonly mode = input<'create' | 'edit'>('create');
  readonly testidPrefix = input('project-basics');
  readonly disabled = input(false);
  readonly showValidation = input(false);
  readonly allowAgentInheritance = input(false);
  readonly workspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  readonly projects = input<readonly RegistryProjectSummary[]>([]);
  readonly currentProjectId = input<string | null>(null);
  readonly availableModels = input<readonly CliModelInfo[]>([]);

  readonly workspaceId = model('');
  readonly displayName = model('');
  readonly shortCode = model('');
  readonly color = model<string>(PROJECT_COLOR_SWATCHES[0]);
  readonly repositoryPath = model('');
  readonly rootPath = model('');
  readonly repositoryUrl = model('');
  readonly agentOverrideEnabled = model(true);
  readonly cliDefault = model<CliType>('claude');
  readonly modelDefault = model('');

  readonly swatches = PROJECT_COLOR_SWATCHES;
  private readonly touched = signal<ReadonlySet<ProjectBasicsField>>(new Set());
  private readonly shortCodeEdited = signal(false);
  private readonly displayNameInput = viewChild<ElementRef<HTMLInputElement>>('displayNameInput');

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
    projects: this.projects(),
    currentProjectId: this.currentProjectId(),
  }));

  constructor() {
    effect(() => {
      if (this.mode() === 'create' && !this.displayName() && !this.shortCode()) {
        this.shortCodeEdited.set(false);
        this.touched.set(new Set());
      }
    });
  }

  fieldTestid(field: string): string {
    return `${this.testidPrefix()}-${field}`;
  }

  focusDisplayName(): void {
    this.displayNameInput()?.nativeElement.focus();
  }

  errorFor(field: ProjectBasicsField): string | null {
    if (!this.showValidation() && !this.touched().has(field)) return null;
    return this.validation()[field] ?? null;
  }

  markTouched(field: ProjectBasicsField): void {
    this.touched.update((fields) => new Set([...fields, field]));
  }

  onNameChange(value: string): void {
    this.displayName.set(value);
    if (this.mode() === 'create' && !this.shortCodeEdited()) {
      this.shortCode.set(deriveProjectShortCode(value));
    }
  }

  onCodeChange(value: string): void {
    this.shortCodeEdited.set(true);
    this.shortCode.set(normalizeProjectShortCode(value));
  }

  onAgentCommit(selection: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    this.cliDefault.set(selection.cliType);
    this.modelDefault.set(selection.model);
  }
}
