import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { AuthSessionState } from '../../../services/auth.service';
import { SecretsService, type SecretEnvironment } from '../secrets.service';
import { SecretsPanelComponent } from '../secrets-panel/secrets-panel.component';

interface Binding { name: string; reference: string; approvedBy: string | null }
interface Missing { name: string; reference: string; scope: string; reason: string }
interface Preflight { project: string; environment: string; bindings: Binding[]; missing: Missing[]; ready: boolean }

@Component({
  selector: 'app-project-secrets-page',
  standalone: true,
  imports: [SecretsPanelComponent],
  templateUrl: './project-secrets-page.component.html',
  styleUrl: './project-secrets-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectSecretsPageComponent {
  private readonly http = inject(HttpClient);
  private readonly secrets = inject(SecretsService);
  private readonly auth = inject(AuthSessionState);
  readonly projectName = input.required<string>();
  readonly environment = signal('preview');
  readonly environments = signal<readonly SecretEnvironment[]>([{ id: 'preview', kind: 'preview' }]);
  readonly isProduction = computed(() => this.environments().some(item =>
    item.id === this.environment() && item.kind === 'production'));
  readonly check = signal<Preflight | null>(null);
  readonly name = signal('');
  readonly reference = signal('');
  readonly approve = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly canWrite = () => {
    const status = this.auth.status();
    return status?.profile === 'local' || ['owner', 'operator'].includes(status?.user?.role ?? '');
  };

  constructor() {
    effect(() => {
      const project = this.projectName();
      this.secrets.environments(project).subscribe(items =>
        this.environments.set([{ id: 'preview', kind: 'preview' }, ...items.filter(item => item.id !== 'preview')]));
    });
    effect(() => this.refresh(this.projectName(), this.environment()));
  }

  selectEnvironment(value: string): void {
    this.environment.set(value);
    this.name.set('');
    this.reference.set('');
  }

  selectMissing(item: Missing): void {
    this.name.set(item.name);
    this.reference.set(item.reference);
  }

  refresh(project = this.projectName(), environment = this.environment()): void {
    this.http.get<Preflight>(this.path(project, environment)).subscribe({
      next: check => { this.check.set(check); this.error.set(null); },
      error: error => { this.check.set(null); this.error.set(this.message(error)); },
    });
  }

  bind(): void {
    if (!this.name().trim() || !this.reference().trim() || this.saving()) return;
    this.saving.set(true);
    const environment = this.environment();
    this.http.put<Binding>(`${this.path(this.projectName(), environment)}/${encodeURIComponent(this.name().trim())}`,
      { reference: this.reference().trim(), approve: this.approve() }).subscribe({
      next: () => {
        this.saving.set(false);
        this.name.set('');
        this.reference.set('');
        this.approve.set(false);
        this.refresh();
      },
      error: error => { this.saving.set(false); this.error.set(this.message(error)); },
    });
  }

  private path(project: string, environment: string): string {
    return `/api/projects/${encodeURIComponent(project)}/secret-bindings/${encodeURIComponent(environment)}`;
  }

  private message(error: unknown): string {
    if (error instanceof HttpErrorResponse) return error.error?.message ?? `Request failed (${error.status}).`;
    return 'Request failed.';
  }
}
