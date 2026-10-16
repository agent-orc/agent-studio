import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

export interface CredentialInventoryItem {
  id: string;
  category: 'provider' | 'repository' | 'service' | 'human';
  hostId: string;
  kind: string;
  label: string;
  scopes: readonly string[];
  repository: string | null;
  permission: string | null;
  expiresAt: string | null;
  lastUseAt: string | null;
  state: 'ok' | 'expiring' | 'missing' | 'unused' | 'unexpected';
  required: boolean;
  detail: string;
  affectedCards: readonly string[];
}

export interface CredentialInventory {
  generatedAt: string;
  items: readonly CredentialInventoryItem[];
}

@Injectable({ providedIn: 'root' })
export class CredentialInventoryService {
  private readonly http = inject(HttpClient, { optional: true });
  readonly inventory = signal<CredentialInventory | null>(null);
  readonly state = signal<'idle' | 'loading' | 'ready' | 'unavailable'>('idle');

  ensureLoaded(): void {
    if (this.state() === 'idle') this.load();
  }

  load(): void {
    if (this.state() === 'loading') return;
    if (!this.http) {
      this.state.set('unavailable');
      return;
    }
    this.state.set('loading');
    this.http.get<CredentialInventory>('/api/v1/management/credential-inventory').subscribe({
      next: inventory => {
        this.inventory.set(inventory);
        this.state.set('ready');
      },
      error: () => {
        this.inventory.set(null);
        this.state.set('unavailable');
      },
    });
  }
}
