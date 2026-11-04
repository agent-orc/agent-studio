import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { AuthSessionState } from '../../../../services/auth.service';
import { QualityStudioService } from '../../../../services/quality-studio.service';

interface Connection { internalBaseUrl: string; publicUrl: string; serviceClientId: string; }

@Component({
  selector: 'app-quality-studio-connection-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quality-studio-connection-settings.component.html',
  styleUrl: './quality-studio-connection-settings.component.scss',
})
export class QualityStudioConnectionSettingsComponent {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  private readonly quality = inject(QualityStudioService);
  readonly owner = computed(() => this.auth.status()?.user?.role === 'owner');
  readonly internalBaseUrl = signal('');
  readonly publicUrl = signal('');
  readonly serviceClientId = signal('agent-studio');
  readonly token = signal('');
  readonly saving = signal(false);
  readonly message = signal<string | null>(null);
  private loaded = false;

  constructor() {
    effect(() => {
      if (!this.owner() || this.loaded) return;
      this.loaded = true;
      this.http.get<Connection | null>('/api/integrations/quality-studio/connection').subscribe({
        next: connection => {
          if (!connection) return;
          this.internalBaseUrl.set(connection.internalBaseUrl);
          this.publicUrl.set(connection.publicUrl);
          this.serviceClientId.set(connection.serviceClientId);
        },
        error: () => this.message.set('Could not load the Quality Studio connection.'),
      });
    });
  }

  save(): void {
    this.saving.set(true);
    this.message.set(null);
    this.http.put<Connection>('/api/integrations/quality-studio/connection', {
      internalBaseUrl: this.internalBaseUrl().trim(), publicUrl: this.publicUrl().trim(),
      serviceClientId: this.serviceClientId().trim(),
    }).subscribe({
      next: () => {
        const token = this.token();
        if (!token) { this.saving.set(false); this.message.set('Connection saved.'); this.quality.connectionChanged(); return; }
        this.http.post<void>('/api/integrations/quality-studio/token', { token }).subscribe({
          next: () => { this.token.set(''); this.saving.set(false); this.message.set('Connection and service token saved.'); this.quality.connectionChanged(); },
          error: () => { this.saving.set(false); this.message.set('Connection saved, but the service token could not be saved.'); },
        });
      },
      error: () => { this.saving.set(false); this.message.set('Could not save the Quality Studio connection.'); },
    });
  }
}
