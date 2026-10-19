import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { Observable } from 'rxjs';
import { AuthSessionState } from '../../../services/auth.service';
import type { WatchPathEntry } from '../../../models/task.model';
import { SecretsService, type SecretMetadata, type SecretScopeKey } from '../secrets.service';

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

  readonly nameExists = computed(() => this.rows().some(row => row.name === this.newName().trim()));

  constructor() { this.service.load(); }

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
