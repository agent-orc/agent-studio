import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { AuthSessionState } from '../../../services/auth.service';
import { SecretsService, type SecretEnvironment } from '../secrets.service';
import { SecretsPanelComponent } from '../secrets-panel/secrets-panel.component';

interface Binding { name: string; reference: string; approvedBy: string | null }
interface Missing { name: string; reference: string; scope: string; reason: string }
interface Preflight { project: string; environment: string; bindings: Binding[]; missing: Missing[]; ready: boolean }
interface Readiness { environments: { environmentId: string; ready: boolean }[] }

/** Reasons a binding resolves; the others need a stored secret, not a binding. */
const BINDABLE = new Set(['not-bound', 'approval-required']);

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
  /** F14: the environment a Deploy Bind action asked for; without it the page opens on one that needs attention. */
  readonly initialEnvironment = input<string | null>(null);
  /** Set once the operator picks an environment, so a late attention answer never moves the panel. */
  private chosen = false;
  readonly environment = signal('preview');
  readonly environments = signal<readonly SecretEnvironment[]>([{ id: 'preview', kind: 'preview' }]);
  readonly isProduction = computed(() => this.environments().some(item =>
    item.id === this.environment() && item.kind === 'production'));
  private readonly preflight = signal<{ project: string; environment: string; result: Preflight } | null>(null);
  private preflightRequest = 0;
  readonly check = computed(() => {
    const preflight = this.preflight();
    return preflight?.project === this.projectName() && preflight.environment === this.environment()
      ? preflight.result : null;
  });
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
    effect(onCleanup => {
      const project = this.projectName();
      this.chosen = false;
      // Created first: an environments answer may arrive synchronously and attach the attention probe.
      const subscription = new Subscription();
      subscription.add(this.secrets.environments(project).subscribe(items => {
        const environments = [{ id: 'preview', kind: 'preview' }, ...items.filter(item => item.id !== 'preview')];
        this.environments.set(environments);
        untracked(() => this.openOnAttention(project, environments, subscription));
      }));
      onCleanup(() => subscription.unsubscribe());
    });
    effect(() => this.refresh(this.projectName(), this.environment()));
  }

  selectEnvironment(value: string, byOperator = true): void {
    if (byOperator) this.chosen = true;
    ++this.preflightRequest;
    this.preflight.set(null);
    this.error.set(null);
    this.environment.set(value);
    this.name.set('');
    this.reference.set('');
  }

  /** Fills the form, for binding a listed name to another reference. */
  selectMissing(item: Missing): void {
    this.name.set(item.name);
    this.reference.set(item.reference);
  }

  canBind(item: Missing): boolean { return BINDABLE.has(item.reason); }

  bindLabel(item: Missing): string {
    if (item.reason === 'approval-required') return `Approve ${item.name} binding`;
    return this.isProduction() ? `Bind and approve ${item.name}` : `Bind ${item.name}`;
  }

  /** F14: one action binds a listed reference; on Production it approves the binding too. */
  bindMissing(item: Missing): void {
    this.save(item.name, item.reference, this.isProduction());
  }

  refresh(project = this.projectName(), environment = this.environment()): void {
    const request = ++this.preflightRequest;
    this.preflight.set(null);
    this.error.set(null);
    this.http.get<Preflight>(this.path(project, environment)).subscribe({
      next: check => {
        if (request !== this.preflightRequest || project !== this.projectName() || environment !== this.environment()) return;
        this.preflight.set({ project, environment, result: check });
      },
      error: error => {
        if (request !== this.preflightRequest || project !== this.projectName() || environment !== this.environment()) return;
        this.error.set(this.message(error));
      },
    });
  }

  bind(): void {
    this.save(this.name().trim(), this.reference().trim(), this.approve());
  }

  private save(name: string, reference: string, approve: boolean): void {
    if (!name || !reference || this.saving()) return;
    this.saving.set(true);
    const environment = this.environment();
    this.http.put<Binding>(`${this.path(this.projectName(), environment)}/${encodeURIComponent(name)}`,
      { reference, approve }).subscribe({
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

  /**
   * Opens the environment a Bind action named, else the first lifecycle
   * environment whose next deploy misses a secret. Preview stays selected
   * when every environment is ready or the check is unavailable.
   */
  private openOnAttention(project: string, environments: readonly SecretEnvironment[], owner: Subscription): void {
    if (this.chosen) return;
    const known = (id: string | null | undefined): id is string => !!id && environments.some(item => item.id === id);
    const wanted = this.initialEnvironment();
    if (known(wanted)) {
      if (wanted !== this.environment()) this.selectEnvironment(wanted, false);
      return;
    }
    if (environments.length < 2) return;
    const probe = this.http.get<Readiness>(`/api/projects/${encodeURIComponent(project)}/deploy/secret-readiness`).subscribe({
      next: readiness => {
        if (this.chosen || project !== this.projectName()) return;
        const target = readiness.environments.find(item => !item.ready && known(item.environmentId))?.environmentId;
        if (target && target !== this.environment()) this.selectEnvironment(target, false);
      },
      error: () => undefined,
    });
    owner.add(probe);
  }

  private path(project: string, environment: string): string {
    return `/api/projects/${encodeURIComponent(project)}/secret-bindings/${encodeURIComponent(environment)}`;
  }

  private message(error: unknown): string {
    if (error instanceof HttpErrorResponse) return error.error?.message ?? `Request failed (${error.status}).`;
    return 'Request failed.';
  }
}
