import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { AuthSessionState } from '../../../../services/auth.service';

interface UnpricedModelRow { model: string; calls: number; totalTokens: number }
interface UnpricedModelsResponse { models: UnpricedModelRow[] }

/** Owner diagnostic for active models absent from the pinned price catalogue. */
@Component({
  selector: 'app-unpriced-models',
  standalone: true,
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './unpriced-models.component.html',
  styleUrl: './unpriced-models.component.scss',
})
export class UnpricedModelsComponent {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthSessionState);
  readonly owner = () => this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner';
  readonly models = signal<UnpricedModelRow[] | null>(null);
  readonly error = signal(false);

  load(): void {
    this.http.get<UnpricedModelsResponse>('/api/token-pricing/unpriced-models').subscribe({
      next: response => { this.models.set(response.models); this.error.set(false); },
      error: () => this.error.set(true),
    });
  }
}
