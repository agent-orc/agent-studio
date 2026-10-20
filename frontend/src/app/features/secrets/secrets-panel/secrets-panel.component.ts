import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { Observable } from 'rxjs';
import { AuthSessionState } from '../../../services/auth.service';
import type { WatchPathEntry } from '../../../models/task.model';
import { SecretsService, type SecretAdapterConfig, type SecretMetadata, type SecretScopeKey } from '../secrets.service';

/**
 * Secrets section of the Access area in Settings. People pick a scope
 * (workspace, project, or project environment), add a secret, rotate it, or
 * revoke versions. Values are write-only: the inputs clear after each save
 * and the API never returns a value to the browser.
 */
@Component({
  selector: 'app-secrets-panel',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './secrets-panel.component.html',
  styleUrl: './secrets-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SecretsPanelComponent {
  readonly service = inject(SecretsService);
  private readonly auth = inject(AuthSessionState, { optional: true });
  private readonly destroyRef = inject(DestroyRef);

  readonly projects = input<readonly WatchPathEntry[]>([]);
  readonly project = signal('');
  readonly environment = signal('');
  readonly environmentOptions = signal<readonly string[]>([]);
  readonly newName = signal('');
  readonly newValue = signal('');
  readonly rotating = signal<string | null>(null);
  readonly rotateValue = signal('');
  readonly busy = signal(false);
  readonly adapterKind = signal<SecretAdapterConfig['kind']>('builtin');
  readonly adapterAddress = signal('');
  readonly adapterMount = signal('secret');
  readonly adapterVault = signal('');
  readonly adapterCredentialEnvironment = signal('');
  readonly adapterTtl = signal(30);
  readonly adapterBusy = signal(false);

  readonly projectNames = computed(() =>
    [...new Set(this.projects().map(entry => entry.name))].sort((a, b) => a.localeCompare(b)));

  readonly scope = computed<SecretScopeKey>(() => {
    const project = this.project();
    if (!project) return 'workspace';
    return this.environment() ? `${project}/${this.environment()}` : project;
  });

  readonly rows = computed<readonly SecretMetadata[]>(() => {
    const scope = this.scope().toLowerCase();
    return (this.service.overview()?.secrets ?? [])
      .filter(item => item.scope.toLowerCase() === scope)
      .slice()
      .sort((a, b) => a.name.localeCompare(b.name));
  });

  /** Viewers and the Product role see metadata only; the backend enforces the same rule. */
  readonly canWrite = computed(() => {
    const role = this.auth?.status()?.user?.role;
    if (role === 'viewer' || role === 'contributor') return false;
    return this.scope() !== 'workspace' || (this.service.overview()?.canWriteWorkspace ?? false);
  });
  readonly canConfigureAdapter = computed(() => {
    const role = this.auth?.status()?.user?.role;
    return !role || role === 'owner' || role === 'operator';
  });

  readonly nameExists = computed(() => this.rows().some(row => row.name === this.newName().trim()));

  /** Items the store could not open, across every scope: a store-health warning, not a scope row. */
  readonly unreadable = computed(() => this.service.overview()?.unreadable ?? []);

  constructor() {
    this.service.load();
    effect(() => this.service.loadAdapter(this.scope()));
    effect(() => {
      const selection = this.service.adapter();
      if (!selection || selection.scope !== this.scope()) return;
      const config = selection.configuration;
      this.adapterKind.set(config?.kind ?? 'builtin');
      this.adapterAddress.set(config?.address ?? '');
      this.adapterMount.set(config?.mount ?? 'secret');
      this.adapterVault.set(config?.vault ?? '');
      this.adapterCredentialEnvironment.set(config?.credentialEnvironment ?? '');
      this.adapterTtl.set(config?.cacheTtlSeconds ?? 30);
    });
  }

  saveAdapter(): void {
    if (this.adapterBusy()) return;
    const configuration: SecretAdapterConfig = {
      kind: this.adapterKind(), address: this.adapterAddress().trim() || null,
      mount: this.adapterMount().trim() || null, vault: this.adapterVault().trim() || null,
      credentialEnvironment: this.adapterCredentialEnvironment().trim(), cacheTtlSeconds: this.adapterTtl(),
    };
    this.adapterBusy.set(true);
    this.service.setAdapter(this.scope(), configuration).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.adapterBusy.set(false); this.service.load(); },
      error: () => this.adapterBusy.set(false),
    });
  }

  selectProject(project: string): void {
    this.project.set(project);
    this.environment.set('');
    this.environmentOptions.set([]);
    if (!project) return;
    this.service.environments(project).pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(options => this.environmentOptions.set(options));
  }

  add(): void {
    const name = this.newName().trim();
    const value = this.newValue();
    if (!name || !value || this.busy()) return;
    this.run(this.service.add(this.scope(), name, value), () => {
      this.newName.set('');
      this.newValue.set('');
    });
  }

  startRotate(name: string): void {
    this.rotating.set(name);
    this.rotateValue.set('');
  }

  cancelRotate(): void {
    this.rotating.set(null);
    this.rotateValue.set('');
  }

  rotate(name: string): void {
    const value = this.rotateValue();
    if (!value || this.busy()) return;
    this.run(this.service.rotate(this.scope(), name, value), () => this.cancelRotate());
  }

  revoke(name: string, version: number | null): void {
    if (this.busy()) return;
    this.run(this.service.revoke(this.scope(), name, version), () => undefined);
  }

  private run(request: Observable<SecretMetadata>, done: () => void): void {
    this.busy.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        done();
        this.busy.set(false);
      },
      error: () => this.busy.set(false),
    });
  }
}
