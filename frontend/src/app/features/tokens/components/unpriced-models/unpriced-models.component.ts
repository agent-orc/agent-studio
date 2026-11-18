import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { AuthSessionState } from '../../../../services/auth.service';

interface UnpricedModelRow { model: string; calls: number; totalTokens: number }
interface UnpricedModelsResponse { models: UnpricedModelRow[]; weekStartUtc: string; weekEndUtc: string; timeZone: string }

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
  readonly workspaceId = input<string | null>(null);
  readonly owner = () => this.auth.status()?.profile === 'local' || this.auth.status()?.user?.role === 'owner';
  readonly models = signal<UnpricedModelRow[] | null>(null);
  readonly period = signal<string | null>(null);
  readonly error = signal(false);

  load(): void {
    const url = this.workspaceId()
      ? `/api/token-pricing/unpriced-models?workspaceId=${encodeURIComponent(this.workspaceId()!)}`
      : '/api/token-pricing/unpriced-models';
    this.http.get<UnpricedModelsResponse>(url).subscribe({
      next: response => {
        this.models.set(response.models);
        const date = (value: string) => new Intl.DateTimeFormat('en', {
          timeZone: response.timeZone, year: 'numeric', month: 'short', day: 'numeric',
        }).format(new Date(value));
        this.period.set(`${date(response.weekStartUtc)} to ${date(new Date(new Date(response.weekEndUtc).getTime() - 1).toISOString())} (${response.timeZone})`);
        this.error.set(false);
      },
      error: () => this.error.set(true),
    });
  }
}
