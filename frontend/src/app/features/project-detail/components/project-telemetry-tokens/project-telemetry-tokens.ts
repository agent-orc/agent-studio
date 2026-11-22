import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { take } from 'rxjs';
import { AuthSessionState } from '../../../../services/auth.service';
import { SecretsService, type SecretEnvironment } from '../../../secrets/secrets.service';
import {
  DEPLOY_TOKEN_CREATOR, exporterSettings, tokenState,
  type TelemetryToken, type TelemetryTokenCreated,
} from './project-telemetry-tokens.model';

/**
 * Ingest tokens of one project environment (M5): create a token whose plaintext
 * is shown once, list tokens with their last use, and revoke them.
 */
@Component({
  selector: 'app-project-telemetry-tokens',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './project-telemetry-tokens.html',
  styleUrl: './project-telemetry-tokens.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectTelemetryTokensComponent {
  private readonly http = inject(HttpClient);
  private readonly secrets = inject(SecretsService);
  private readonly auth = inject(AuthSessionState);
  private generation = 0;
  readonly projectName = input.required<string>();
  readonly environments = signal<readonly SecretEnvironment[]>([]);
  readonly environment = signal('');
  readonly tokens = signal<readonly TelemetryToken[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly name = signal('');
  readonly metrics = signal(true);
  readonly logs = signal(true);
  readonly expiresOn = signal('');
  readonly busy = signal(false);
  readonly created = signal<TelemetryTokenCreated | null>(null);
  readonly copied = signal(false);
  readonly confirming = signal<string | null>(null);
  readonly now = signal(new Date());
  readonly settings = computed(() => {
    const created = this.created();
    return created ? exporterSettings(created) : '';
  });
  readonly canCreate = computed(() => !this.busy() && !!this.environment() && (this.metrics() || this.logs()));
  readonly deployCreator = DEPLOY_TOKEN_CREATOR;
  readonly state = (token: TelemetryToken) => tokenState(token, this.now());

  readonly canWrite = () => {
    const status = this.auth.status();
    return status?.profile === 'local' || ['owner', 'operator'].includes(status?.user?.role ?? '');
  };

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      // Only the project drives this effect; loading tokens reads other signals.
      const subscription = untracked(() => {
        this.environments.set([]);
        this.selectEnvironment('');
        return this.secrets.environments(project).subscribe(items => {
          const declared = items.filter(item => item.id !== 'preview');
          this.environments.set(declared);
          this.selectEnvironment(declared[0]?.id ?? '');
        });
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  selectEnvironment(id: string): void {
    this.environment.set(id);
    this.created.set(null);
    this.copied.set(false);
    this.confirming.set(null);
    this.load();
  }

  load(): void {
    const generation = ++this.generation;
    const environment = this.environment();
    this.tokens.set(null);
    this.error.set(null);
    if (!environment) return;
    this.http.get<TelemetryToken[]>(this.path(environment)).pipe(take(1)).subscribe({
      next: tokens => {
        if (generation !== this.generation) return;
        this.now.set(new Date());
        this.tokens.set([...tokens].reverse());
      },
      error: error => { if (generation === this.generation) this.error.set(this.message(error)); },
    });
  }

  create(): void {
    if (!this.canCreate()) return;
    const environment = this.environment();
    const signals = [this.metrics() ? 'metrics' : null, this.logs() ? 'logs' : null].filter(item => item !== null);
    const expiresAt = this.expiresOn() ? new Date(`${this.expiresOn()}T23:59:59Z`).toISOString() : null;
    this.busy.set(true);
    this.error.set(null);
    this.http.post<TelemetryTokenCreated>(this.path(environment), { name: this.name().trim() || null, signals, expiresAt })
      .pipe(take(1)).subscribe({
        next: created => {
          this.busy.set(false);
          if (environment !== this.environment()) return;
          this.created.set(created);
          this.copied.set(false);
          this.name.set('');
          this.expiresOn.set('');
          this.load();
        },
        error: error => { this.busy.set(false); this.error.set(this.message(error)); },
      });
  }

  async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.settings());
      this.copied.set(true);
    } catch {
      this.error.set('The browser did not allow copying. Select the text and copy it.');
    }
  }

  dismiss(): void {
    this.created.set(null);
    this.copied.set(false);
  }

  revoke(token: TelemetryToken): void {
    if (this.confirming() !== token.id) { this.confirming.set(token.id); return; }
    const environment = this.environment();
    this.busy.set(true);
    this.http.delete(`${this.path(environment)}/${encodeURIComponent(token.id)}`).pipe(take(1)).subscribe({
      next: () => { this.busy.set(false); this.confirming.set(null); this.load(); },
      error: error => { this.busy.set(false); this.confirming.set(null); this.error.set(this.message(error)); },
    });
  }

  private path(environment: string): string {
    return `/api/projects/${encodeURIComponent(this.projectName())}/environments/${encodeURIComponent(environment)}/telemetry/tokens`;
  }

  private message(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 404) return 'This environment is not declared, so it cannot have ingest tokens.';
      return error.error?.message ?? `Request failed (${error.status}).`;
    }
    return 'Request failed.';
  }
}
