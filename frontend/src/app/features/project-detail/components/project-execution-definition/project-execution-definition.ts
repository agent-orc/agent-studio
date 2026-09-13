import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';

interface DefinitionIssue {
  path: string;
  code: string;
  message: string;
}

interface CacheManifest {
  block: string;
  key: string;
  state: string;
}

interface PreparationManifest {
  completedAtUtc: string;
  durationMs: number;
  succeeded: boolean;
  subjectSha?: string | null;
  caches: CacheManifest[];
}

interface DefinitionOverride {
  definition: string;
  justification: string;
  updatedAtUtc: string;
}

interface ExecutionDefinitionResponse {
  repositoryDefinition?: string | null;
  definitionSha256?: string | null;
  valid: boolean;
  issues: DefinitionIssue[];
  lastManifest?: PreparationManifest | null;
  override?: DefinitionOverride | null;
  source: string;
}

@Component({
  selector: 'app-project-execution-definition',
  standalone: true,
  imports: [PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-execution-definition.html',
  styleUrl: './project-execution-definition.scss',
})
export class ProjectExecutionDefinitionComponent implements OnInit {
  readonly projectName = input.required<string>();

  private readonly http = inject(HttpClient);
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly proposing = signal(false);
  readonly data = signal<ExecutionDefinitionResponse | null>(null);
  readonly overrideDefinition = signal('');
  readonly justification = signal('');
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.http.get<ExecutionDefinitionResponse>(this.url()).subscribe({
      next: response => {
        this.data.set(response);
        this.overrideDefinition.set(response.override?.definition ?? response.repositoryDefinition ?? '');
        this.justification.set(response.override?.justification ?? '');
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Execution definition could not be loaded.');
        this.loading.set(false);
      },
    });
  }

  saveOverride(): void {
    if (!this.overrideDefinition().trim() || !this.justification().trim()) return;
    this.saving.set(true);
    this.message.set(null);
    this.error.set(null);
    this.http.put<DefinitionOverride>(`${this.url()}/override`, {
      definition: this.overrideDefinition(),
      justification: this.justification(),
    }).subscribe({
      next: saved => {
        this.data.update(current => current ? { ...current, override: saved } : current);
        this.message.set('Justified override saved. Runs still read the repository definition from the subject commit.');
        this.saving.set(false);
      },
      error: response => {
        this.error.set(response?.error?.error ?? 'Override could not be saved.');
        this.saving.set(false);
      },
    });
  }

  clearOverride(): void {
    this.saving.set(true);
    this.message.set(null);
    this.error.set(null);
    this.http.delete(`${this.url()}/override`).subscribe({
      next: () => {
        this.data.update(current => current ? { ...current, override: null } : current);
        this.overrideDefinition.set(this.data()?.repositoryDefinition ?? '');
        this.justification.set('');
        this.message.set('Override cleared.');
        this.saving.set(false);
      },
      error: () => {
        this.error.set('Override could not be cleared.');
        this.saving.set(false);
      },
    });
  }

  createProposal(): void {
    this.proposing.set(true);
    this.message.set(null);
    this.error.set(null);
    this.http.post<{ taskId: string }>(`${this.url()}/proposal`, {}).subscribe({
      next: response => {
        this.message.set(`Proposal card ${response.taskId} is ready for review.`);
        this.proposing.set(false);
      },
      error: response => {
        this.error.set(response?.error?.error ?? 'Proposal card could not be created.');
        this.proposing.set(false);
      },
    });
  }

  cacheIdentity(cache: CacheManifest): string {
    return `${cache.block}:${cache.key}`;
  }

  private url(): string {
    return `/api/projects/${encodeURIComponent(this.projectName())}/execution`;
  }
}
