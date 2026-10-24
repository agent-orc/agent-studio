import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { SecretsService, type SecretBackendBinding } from '../secrets.service';

/** Scope-specific storage selection. Credentials remain in the built-in store or process environment. */
@Component({
  selector: 'app-secret-backend-settings',
  standalone: true,
  templateUrl: './secret-backend-settings.component.html',
  styleUrl: './secret-backend-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SecretBackendSettingsComponent {
  private readonly service = inject(SecretsService);
  readonly scope = input.required<string>();
  readonly canWrite = input(false);
  readonly kind = signal<SecretBackendBinding['kind']>('builtin');
  readonly url = signal('');
  readonly mount = signal('');
  readonly vaultId = signal('');
  readonly credentialReference = signal('');
  readonly credentialEnvironmentVariable = signal('');
  readonly ttlSeconds = signal(30);
  readonly busy = signal(false);
  readonly notice = signal<string | null>(null);
  readonly health = computed(() => this.service.overview()?.backendHealth?.find(item => item.scope === this.scope()));

  constructor() {
    effect(() => {
      if (this.scope()) this.notice.set(null);
    });
    effect(() => {
      const binding = this.service.overview()?.backends?.find(item => item.scope === this.scope());
      this.kind.set(binding?.kind ?? 'builtin');
      this.url.set(binding?.url ?? '');
      this.mount.set(binding?.mount ?? '');
      this.vaultId.set(binding?.vaultId ?? '');
      this.credentialReference.set(binding?.credentialReference ?? '');
      this.credentialEnvironmentVariable.set(binding?.credentialEnvironmentVariable ?? '');
      this.ttlSeconds.set(binding?.ttlSeconds ?? 30);
    });
  }

  save(): void {
    if (!this.canWrite() || this.busy()) return;
    const scope = this.scope();
    this.busy.set(true);
    this.notice.set(null);
    this.service.selectBackend({
      scope, kind: this.kind(), url: this.url().trim(),
      mount: this.mount().trim() || null, vaultId: this.vaultId().trim() || null,
      credentialReference: this.credentialReference().trim() || null,
      credentialEnvironmentVariable: this.credentialEnvironmentVariable().trim() || null,
      ttlSeconds: this.ttlSeconds(),
    }).subscribe({
      next: () => { this.busy.set(false); if (this.scope() === scope) this.notice.set('Backend selection saved.'); },
      error: () => this.busy.set(false),
    });
  }
}
